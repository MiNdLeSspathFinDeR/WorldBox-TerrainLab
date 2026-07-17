from typing import Any, Dict, Optional, Tuple

import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import time
import uuid

from PIL import Image as Img

from .models import Map

SAVE_SLOT_RE = re.compile(r"save(\d+)$")


def worldbox_saves_candidates() -> Tuple[Path, ...]:
    """Return common WorldBox save directories for desktop platforms."""
    home = Path.home()
    candidates = []

    user_profile = os.environ.get("USERPROFILE")
    if user_profile:
        candidates.append(
            Path(user_profile)
            / "AppData"
            / "LocalLow"
            / "mkarpenko"
            / "WorldBox"
            / "saves"
        )

    candidates.extend(
        (
            home / "AppData" / "LocalLow" / "mkarpenko" / "WorldBox" / "saves",
            home / ".config" / "unity3d" / "mkarpenko" / "WorldBox" / "saves",
            home
            / "Library"
            / "Application Support"
            / "mkarpenko"
            / "WorldBox"
            / "saves",
        )
    )

    unique = []
    seen = set()
    for candidate in candidates:
        if candidate not in seen:
            unique.append(candidate)
            seen.add(candidate)

    return tuple(unique)


def find_worldbox_saves_dir() -> Optional[Path]:
    for candidate in worldbox_saves_candidates():
        if candidate.exists() and candidate.is_dir():
            return candidate

    return None


def resolve_worldbox_saves_dir(path: Optional[Path] = None) -> Path:
    if path is not None:
        return path.expanduser()

    saves_dir = find_worldbox_saves_dir()
    if saves_dir is None:
        candidates = ", ".join(str(path) for path in worldbox_saves_candidates())
        raise FileNotFoundError(
            "Could not find WorldBox saves directory. "
            f"Use --game-saves-dir. Checked: {candidates}"
        )

    return saves_dir


def next_worldbox_save_slot(saves_dir: Path) -> Path:
    last_slot = 0

    if saves_dir.exists():
        for child in saves_dir.iterdir():
            if not child.is_dir():
                continue

            match = SAVE_SLOT_RE.fullmatch(child.name)
            if match is not None:
                last_slot = max(last_slot, int(match.group(1)))

    for index in range(1, last_slot + 2):
        candidate = saves_dir / f"save{index}"
        if not candidate.exists():
            return candidate

    return saves_dir / f"save{last_slot + 1}"


def find_worldbox_stats_template(saves_dir: Path) -> Optional[Path]:
    if not saves_dir.exists():
        return None

    for child in sorted(saves_dir.iterdir()):
        stats_path = child / "map_stats.s3db"
        if stats_path.is_file():
            return stats_path

    return None


def create_stats_db(target_path: Path, template_path: Optional[Path] = None) -> None:
    if target_path.exists():
        target_path.unlink()

    if template_path is None:
        sqlite3.connect(target_path).close()
        return

    template_connection = sqlite3.connect(template_path)
    try:
        schema_rows = template_connection.execute(
            """
            SELECT type, name, sql
            FROM sqlite_master
            WHERE sql IS NOT NULL
              AND type IN ('table', 'index')
            ORDER BY CASE type WHEN 'table' THEN 0 ELSE 1 END, name
            """
        ).fetchall()
    finally:
        template_connection.close()

    target_connection = sqlite3.connect(target_path)
    try:
        for object_type, name, sql in schema_rows:
            if object_type == "index" and name.startswith("sqlite_autoindex_"):
                continue
            target_connection.execute(sql)
        target_connection.commit()
    finally:
        target_connection.close()


def make_map_meta(
    converted_map: Map,
    name: str,
    timestamp: Optional[float] = None,
) -> Dict[str, Any]:
    timestamp = time.time() if timestamp is None else timestamp

    return {
        "saveVersion": 17,
        "width": converted_map.width,
        "height": converted_map.height,
        "mapStats": {
            "name": name,
            "description": "",
            "player_name": "ImageToMap",
            "player_mood": "serene",
            "custom_data": {},
            "world_time": 0.0,
            "history_current_year": 0,
            "world_age_id": "age_hope",
            "world_age_started_at": 0.0,
            "same_world_age_started_at": 0.0,
            "current_world_ages_duration": 3120.0,
            "current_age_progress": 0.0,
            "world_ages_slots": [None, None, None, None, None, None, None, None],
        },
        "cities": 0,
        "units": 0,
        "population": 0,
        "structures": 0,
        "mobs": 0,
        "vegetation": 0,
        "deaths": 0,
        "kingdoms": 0,
        "buildings": 0,
        "equipment": 0,
        "books": 0,
        "wars": 0,
        "alliances": 0,
        "families": 0,
        "clans": 0,
        "cultures": 0,
        "religions": 0,
        "languages": 0,
        "subspecies": 0,
        "favorites": 0,
        "modsActive": [],
        "timestamp": timestamp,
    }


def write_map_folder(
    output_path: Path,
    converted_map: Map,
    name: str,
    include_game_files: bool = False,
    stats_template: Optional[Path] = None,
) -> None:
    output_path.mkdir(exist_ok=True, parents=True)
    (output_path / "map.wbox").write_bytes(converted_map.data)

    preview = converted_map.preview.convert("RGBA")
    try:
        preview.save(output_path / "preview.png", optimize=True)

        if include_game_files:
            preview_small = preview.resize((32, 32), resample=Img.Resampling.NEAREST)
            try:
                preview_small.save(output_path / "preview_small.png", optimize=True)
            finally:
                preview_small.close()
    finally:
        preview.close()

    if include_game_files:
        meta = make_map_meta(converted_map=converted_map, name=name)
        (output_path / "map.meta").write_bytes(
            json.dumps(meta, separators=(",", ":")).encode("utf-8")
        )
        create_stats_db(output_path / "map_stats.s3db", stats_template)


def write_game_save_atomically(
    output_path: Path,
    converted_map: Map,
    name: str,
    stats_template: Optional[Path] = None,
) -> None:
    """Publish a complete WorldBox save directory in one filesystem rename."""
    output_path.parent.mkdir(exist_ok=True, parents=True)
    if output_path.exists():
        raise FileExistsError(f"save slot already exists: {output_path}")

    temporary_path = output_path.parent / (
        f"terrainlab-staging-{output_path.name}-{uuid.uuid4().hex}.tmp"
    )
    try:
        write_map_folder(
            output_path=temporary_path,
            converted_map=converted_map,
            name=name,
            include_game_files=True,
            stats_template=stats_template,
        )
        temporary_path.rename(output_path)
    finally:
        if temporary_path.exists():
            shutil.rmtree(temporary_path)
