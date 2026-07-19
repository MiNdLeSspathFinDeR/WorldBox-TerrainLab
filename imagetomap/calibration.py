from dataclasses import dataclass, replace
import json
import math
from pathlib import Path
import struct
from typing import Any, Dict, Mapping, Optional, Sequence, Tuple

import numpy as np
from PIL import Image as Img
from PIL import ImageDraw
from PIL.Image import Image

from .consts import (
    CHUNK_SIZE,
    ELEVATION_MAXIMUM,
    ELEVATION_MINIMUM,
    ELEVATION_NODATA,
    MAX_MAP_CELLS,
)
from .georeference import (
    RasterGeoreference,
    build_geo_key_directory,
    write_raster_georeference_sidecars,
)
from .terrain import (
    gradient_magnitude,
    living_soil_index,
    local_roughness,
    make_index_image,
    normalize,
    percentiles,
    rgb_to_hsv,
    smooth_float,
)


CLASSIFICATION_PROFILE_SUFFIX = ".terrainlab-classification.json"
GENERATED_ELEVATION_FILE_NAME = "terrainlab-elevation.tif"
GENERATED_PROFILE_FILE_NAME = "terrainlab-classification.json"
MAXIMUM_PROFILE_BYTES = 4 * 1024 * 1024
MAXIMUM_SAMPLES = 512
MAXIMUM_REGIONS = 128
MAXIMUM_LINES = 128
MAXIMUM_REGION_VERTICES = 256
MAXIMUM_TOTAL_REGION_VERTICES = 8192
MAXIMUM_LINE_WIDTH_CELLS = 32
MAXIMUM_EFFECTIVE_SAMPLES = 512
MAXIMUM_REGION_TRAINING_SAMPLES = 32

SURFACE_IDS = (
    "deep_ocean",
    "shelf",
    "shallow_water",
    "river_lake",
    "sand",
    "plain",
    "lowland",
    "upland",
    "hills",
    "rocks",
    "summit",
    "depression",
)
BIOTOPE_IDS = (
    "auto",
    "none",
    "grass",
    "jungle",
    "savanna",
    "desert",
    "permafrost",
    "swamp",
    "enchanted",
    "lemon",
    "crystal",
    "corrupted",
    "infernal",
    "candy",
    "mushroom",
    "wasteland",
    "birch",
    "maple",
    "rocklands",
    "garlic",
    "flower",
    "celestial",
    "clover",
    "singularity",
    "paradox",
)

_SURFACE_TILE = {
    "deep_ocean": ("deep_ocean", "close_ocean", "shallow_waters"),
    "shelf": ("close_ocean", "shallow_waters", "deep_ocean"),
    "shallow_water": ("shallow_waters", "close_ocean", "deep_ocean"),
    "river_lake": ("shallow_waters", "close_ocean", "deep_ocean"),
    "sand": ("sand", "soil_low"),
    "plain": ("soil_low",),
    "lowland": ("soil_low",),
    "upland": ("soil_high", "soil_low"),
    "hills": ("hills", "soil_high", "mountains"),
    "rocks": ("mountains", "hills", "soil_high"),
    "summit": ("mountains", "hills", "soil_high"),
    "depression": ("soil_low", "sand"),
}
_BIOTOPE_SUFFIX = {
    "grass": "grass",
    "jungle": "jungle",
    "savanna": "savanna",
    "desert": "desert",
    "permafrost": "permafrost",
    "swamp": "swamp",
    "enchanted": "enchanted",
    "lemon": "lemon",
    "crystal": "crystal",
    "corrupted": "corrupted",
    "infernal": "infernal",
    "candy": "candy",
    "mushroom": "mushroom",
    "wasteland": "wasteland",
    "birch": "birch",
    "maple": "maple",
    "rocklands": "rocklands",
    "garlic": "garlic",
    "flower": "flower",
    "celestial": "celestial",
    "clover": "clover",
    "singularity": "singularity",
    "paradox": "paradox",
}


@dataclass(frozen=True)
class ClassificationSample:
    x: int
    y: int
    surface: str
    biotope: str
    elevation: int


@dataclass(frozen=True)
class ClassificationRegion:
    vertices: Tuple[Tuple[int, int], ...]
    surface: str
    biotope: str
    elevation: int
    vertex_elevations: Tuple[int, ...] = ()

    def __post_init__(self) -> None:
        if not self.vertex_elevations:
            object.__setattr__(
                self,
                "vertex_elevations",
                (self.elevation,) * len(self.vertices),
            )
        elif len(self.vertex_elevations) != len(self.vertices):
            raise ValueError(
                "region vertex elevations must match its vertices"
            )


@dataclass(frozen=True)
class ClassificationLine:
    vertices: Tuple[Tuple[int, int], ...]
    surface: str
    biotope: str
    elevation: int
    width_cells: int = 1
    vertex_elevations: Tuple[int, ...] = ()

    def __post_init__(self) -> None:
        if not self.vertex_elevations:
            object.__setattr__(
                self,
                "vertex_elevations",
                (self.elevation,) * len(self.vertices),
            )
        elif len(self.vertex_elevations) != len(self.vertices):
            raise ValueError(
                "line vertex elevations must match its vertices"
            )


@dataclass(frozen=True)
class ClassificationProfile:
    source_file_name: str
    source_width: int
    source_height: int
    samples: Tuple[ClassificationSample, ...]
    long_side_blocks: int = 20
    color_weight: float = 0.55
    texture_weight: float = 0.20
    spatial_weight: float = 0.25
    appearance_tolerance: float = 0.65
    local_influence: float = 0.08
    elevation_power: float = 2.0
    elevation_smoothing: int = 1
    interpolate_elevation_globally: bool = True
    regions: Tuple[ClassificationRegion, ...] = ()
    lines: Tuple[ClassificationLine, ...] = ()
    map_boundary: Tuple[Tuple[int, int], ...] = ()
    outside_surface: str = "deep_ocean"
    outside_biotope: str = "none"
    outside_elevation: int = -4000

    def to_json_dict(self) -> Dict[str, Any]:
        payload = {
            "schema_version": 3,
            "source": {
                "file_name": self.source_file_name,
                "width": self.source_width,
                "height": self.source_height,
            },
            "settings": {
                "long_side_blocks": self.long_side_blocks,
                "color_weight": self.color_weight,
                "texture_weight": self.texture_weight,
                "spatial_weight": self.spatial_weight,
                "appearance_tolerance": self.appearance_tolerance,
                "local_influence": self.local_influence,
                "elevation_power": self.elevation_power,
                "elevation_smoothing": self.elevation_smoothing,
                "interpolate_elevation_globally": (
                    self.interpolate_elevation_globally
                ),
            },
            "samples": [
                {
                    "x": sample.x,
                    "y": sample.y,
                    "surface": sample.surface,
                    "biotope": sample.biotope,
                    "elevation": sample.elevation,
                }
                for sample in self.samples
            ],
            "regions": [
                {
                    "vertices": [
                        {
                            "x": vertex[0],
                            "y": vertex[1],
                            "elevation": (
                                region.vertex_elevations[index]
                                if index < len(region.vertex_elevations)
                                else region.elevation
                            ),
                        }
                        for index, vertex in enumerate(region.vertices)
                    ],
                    "surface": region.surface,
                    "biotope": region.biotope,
                    "elevation": region.elevation,
                }
                for region in self.regions
            ],
            "lines": [
                {
                    "vertices": [
                        {
                            "x": vertex[0],
                            "y": vertex[1],
                            "elevation": (
                                line.vertex_elevations[index]
                                if index < len(line.vertex_elevations)
                                else line.elevation
                            ),
                        }
                        for index, vertex in enumerate(line.vertices)
                    ],
                    "surface": line.surface,
                    "biotope": line.biotope,
                    "elevation": line.elevation,
                    "width_cells": line.width_cells,
                }
                for line in self.lines
            ],
        }
        if self.map_boundary:
            payload["map_boundary"] = {
                "vertices": [
                    {"x": vertex[0], "y": vertex[1]}
                    for vertex in self.map_boundary
                ],
                "outside_surface": self.outside_surface,
                "outside_biotope": self.outside_biotope,
                "outside_elevation": self.outside_elevation,
            }
        return payload


def classification_processing_extent(
    profile: ClassificationProfile,
) -> Tuple[int, int, int, int]:
    """Return the inclusive-boundary crop as a PIL-style source box."""
    if not profile.map_boundary:
        return (0, 0, profile.source_width, profile.source_height)
    x_values = [vertex[0] for vertex in profile.map_boundary]
    y_values = [vertex[1] for vertex in profile.map_boundary]
    left = max(0, min(x_values))
    top = max(0, min(y_values))
    right = min(profile.source_width, max(x_values) + 1)
    bottom = min(profile.source_height, max(y_values) + 1)
    if right <= left or bottom <= top:
        raise ValueError("classification map boundary has an empty extent")
    return (left, top, right, bottom)


def crop_classification_profile(
    profile: ClassificationProfile,
    extent: Tuple[int, int, int, int],
) -> ClassificationProfile:
    """Shift a source-space profile into a cropped processing extent."""
    left, top, right, bottom = extent
    width = right - left
    height = bottom - top
    if (
        left < 0
        or top < 0
        or right > profile.source_width
        or bottom > profile.source_height
        or width <= 0
        or height <= 0
    ):
        raise ValueError("classification crop is outside the source image")

    def contains(point: Tuple[int, int]) -> bool:
        return (
            left <= point[0] < right
            and top <= point[1] < bottom
        )

    def shift(point: Tuple[int, int]) -> Tuple[int, int]:
        return point[0] - left, point[1] - top

    samples = tuple(
        replace(sample, x=sample.x - left, y=sample.y - top)
        for sample in profile.samples
        if contains((sample.x, sample.y))
    )
    regions = tuple(
        replace(
            region,
            vertices=tuple(shift(vertex) for vertex in region.vertices),
        )
        for region in profile.regions
        if all(contains(vertex) for vertex in region.vertices)
    )
    lines = tuple(
        replace(
            line,
            vertices=tuple(shift(vertex) for vertex in line.vertices),
        )
        for line in profile.lines
        if all(contains(vertex) for vertex in line.vertices)
    )
    boundary = tuple(shift(vertex) for vertex in profile.map_boundary)
    return replace(
        profile,
        source_width=width,
        source_height=height,
        samples=samples,
        regions=regions,
        lines=lines,
        map_boundary=boundary,
    )


def classification_profile_path(image_path: Path) -> Path:
    return image_path.with_name(image_path.name + CLASSIFICATION_PROFILE_SUFFIX)


def load_classification_profile(
    path: Path,
    source_size: Optional[Tuple[int, int]] = None,
) -> ClassificationProfile:
    path = Path(path)
    if not path.is_file():
        raise FileNotFoundError(f"classification profile was not found: {path}")
    if path.stat().st_size > MAXIMUM_PROFILE_BYTES:
        raise ValueError("classification profile exceeds 4 MiB")

    payload = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(payload, dict) or payload.get("schema_version") not in {
        1,
        2,
        3,
    }:
        raise ValueError(
            "classification profile schema_version must be 1, 2, or 3"
        )

    source = _mapping(payload.get("source"), "source")
    source_file_name = str(source.get("file_name") or "").strip()
    source_width = _integer(source.get("width"), "source.width", 1, 1_000_000)
    source_height = _integer(source.get("height"), "source.height", 1, 1_000_000)
    if source_size is not None and (
        source_width != source_size[0] or source_height != source_size[1]
    ):
        raise ValueError(
            "classification profile source dimensions "
            f"{source_width}x{source_height} do not match image "
            f"{source_size[0]}x{source_size[1]}"
        )

    raw_samples = payload.get("samples", [])
    if not isinstance(raw_samples, list):
        raise ValueError("classification profile samples must be a JSON array")
    if len(raw_samples) > MAXIMUM_SAMPLES:
        raise ValueError(
            f"classification profile has more than {MAXIMUM_SAMPLES} samples"
        )

    samples = []
    seen = set()
    for index, raw_sample in enumerate(raw_samples):
        sample_data = _mapping(raw_sample, f"samples[{index}]")
        x = _integer(sample_data.get("x"), f"samples[{index}].x", 0, source_width - 1)
        y = _integer(
            sample_data.get("y"),
            f"samples[{index}].y",
            0,
            source_height - 1,
        )
        surface = str(sample_data.get("surface") or "").strip().lower()
        biotope = str(sample_data.get("biotope") or "auto").strip().lower()
        elevation = _integer(
            sample_data.get("elevation"),
            f"samples[{index}].elevation",
            ELEVATION_MINIMUM,
            ELEVATION_NODATA,
        )
        if elevation == ELEVATION_NODATA:
            raise ValueError("9999 is reserved for DEM NODATA")
        if elevation > ELEVATION_MAXIMUM:
            raise ValueError(
                f"samples[{index}].elevation must be between "
                f"{ELEVATION_MINIMUM} and {ELEVATION_MAXIMUM}"
            )
        if surface not in SURFACE_IDS:
            raise ValueError(
                f"samples[{index}].surface must be one of {', '.join(SURFACE_IDS)}"
            )
        if biotope not in BIOTOPE_IDS:
            raise ValueError(
                f"samples[{index}].biotope must be one of {', '.join(BIOTOPE_IDS)}"
            )
        coordinate = (x, y)
        if coordinate in seen:
            raise ValueError(f"duplicate classification sample at {x},{y}")
        seen.add(coordinate)
        samples.append(ClassificationSample(x, y, surface, biotope, elevation))

    raw_regions = payload.get("regions", [])
    if not isinstance(raw_regions, list):
        raise ValueError("classification profile regions must be a JSON array")
    if len(raw_regions) > MAXIMUM_REGIONS:
        raise ValueError(
            f"classification profile has more than {MAXIMUM_REGIONS} regions"
        )

    regions = []
    total_vertices = 0
    for index, raw_region in enumerate(raw_regions):
        region_data = _mapping(raw_region, f"regions[{index}]")
        raw_vertices = region_data.get("vertices")
        if not isinstance(raw_vertices, list):
            raise ValueError(f"regions[{index}].vertices must be a JSON array")
        if len(raw_vertices) > MAXIMUM_REGION_VERTICES + 1:
            raise ValueError(
                f"regions[{index}] has more than "
                f"{MAXIMUM_REGION_VERTICES} vertices"
            )

        vertices = []
        vertex_elevations = []
        for vertex_index, raw_vertex in enumerate(raw_vertices):
            vertex_data = _mapping(
                raw_vertex,
                f"regions[{index}].vertices[{vertex_index}]",
            )
            vertices.append(
                (
                    _integer(
                        vertex_data.get("x"),
                        f"regions[{index}].vertices[{vertex_index}].x",
                        0,
                        source_width - 1,
                    ),
                    _integer(
                        vertex_data.get("y"),
                        f"regions[{index}].vertices[{vertex_index}].y",
                        0,
                        source_height - 1,
                    ),
                )
            )
        if len(vertices) >= 4 and vertices[0] == vertices[-1]:
            vertices.pop()
        _validate_polygon(vertices, f"regions[{index}]")
        total_vertices += len(vertices)
        if total_vertices > MAXIMUM_TOTAL_REGION_VERTICES:
            raise ValueError(
                "classification profile has more than "
                f"{MAXIMUM_TOTAL_REGION_VERTICES} region vertices"
            )

        surface = str(region_data.get("surface") or "").strip().lower()
        biotope = str(region_data.get("biotope") or "auto").strip().lower()
        elevation = _integer(
            region_data.get("elevation"),
            f"regions[{index}].elevation",
            ELEVATION_MINIMUM,
            ELEVATION_NODATA,
        )
        if elevation == ELEVATION_NODATA:
            raise ValueError("9999 is reserved for DEM NODATA")
        if elevation > ELEVATION_MAXIMUM:
            raise ValueError(
                f"regions[{index}].elevation must be between "
                f"{ELEVATION_MINIMUM} and {ELEVATION_MAXIMUM}"
            )
        if surface not in SURFACE_IDS:
            raise ValueError(
                f"regions[{index}].surface must be one of "
                f"{', '.join(SURFACE_IDS)}"
            )
        if biotope not in BIOTOPE_IDS:
            raise ValueError(
                f"regions[{index}].biotope must be one of "
                f"{', '.join(BIOTOPE_IDS)}"
            )
        for vertex_index, raw_vertex in enumerate(raw_vertices[: len(vertices)]):
            vertex_data = _mapping(
                raw_vertex,
                f"regions[{index}].vertices[{vertex_index}]",
            )
            vertex_elevation = _integer(
                vertex_data.get("elevation", elevation),
                f"regions[{index}].vertices[{vertex_index}].elevation",
                ELEVATION_MINIMUM,
                ELEVATION_NODATA,
            )
            if (
                vertex_elevation == ELEVATION_NODATA
                or vertex_elevation > ELEVATION_MAXIMUM
            ):
                raise ValueError(
                    f"regions[{index}].vertices[{vertex_index}].elevation "
                    f"must be {ELEVATION_MINIMUM}..{ELEVATION_MAXIMUM}"
                )
            vertex_elevations.append(vertex_elevation)
        regions.append(
            ClassificationRegion(
                vertices=tuple(vertices),
                surface=surface,
                biotope=biotope,
                elevation=elevation,
                vertex_elevations=tuple(vertex_elevations),
            )
        )

    raw_lines = payload.get("lines", [])
    if not isinstance(raw_lines, list):
        raise ValueError("classification profile lines must be a JSON array")
    if len(raw_lines) > MAXIMUM_LINES:
        raise ValueError(
            f"classification profile has more than {MAXIMUM_LINES} lines"
        )

    lines = []
    for index, raw_line in enumerate(raw_lines):
        line_data = _mapping(raw_line, f"lines[{index}]")
        raw_vertices = line_data.get("vertices")
        if not isinstance(raw_vertices, list):
            raise ValueError(f"lines[{index}].vertices must be a JSON array")
        if len(raw_vertices) > MAXIMUM_REGION_VERTICES:
            raise ValueError(
                f"lines[{index}] has more than "
                f"{MAXIMUM_REGION_VERTICES} vertices"
            )

        vertices = []
        vertex_elevations = []
        for vertex_index, raw_vertex in enumerate(raw_vertices):
            vertex_data = _mapping(
                raw_vertex,
                f"lines[{index}].vertices[{vertex_index}]",
            )
            vertices.append(
                (
                    _integer(
                        vertex_data.get("x"),
                        f"lines[{index}].vertices[{vertex_index}].x",
                        0,
                        source_width - 1,
                    ),
                    _integer(
                        vertex_data.get("y"),
                        f"lines[{index}].vertices[{vertex_index}].y",
                        0,
                        source_height - 1,
                    ),
                )
            )
        _validate_line(vertices, f"lines[{index}]")
        total_vertices += len(vertices)
        if total_vertices > MAXIMUM_TOTAL_REGION_VERTICES:
            raise ValueError(
                "classification profile has more than "
                f"{MAXIMUM_TOTAL_REGION_VERTICES} vector vertices"
            )

        surface = str(line_data.get("surface") or "").strip().lower()
        biotope = str(line_data.get("biotope") or "auto").strip().lower()
        elevation = _integer(
            line_data.get("elevation"),
            f"lines[{index}].elevation",
            ELEVATION_MINIMUM,
            ELEVATION_NODATA,
        )
        if elevation == ELEVATION_NODATA:
            raise ValueError("9999 is reserved for DEM NODATA")
        if elevation > ELEVATION_MAXIMUM:
            raise ValueError(
                f"lines[{index}].elevation must be between "
                f"{ELEVATION_MINIMUM} and {ELEVATION_MAXIMUM}"
            )
        if surface not in SURFACE_IDS:
            raise ValueError(
                f"lines[{index}].surface must be one of "
                f"{', '.join(SURFACE_IDS)}"
            )
        if biotope not in BIOTOPE_IDS:
            raise ValueError(
                f"lines[{index}].biotope must be one of "
                f"{', '.join(BIOTOPE_IDS)}"
            )
        width_cells = _integer(
            line_data.get("width_cells", 1),
            f"lines[{index}].width_cells",
            1,
            MAXIMUM_LINE_WIDTH_CELLS,
        )
        for vertex_index, raw_vertex in enumerate(raw_vertices[: len(vertices)]):
            vertex_data = _mapping(
                raw_vertex,
                f"lines[{index}].vertices[{vertex_index}]",
            )
            vertex_elevation = _integer(
                vertex_data.get("elevation", elevation),
                f"lines[{index}].vertices[{vertex_index}].elevation",
                ELEVATION_MINIMUM,
                ELEVATION_NODATA,
            )
            if (
                vertex_elevation == ELEVATION_NODATA
                or vertex_elevation > ELEVATION_MAXIMUM
            ):
                raise ValueError(
                    f"lines[{index}].vertices[{vertex_index}].elevation "
                    f"must be {ELEVATION_MINIMUM}..{ELEVATION_MAXIMUM}"
                )
            vertex_elevations.append(vertex_elevation)
        lines.append(
            ClassificationLine(
                vertices=tuple(vertices),
                surface=surface,
                biotope=biotope,
                elevation=elevation,
                width_cells=width_cells,
                vertex_elevations=tuple(vertex_elevations),
            )
        )

    map_boundary = ()
    outside_surface = "deep_ocean"
    outside_biotope = "none"
    outside_elevation = -4000
    raw_boundary = payload.get("map_boundary")
    if raw_boundary is not None:
        boundary_data = _mapping(raw_boundary, "map_boundary")
        raw_vertices = boundary_data.get("vertices")
        if not isinstance(raw_vertices, list):
            raise ValueError(
                "map_boundary.vertices must be a JSON array"
            )
        if len(raw_vertices) > MAXIMUM_REGION_VERTICES + 1:
            raise ValueError(
                "map_boundary has more than "
                f"{MAXIMUM_REGION_VERTICES} vertices"
            )

        boundary_vertices = []
        for vertex_index, raw_vertex in enumerate(raw_vertices):
            vertex_data = _mapping(
                raw_vertex,
                f"map_boundary.vertices[{vertex_index}]",
            )
            boundary_vertices.append(
                (
                    _integer(
                        vertex_data.get("x"),
                        f"map_boundary.vertices[{vertex_index}].x",
                        0,
                        source_width - 1,
                    ),
                    _integer(
                        vertex_data.get("y"),
                        f"map_boundary.vertices[{vertex_index}].y",
                        0,
                        source_height - 1,
                    ),
                )
            )
        if (
            len(boundary_vertices) >= 4
            and boundary_vertices[0] == boundary_vertices[-1]
        ):
            boundary_vertices.pop()
        _validate_polygon(boundary_vertices, "map_boundary")
        map_boundary = tuple(boundary_vertices)

        outside_surface = str(
            boundary_data.get("outside_surface") or "deep_ocean"
        ).strip().lower()
        if outside_surface not in SURFACE_IDS and outside_surface != "auto":
            raise ValueError(
                "map_boundary.outside_surface must be one of "
                f"auto, {', '.join(SURFACE_IDS)}"
            )
        outside_biotope = str(
            boundary_data.get("outside_biotope") or "none"
        ).strip().lower()
        if outside_biotope not in BIOTOPE_IDS:
            raise ValueError(
                "map_boundary.outside_biotope must be one of "
                f"{', '.join(BIOTOPE_IDS)}"
            )
        outside_elevation = _integer(
            boundary_data.get("outside_elevation", -4000),
            "map_boundary.outside_elevation",
            ELEVATION_MINIMUM,
            ELEVATION_NODATA,
        )
        if outside_elevation == ELEVATION_NODATA:
            raise ValueError("9999 is reserved for DEM NODATA")
        if outside_elevation > ELEVATION_MAXIMUM:
            raise ValueError(
                "map_boundary.outside_elevation must be between "
                f"{ELEVATION_MINIMUM} and {ELEVATION_MAXIMUM}"
            )

    if not samples and not regions and not lines:
        raise ValueError(
            "classification profile must contain a sample, line, or region"
        )

    settings = payload.get("settings")
    if settings is None:
        settings = {}
    settings = _mapping(settings, "settings")
    weights = (
        _number(settings.get("color_weight", 0.55), "settings.color_weight", 0.0, 10.0),
        _number(
            settings.get("texture_weight", 0.20),
            "settings.texture_weight",
            0.0,
            10.0,
        ),
        _number(
            settings.get("spatial_weight", 0.25),
            "settings.spatial_weight",
            0.0,
            10.0,
        ),
    )
    if sum(weights) <= 0.0:
        raise ValueError("at least one classification distance weight must be positive")

    return ClassificationProfile(
        source_file_name=source_file_name,
        source_width=source_width,
        source_height=source_height,
        samples=tuple(samples),
        regions=tuple(regions),
        lines=tuple(lines),
        long_side_blocks=_integer(
            settings.get("long_side_blocks", 20),
            "settings.long_side_blocks",
            1,
            MAX_MAP_CELLS // (CHUNK_SIZE * CHUNK_SIZE),
        ),
        color_weight=weights[0],
        texture_weight=weights[1],
        spatial_weight=weights[2],
        appearance_tolerance=_number(
            settings.get("appearance_tolerance", 0.65),
            "settings.appearance_tolerance",
            0.01,
            8.0,
        ),
        local_influence=_number(
            settings.get("local_influence", 0.08),
            "settings.local_influence",
            0.001,
            1.0,
        ),
        elevation_power=_number(
            settings.get("elevation_power", 2.0),
            "settings.elevation_power",
            0.25,
            8.0,
        ),
        elevation_smoothing=_integer(
            settings.get("elevation_smoothing", 1),
            "settings.elevation_smoothing",
            0,
            8,
        ),
        interpolate_elevation_globally=bool(
            settings.get("interpolate_elevation_globally", True)
        ),
        map_boundary=map_boundary,
        outside_surface=outside_surface,
        outside_biotope=outside_biotope,
        outside_elevation=outside_elevation,
    )


def apply_manual_classification(
    image: Image,
    automatic_tiles: Image,
    tile_names: Sequence[str],
    profile: ClassificationProfile,
    boundary_mask: Optional[np.ndarray] = None,
) -> Tuple[Image, np.ndarray]:
    """Apply ROI-bounded point, line, and polygon labels."""
    if image.size != automatic_tiles.size:
        raise ValueError("manual classifier inputs must have matching dimensions")
    if (profile.source_width, profile.source_height) == (0, 0):
        raise ValueError("classification profile has invalid source dimensions")

    width, height = image.size
    if boundary_mask is None:
        boundary_mask = rasterize_map_boundary(profile, width, height)
    if boundary_mask is None:
        active_mask = np.ones((height, width), dtype=bool)
    else:
        active_mask = np.asarray(boundary_mask, dtype=bool)
        if active_mask.shape != (height, width):
            raise ValueError(
                "classification map boundary has invalid dimensions"
            )
        if not np.any(active_mask):
            raise ValueError("classification map boundary is empty")
    rgb = np.asarray(image.convert("RGB"), dtype=np.float32) / 255.0
    if profile.map_boundary:
        interior = rgb[active_mask]
        stride = max(1, interior.shape[0] // 60_000)
        fill = np.median(interior[::stride], axis=0)
        rgb = rgb.copy()
        rgb[~active_mask] = fill
    color_features, texture_features = _make_calibration_features(
        rgb,
        active_mask,
    )
    flat_color = color_features.reshape((-1, color_features.shape[2]))
    flat_texture = texture_features.reshape((-1, texture_features.shape[2]))
    flat_automatic = np.asarray(automatic_tiles, dtype=np.uint8).reshape(-1)
    flat_result = flat_automatic.copy()

    region_labels = _rasterize_regions(profile, width, height)
    region_labels[~active_mask] = -1
    line_labels = _rasterize_lines(profile, width, height)
    line_labels[~active_mask] = -1
    _sample_positions, sample_indices = _sample_target_positions(
        profile,
        width,
        height,
        profile.samples,
    )
    del _sample_positions
    flat_active = active_mask.reshape(-1)
    active_samples = tuple(
        sample
        for sample, target_index in zip(profile.samples, sample_indices)
        if flat_active[int(target_index)]
    )
    effective_samples = _make_effective_samples(
        profile,
        region_labels,
        line_labels,
        width,
        height,
        active_samples,
    )
    if not effective_samples:
        raise ValueError(
            "classification profile does not cover a target pixel"
        )
    sample_positions, sample_indices = _sample_target_positions(
        profile,
        width,
        height,
        effective_samples,
    )
    sample_color = flat_color[sample_indices]
    sample_texture = flat_texture[sample_indices]
    sample_tiles = np.asarray(
        [
            _resolve_tile_index(
                sample.surface,
                sample.biotope,
                int(flat_automatic[target_index]),
                tile_names,
            )
            for sample, target_index in zip(effective_samples, sample_indices)
        ],
        dtype=np.uint8,
    )

    sample_surface_indices = np.asarray(
        [SURFACE_IDS.index(sample.surface) for sample in effective_samples],
        dtype=np.int8,
    )
    assigned_surface = np.full(flat_result.shape, -1, dtype=np.int8)
    local_squared = profile.local_influence * profile.local_influence
    chunk_size = max(
        512,
        min(16384, 1_000_000 // max(1, len(effective_samples))),
    )
    for start in range(0, flat_result.size, chunk_size):
        end = min(flat_result.size, start + chunk_size)
        positions = np.arange(start, end, dtype=np.int64)
        y = positions // width
        x = positions - y * width
        normalized_position = np.stack(
            ((x.astype(np.float32) + 0.5) / width,
             (y.astype(np.float32) + 0.5) / height),
            axis=1,
        )

        color_delta = flat_color[start:end, None, :] - sample_color[None, :, :]
        texture_delta = (
            flat_texture[start:end, None, :] - sample_texture[None, :, :]
        )
        spatial_delta = (
            normalized_position[:, None, :] - sample_positions[None, :, :]
        )
        color_distance = np.mean(color_delta * color_delta, axis=2)
        texture_distance = np.mean(texture_delta * texture_delta, axis=2)
        spatial_distance = np.sum(spatial_delta * spatial_delta, axis=2)
        distance = (
            profile.color_weight * color_distance
            + profile.texture_weight * texture_distance
            + profile.spatial_weight * spatial_distance
        )
        nearest = np.argmin(distance, axis=1)
        rows = np.arange(end - start)
        nearest_appearance = (
            color_distance[rows, nearest] + texture_distance[rows, nearest]
        )
        nearest_spatial = spatial_distance[rows, nearest]
        if len(effective_samples) == 1:
            apply_mask = nearest_spatial <= local_squared
        else:
            apply_mask = (
                nearest_appearance <= profile.appearance_tolerance
            ) | (nearest_spatial <= local_squared)
        apply_mask &= flat_active[start:end]
        target = flat_result[start:end]
        target[apply_mask] = sample_tiles[nearest[apply_mask]]
        assigned = assigned_surface[start:end]
        assigned[apply_mask] = sample_surface_indices[nearest[apply_mask]]

    flat_region_labels = region_labels.reshape(-1)
    for region_index, region in enumerate(profile.regions):
        region_mask = flat_region_labels == region_index
        if not np.any(region_mask):
            continue
        tile_lookup = np.asarray(
            [
                _resolve_tile_index(
                    region.surface,
                    region.biotope,
                    automatic_index,
                    tile_names,
                )
                for automatic_index in range(len(tile_names))
            ],
            dtype=np.uint8,
        )
        flat_result[region_mask] = tile_lookup[flat_automatic[region_mask]]
        assigned_surface[region_mask] = SURFACE_IDS.index(region.surface)

    for line_index, line in enumerate(profile.lines):
        line_mask = line_labels.reshape(-1) == line_index
        if not np.any(line_mask):
            continue
        tile_lookup = np.asarray(
            [
                _resolve_tile_index(
                    line.surface,
                    line.biotope,
                    automatic_index,
                    tile_names,
                )
                for automatic_index in range(len(tile_names))
            ],
            dtype=np.uint8,
        )
        flat_result[line_mask] = tile_lookup[flat_automatic[line_mask]]
        assigned_surface[line_mask] = SURFACE_IDS.index(line.surface)

    # The exact sampled cell and its immediate neighbors are authoritative.
    point_positions, point_indices = _sample_target_positions(
        profile,
        width,
        height,
        active_samples,
    )
    del point_positions
    for sample_offset, target_index in enumerate(point_indices):
        sample = active_samples[sample_offset]
        sample_tile = _resolve_tile_index(
            sample.surface,
            sample.biotope,
            int(flat_automatic[target_index]),
            tile_names,
        )
        surface_index = SURFACE_IDS.index(sample.surface)
        target_y, target_x = divmod(int(target_index), width)
        for y in range(max(0, target_y - 1), min(height, target_y + 2)):
            row_start = y * width
            for x in range(max(0, target_x - 1), min(width, target_x + 2)):
                index = row_start + x
                if not flat_active[index]:
                    continue
                flat_result[index] = sample_tile
                assigned_surface[index] = surface_index

    if profile.map_boundary:
        outside = ~flat_active
        _fill_unknown_surface_indices(
            flat_result,
            assigned_surface,
            flat_active,
            tile_names,
        )
        if profile.outside_surface == "auto":
            owner_y, owner_x = _nearest_source_indices(active_mask)
            result_grid = flat_result.reshape((height, width))
            surface_grid = assigned_surface.reshape((height, width))
            result_grid[outside.reshape((height, width))] = result_grid[
                owner_y[outside.reshape((height, width))],
                owner_x[outside.reshape((height, width))],
            ]
            surface_grid[outside.reshape((height, width))] = surface_grid[
                owner_y[outside.reshape((height, width))],
                owner_x[outside.reshape((height, width))],
            ]
        else:
            outside_lookup = np.asarray(
                [
                    _resolve_tile_index(
                        profile.outside_surface,
                        profile.outside_biotope,
                        automatic_index,
                        tile_names,
                    )
                    for automatic_index in range(len(tile_names))
                ],
                dtype=np.uint8,
            )
            flat_result[outside] = outside_lookup[flat_automatic[outside]]
            assigned_surface[outside] = SURFACE_IDS.index(
                profile.outside_surface
            )
    else:
        _fill_unknown_surface_indices(
            flat_result,
            assigned_surface,
            flat_active,
            tile_names,
        )

    result = make_index_image(flat_result.reshape((height, width)), tile_names)
    elevation = _interpolate_elevation(
        width,
        height,
        profile,
        assigned_surface.reshape((height, width)),
        active_samples,
        active_mask,
    )
    return result, elevation


def write_int16_geotiff(
    path: Path,
    values: np.ndarray,
    georeference: Optional[RasterGeoreference] = None,
) -> None:
    """Write a compact, uncompressed signed Int16 GeoTIFF."""
    array = np.asarray(values)
    if array.ndim != 2:
        raise ValueError("DEM GeoTIFF must be a two-dimensional grid")
    valid = array != ELEVATION_NODATA
    if np.any(array[valid] < ELEVATION_MINIMUM) or np.any(
        array[valid] > ELEVATION_MAXIMUM
    ):
        raise ValueError("DEM values must be -20000..9000 or NODATA 9999")

    height, width = array.shape
    rows_per_strip = 256
    strip_count = (height + rows_per_strip - 1) // rows_per_strip
    strip_byte_counts = [
        min(rows_per_strip, height - strip * rows_per_strip) * width * 2
        for strip in range(strip_count)
    ]

    entries = [
        _tiff_long(256, width),
        _tiff_long(257, height),
        _tiff_short(258, 16),
        _tiff_short(259, 1),
        _tiff_short(262, 1),
        _tiff_longs(273, [0] * strip_count),
        _tiff_short(277, 1),
        _tiff_long(278, rows_per_strip),
        _tiff_longs(279, strip_byte_counts),
        _tiff_short(284, 1),
        _tiff_short(339, 2),
        _tiff_ascii(42113, str(ELEVATION_NODATA)),
    ]
    if georeference is None:
        entries.extend(
            (
                _tiff_doubles(33550, (1000.0, 1000.0, 0.0)),
                _tiff_doubles(
                    33922,
                    (0.0, 0.0, 0.0, 0.0, height * 1000.0, 0.0),
                ),
                _tiff_shorts(
                    34735,
                    (1, 1, 0, 2, 1024, 0, 1, 32767, 1025, 0, 1, 1),
                ),
                _tiff_ascii(34737, "WorldBox Local ENGCRS|"),
            )
        )
    else:
        georeference.validate((width, height))
        transform = georeference.raster_to_crs
        origin_x = transform[0]
        origin_y = transform[3]
        if georeference.pixel_interpretation == "point":
            origin_x += 0.5 * transform[1] + 0.5 * transform[2]
            origin_y += 0.5 * transform[4] + 0.5 * transform[5]
        entries.append(
            _tiff_doubles(
                34264,
                (
                    transform[1],
                    transform[2],
                    0.0,
                    origin_x,
                    transform[4],
                    transform[5],
                    0.0,
                    origin_y,
                    0.0,
                    0.0,
                    1.0,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    1.0,
                ),
            )
        )
        geo_keys = georeference.geo_key_directory
        if not geo_keys and georeference.epsg is not None:
            geo_keys = build_geo_key_directory(
                georeference.epsg,
                georeference.crs_kind,
                georeference.pixel_interpretation,
                georeference.vertical_epsg,
            )
        if not geo_keys:
            raster_type = (
                2 if georeference.pixel_interpretation == "point" else 1
            )
            geo_keys = (
                1,
                1,
                0,
                2,
                1024,
                0,
                1,
                32767,
                1025,
                0,
                1,
                raster_type,
            )
        entries.append(_tiff_shorts(34735, geo_keys))
        if georeference.geo_double_params:
            entries.append(
                _tiff_doubles(34736, georeference.geo_double_params)
            )
        if georeference.geo_ascii_params:
            entries.append(
                _tiff_ascii(34737, georeference.geo_ascii_params)
            )
        entries.append(
            _tiff_ascii(
                270,
                json.dumps(
                    {
                        "format": "terrainlab-geotiff",
                        "schema_version": "1.2.0",
                        "source_file_name": georeference.source_file_name,
                        "epsg": georeference.epsg,
                        "pixel_interpretation": (
                            georeference.pixel_interpretation
                        ),
                    },
                    separators=(",", ":"),
                ),
            )
        )
    entries.sort(key=lambda item: item[0])

    ifd_size = 2 + len(entries) * 12 + 4
    extra_offset = 8 + ifd_size
    extra_locations: Dict[int, int] = {}
    for tag, _kind, _count, data in entries:
        if len(data) <= 4:
            continue
        extra_offset = _align4(extra_offset)
        extra_locations[tag] = extra_offset
        extra_offset += len(data)
    pixel_offset = _align4(extra_offset)
    strip_offsets = []
    running_offset = pixel_offset
    for byte_count in strip_byte_counts:
        strip_offsets.append(running_offset)
        running_offset += byte_count
    entries = [
        _tiff_longs(273, strip_offsets) if entry[0] == 273 else entry
        for entry in entries
    ]

    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as stream:
        stream.write(b"II")
        stream.write(struct.pack("<HI", 42, 8))
        stream.write(struct.pack("<H", len(entries)))
        for tag, kind, count, data in entries:
            stream.write(struct.pack("<HHI", tag, kind, count))
            if len(data) <= 4:
                stream.write(data.ljust(4, b"\0"))
            else:
                stream.write(struct.pack("<I", extra_locations[tag]))
        stream.write(struct.pack("<I", 0))

        for tag, _kind, _count, data in entries:
            if len(data) <= 4:
                continue
            stream.seek(extra_locations[tag])
            stream.write(data)
        stream.seek(pixel_offset)
        stream.write(np.asarray(array, dtype="<i2").tobytes(order="C"))
    if georeference is not None:
        write_raster_georeference_sidecars(path, georeference)


def _make_calibration_features(
    rgb: np.ndarray,
    valid_mask: Optional[np.ndarray] = None,
) -> Tuple[np.ndarray, np.ndarray]:
    if valid_mask is None:
        analysis_mask = np.ones(rgb.shape[:2], dtype=bool)
    else:
        analysis_mask = np.asarray(valid_mask, dtype=bool)
        if analysis_mask.shape != rgb.shape[:2]:
            raise ValueError(
                "manual classification mask must match the image"
            )
        if not np.any(analysis_mask):
            raise ValueError("manual classification mask is empty")

    red = rgb[:, :, 0]
    green = rgb[:, :, 1]
    blue = rgb[:, :, 2]
    hue, saturation, _value = rgb_to_hsv(rgb)
    luma = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue)
    low, high = percentiles(luma[analysis_mask], (2.0, 98.0))
    luma_norm = normalize(luma, low, high)
    slope = gradient_magnitude(luma_norm)
    roughness = local_roughness(luma_norm)
    slope_low, slope_high = percentiles(
        slope[analysis_mask],
        (5.0, 95.0),
    )
    rough_low, rough_high = percentiles(
        roughness[analysis_mask],
        (5.0, 95.0),
    )
    hue_angle = hue * (2.0 * np.pi)
    colors = np.stack(
        (
            red,
            green,
            blue,
            luma_norm,
            saturation,
            np.sin(hue_angle) * 0.5,
            np.cos(hue_angle) * 0.5,
        ),
        axis=2,
    ).astype(np.float32)
    textures = np.stack(
        (
            normalize(slope, slope_low, slope_high),
            normalize(roughness, rough_low, rough_high),
        ),
        axis=2,
    ).astype(np.float32)
    return colors, textures


def _sample_target_positions(
    profile: ClassificationProfile,
    width: int,
    height: int,
    samples: Optional[Sequence[ClassificationSample]] = None,
) -> Tuple[np.ndarray, np.ndarray]:
    positions = []
    indices = []
    source_samples = profile.samples if samples is None else samples
    for sample in source_samples:
        normalized_x = (sample.x + 0.5) / profile.source_width
        normalized_y = (sample.y + 0.5) / profile.source_height
        x = min(width - 1, max(0, int(normalized_x * width)))
        y = min(height - 1, max(0, int(normalized_y * height)))
        positions.append((normalized_x, normalized_y))
        indices.append(y * width + x)
    return (
        np.asarray(positions, dtype=np.float32),
        np.asarray(indices, dtype=np.int64),
    )


def rasterize_map_boundary(
    profile: ClassificationProfile,
    width: int,
    height: int,
) -> Optional[np.ndarray]:
    """Rasterize the optional source-space ROI at output-cell resolution."""
    if not profile.map_boundary:
        return None
    if width <= 0 or height <= 0:
        raise ValueError("map boundary target dimensions must be positive")

    mask_image = Img.new("L", (width, height), 0)
    try:
        drawing = ImageDraw.Draw(mask_image)
        vertices = []
        for source_x, source_y in profile.map_boundary:
            target_x = int(
                round(
                    ((source_x + 0.5) / profile.source_width) * width
                    - 0.5
                )
            )
            target_y = int(
                round(
                    ((source_y + 0.5) / profile.source_height) * height
                    - 0.5
                )
            )
            vertices.append(
                (
                    min(width - 1, max(0, target_x)),
                    min(height - 1, max(0, target_y)),
                )
            )
        drawing.polygon(vertices, fill=255)
        return np.asarray(mask_image, dtype=np.uint8).copy() > 0
    finally:
        mask_image.close()


def _rasterize_regions(
    profile: ClassificationProfile,
    width: int,
    height: int,
) -> np.ndarray:
    labels_image = Img.new("I", (width, height), -1)
    try:
        drawing = ImageDraw.Draw(labels_image)
        for region_index, region in enumerate(profile.regions):
            vertices = []
            for source_x, source_y in region.vertices:
                target_x = int(
                    round(
                        ((source_x + 0.5) / profile.source_width) * width
                        - 0.5
                    )
                )
                target_y = int(
                    round(
                        ((source_y + 0.5) / profile.source_height) * height
                        - 0.5
                    )
                )
                vertices.append(
                    (
                        min(width - 1, max(0, target_x)),
                        min(height - 1, max(0, target_y)),
                    )
                )
            drawing.polygon(vertices, fill=region_index)
        return np.asarray(labels_image, dtype=np.int32).copy()
    finally:
        labels_image.close()


def _rasterize_lines(
    profile: ClassificationProfile,
    width: int,
    height: int,
) -> np.ndarray:
    labels_image = Img.new("I", (width, height), -1)
    try:
        drawing = ImageDraw.Draw(labels_image)
        for line_index, line in enumerate(profile.lines):
            vertices = []
            for source_x, source_y in line.vertices:
                target_x = int(
                    round(
                        ((source_x + 0.5) / profile.source_width) * width
                        - 0.5
                    )
                )
                target_y = int(
                    round(
                        ((source_y + 0.5) / profile.source_height) * height
                        - 0.5
                    )
                )
                vertices.append(
                    (
                        min(width - 1, max(0, target_x)),
                        min(height - 1, max(0, target_y)),
                    )
                )
            drawing.line(
                vertices,
                fill=line_index,
                width=max(1, line.width_cells),
            )
        return np.asarray(labels_image, dtype=np.int32).copy()
    finally:
        labels_image.close()


def _make_effective_samples(
    profile: ClassificationProfile,
    region_labels: np.ndarray,
    line_labels: np.ndarray,
    width: int,
    height: int,
    point_samples: Optional[Sequence[ClassificationSample]] = None,
) -> Tuple[ClassificationSample, ...]:
    samples = list(
        profile.samples if point_samples is None else point_samples
    )
    remaining = max(0, MAXIMUM_EFFECTIVE_SAMPLES - len(samples))
    if remaining == 0 or (
        not profile.regions and not profile.lines
    ):
        return tuple(samples)

    occupied = {(sample.x, sample.y) for sample in samples}
    flat_labels = region_labels.reshape(-1)
    candidate_groups = []
    for region_index, region in enumerate(profile.regions):
        region_indices = np.flatnonzero(flat_labels == region_index)
        if region_indices.size == 0:
            candidate_groups.append([])
            continue

        desired = min(
            MAXIMUM_REGION_TRAINING_SAMPLES,
            int(region_indices.size),
            max(4, int(math.ceil(math.sqrt(region_indices.size) / 16.0))),
        )
        offsets = np.linspace(
            0,
            region_indices.size - 1,
            num=desired,
            dtype=np.int64,
        )
        candidates = []
        for target_index in region_indices[offsets]:
            target_y, target_x = divmod(int(target_index), width)
            source_x = min(
                profile.source_width - 1,
                max(
                    0,
                    int(
                        ((target_x + 0.5) / width)
                        * profile.source_width
                    ),
                ),
            )
            source_y = min(
                profile.source_height - 1,
                max(
                    0,
                    int(
                        ((target_y + 0.5) / height)
                        * profile.source_height
                    ),
                ),
            )
            coordinate = (source_x, source_y)
            if coordinate in occupied:
                continue
            occupied.add(coordinate)
            candidates.append(
                ClassificationSample(
                    x=source_x,
                    y=source_y,
                    surface=region.surface,
                    biotope=region.biotope,
                    elevation=region.elevation,
                )
            )
        candidate_groups.append(candidates)

    for line_index, line in enumerate(profile.lines):
        line_indices = np.flatnonzero(
            line_labels.reshape(-1) == line_index
        )
        if line_indices.size == 0:
            candidate_groups.append([])
            continue

        desired = min(
            MAXIMUM_REGION_TRAINING_SAMPLES,
            int(line_indices.size),
            max(2, int(math.ceil(math.sqrt(line_indices.size) / 8.0))),
        )
        offsets = np.linspace(
            0,
            line_indices.size - 1,
            num=desired,
            dtype=np.int64,
        )
        candidates = []
        for target_index in line_indices[offsets]:
            target_y, target_x = divmod(int(target_index), width)
            source_x = min(
                profile.source_width - 1,
                max(
                    0,
                    int(
                        ((target_x + 0.5) / width)
                        * profile.source_width
                    ),
                ),
            )
            source_y = min(
                profile.source_height - 1,
                max(
                    0,
                    int(
                        ((target_y + 0.5) / height)
                        * profile.source_height
                    ),
                ),
            )
            coordinate = (source_x, source_y)
            if coordinate in occupied:
                continue
            occupied.add(coordinate)
            candidates.append(
                ClassificationSample(
                    x=source_x,
                    y=source_y,
                    surface=line.surface,
                    biotope=line.biotope,
                    elevation=line.elevation,
                )
            )
        candidate_groups.append(candidates)

    round_index = 0
    while remaining > 0:
        added = False
        for candidates in candidate_groups:
            if round_index >= len(candidates):
                continue
            samples.append(candidates[round_index])
            remaining -= 1
            added = True
            if remaining == 0:
                break
        if not added:
            break
        round_index += 1

    return tuple(samples)


def _resolve_tile_index(
    surface: str,
    biotope: str,
    automatic_index: int,
    tile_names: Sequence[str],
) -> int:
    lookup = {tile: index for index, tile in enumerate(tile_names)}
    automatic_tile = tile_names[automatic_index]
    base_candidates = _SURFACE_TILE[surface]
    soil_level = None
    if base_candidates[0] in {"soil_low", "soil_high"}:
        soil_level = "low" if base_candidates[0] == "soil_low" else "high"

    if soil_level is not None:
        selected_biotope = None
        if biotope == "auto":
            selected_biotope = _extract_biotope(automatic_tile)
        elif biotope in _BIOTOPE_SUFFIX:
            selected_biotope = _BIOTOPE_SUFFIX[biotope]

        return living_soil_index(
            tile_names,
            soil_level,
            selected_biotope or "grass",
        )

    for candidate in base_candidates:
        if candidate in {"soil_low", "soil_high"}:
            level = "low" if candidate == "soil_low" else "high"
            return living_soil_index(tile_names, level)
        if candidate in lookup:
            return lookup[candidate]
    if automatic_tile in {"soil_low", "soil_high"}:
        level = "low" if automatic_tile == "soil_low" else "high"
        return living_soil_index(tile_names, level)
    return automatic_index


def _extract_biotope(tile: str) -> Optional[str]:
    if ":" not in tile:
        return None
    suffix = tile.split(":", 1)[1]
    if suffix.endswith("_low"):
        return suffix[:-4]
    if suffix.endswith("_high"):
        return suffix[:-5]
    return None


def _interpolate_elevation(
    width: int,
    height: int,
    profile: ClassificationProfile,
    assigned_surface: np.ndarray,
    active_samples: Sequence[ClassificationSample],
    active_mask: np.ndarray,
) -> np.ndarray:
    elevation_controls = _make_elevation_controls(profile, active_samples)
    if not elevation_controls:
        raise ValueError("classification profile has no elevation controls")

    maximum_dimension = 512
    scale = min(1.0, maximum_dimension / max(width, height))
    coarse_width = max(1, int(round(width * scale)))
    coarse_height = max(1, int(round(height * scale)))
    sample_positions, control_indices = _sample_target_positions(
        profile,
        coarse_width,
        coarse_height,
        elevation_controls,
    )
    sample_elevations = np.asarray(
        [sample.elevation for sample in elevation_controls],
        dtype=np.float32,
    )
    coarse_active = _resize_boolean_mask(
        active_mask,
        coarse_width,
        coarse_height,
    )
    keep = coarse_active.reshape(-1)[control_indices]
    if np.any(keep):
        sample_positions = sample_positions[keep]
        sample_elevations = sample_elevations[keep]
    if sample_elevations.size == 0:
        raise ValueError("elevation controls fall outside the map extent")

    maximum_controls = 1024
    if sample_elevations.size > maximum_controls:
        selected = np.linspace(
            0,
            sample_elevations.size - 1,
            maximum_controls,
            dtype=np.int64,
        )
        sample_positions = sample_positions[selected]
        sample_elevations = sample_elevations[selected]

    total = coarse_width * coarse_height
    interpolated = np.empty(total, dtype=np.float32)
    chunk_size = max(
        256,
        min(16384, 1_500_000 // max(1, len(sample_elevations))),
    )
    nearest_count = min(8, len(sample_elevations))
    for start in range(0, total, chunk_size):
        end = min(total, start + chunk_size)
        positions = np.arange(start, end, dtype=np.int64)
        y = positions // coarse_width
        x = positions - y * coarse_width
        normalized = np.stack(
            (
                (x.astype(np.float32) + 0.5) / coarse_width,
                (y.astype(np.float32) + 0.5) / coarse_height,
            ),
            axis=1,
        )
        delta = normalized[:, None, :] - sample_positions[None, :, :]
        distance_squared = np.sum(delta * delta, axis=2)
        if nearest_count < len(sample_elevations):
            nearest = np.argpartition(
                distance_squared,
                nearest_count - 1,
                axis=1,
            )[:, :nearest_count]
            distances = np.take_along_axis(distance_squared, nearest, axis=1)
            elevations = sample_elevations[nearest]
        else:
            distances = distance_squared
            elevations = sample_elevations[None, :]
        weights = np.power(
            np.maximum(distances, 1e-12),
            -profile.elevation_power * 0.5,
        )
        interpolated[start:end] = np.sum(weights * elevations, axis=1) / np.sum(
            weights,
            axis=1,
        )

    coarse = interpolated.reshape((coarse_height, coarse_width))
    coarse_image = Img.fromarray(coarse.astype(np.float32), mode="F")
    try:
        full_image = coarse_image.resize(
            (width, height),
            resample=Img.Resampling.BICUBIC,
        )
        try:
            full = np.asarray(full_image, dtype=np.float32).copy()
        finally:
            full_image.close()
    finally:
        coarse_image.close()

    if profile.elevation_smoothing > 0:
        full = smooth_float(full, profile.elevation_smoothing)

    seed = _stable_relief_seed(profile, elevation_controls)
    full += _quasi_isotropic_relief(
        height,
        width,
        seed,
        amplitude_scale=1.0,
    )

    _positions_full, sample_indices = _sample_target_positions(
        profile,
        width,
        height,
        elevation_controls,
    )
    del _positions_full
    for sample, target_index in zip(elevation_controls, sample_indices):
        y, x = divmod(int(target_index), width)
        if active_mask[y, x]:
            full[y, x] = sample.elevation

    if profile.map_boundary and profile.outside_surface != "auto":
        outside_relief = _quasi_isotropic_relief(
            height,
            width,
            seed + 104729,
            amplitude_scale=0.82,
        )
        full[~active_mask] = (
            float(profile.outside_elevation) + outside_relief[~active_mask]
        )

    _apply_surface_elevation_limits(full, assigned_surface)
    slope_caps = _surface_slope_caps(
        assigned_surface,
        seed + 32452843,
    )
    full = _limit_elevation_rise(
        full,
        maximum_cardinal_rise=slope_caps,
        maximum_diagonal_rise=slope_caps * np.float32(math.sqrt(2.0)),
        passes=12,
    )
    _apply_surface_elevation_limits(full, assigned_surface)

    nodata_mask = np.zeros(full.shape, dtype=bool)
    full = np.clip(full, ELEVATION_MINIMUM, ELEVATION_MAXIMUM)
    result = np.rint(full).astype(np.int16)
    result[(result == ELEVATION_NODATA) & ~nodata_mask] = ELEVATION_MAXIMUM
    result[nodata_mask] = ELEVATION_NODATA
    return result


def _make_elevation_controls(
    profile: ClassificationProfile,
    active_samples: Sequence[ClassificationSample],
) -> Tuple[ClassificationSample, ...]:
    grouped: Dict[Tuple[int, int], list[int]] = {}

    def add(x: int, y: int, elevation: int) -> None:
        grouped.setdefault((x, y), []).append(int(elevation))

    for sample in active_samples:
        add(sample.x, sample.y, sample.elevation)
    for region in profile.regions:
        elevations = (
            region.vertex_elevations
            if len(region.vertex_elevations) == len(region.vertices)
            else (region.elevation,) * len(region.vertices)
        )
        for vertex, elevation in zip(region.vertices, elevations):
            add(vertex[0], vertex[1], elevation)
    for line in profile.lines:
        elevations = (
            line.vertex_elevations
            if len(line.vertex_elevations) == len(line.vertices)
            else (line.elevation,) * len(line.vertices)
        )
        for vertex, elevation in zip(line.vertices, elevations):
            add(vertex[0], vertex[1], elevation)

    return tuple(
        ClassificationSample(
            x=x,
            y=y,
            surface="plain",
            biotope="none",
            elevation=int(round(sum(values) / len(values))),
        )
        for (x, y), values in grouped.items()
    )


def _resize_boolean_mask(
    mask: np.ndarray,
    width: int,
    height: int,
) -> np.ndarray:
    image = Img.fromarray(np.asarray(mask, dtype=np.uint8) * 255, mode="L")
    try:
        resized = image.resize((width, height), resample=Img.Resampling.NEAREST)
        try:
            return np.asarray(resized, dtype=np.uint8).copy() > 0
        finally:
            resized.close()
    finally:
        image.close()


def _stable_relief_seed(
    profile: ClassificationProfile,
    controls: Sequence[ClassificationSample],
) -> int:
    value = (
        profile.source_width * 73856093
        ^ profile.source_height * 19349663
    )
    for index, control in enumerate(controls):
        value ^= (
            (index + 1) * 83492791
            ^ control.x * 73856093
            ^ control.y * 19349663
            ^ (control.elevation + 20000) * 2654435761
        )
    return value & 0x7FFFFFFF


def _quasi_isotropic_relief(
    height: int,
    width: int,
    seed: int,
    amplitude_scale: float,
) -> np.ndarray:
    y, x = np.indices((height, width), dtype=np.float32)
    rng = np.random.default_rng(seed)
    relief = np.zeros((height, width), dtype=np.float32)
    components = (
        (520.0, 64.0),
        (210.0, 31.0),
        (82.0, 15.0),
    )
    for amplitude, wavelength in components:
        angle = float(rng.uniform(0.0, np.pi))
        phase = float(rng.uniform(0.0, 2.0 * np.pi))
        cross_phase = float(rng.uniform(0.0, 2.0 * np.pi))
        axis = np.cos(angle) * x + np.sin(angle) * y
        cross = -np.sin(angle) * x + np.cos(angle) * y
        relief += (
            amplitude
            * amplitude_scale
            * np.sin((2.0 * np.pi * axis / wavelength) + phase)
            * (
                0.72
                + 0.28
                * np.cos(
                    (2.0 * np.pi * cross / (wavelength * 1.37))
                    + cross_phase
                )
            )
        )
    relief -= float(np.median(relief))
    return relief


def _apply_surface_elevation_limits(
    full: np.ndarray,
    assigned_surface: np.ndarray,
) -> None:
    for surface_index, surface in enumerate(SURFACE_IDS):
        mask = assigned_surface == surface_index
        if not np.any(mask):
            continue
        if surface == "deep_ocean":
            full[mask] = np.minimum(full[mask], -151.0)
        elif surface == "shelf":
            full[mask] = np.clip(full[mask], -150.0, -6.0)
        elif surface == "shallow_water":
            full[mask] = np.clip(full[mask], -5.0, -1.0)


def _fill_unknown_surface_indices(
    flat_tiles: np.ndarray,
    assigned_surface: np.ndarray,
    active: np.ndarray,
    tile_names: Sequence[str],
) -> None:
    unresolved = active & (assigned_surface < 0)
    if not np.any(unresolved):
        return
    for tile_index, tile_name in enumerate(tile_names):
        surface = _surface_for_tile_name(tile_name)
        mask = unresolved & (flat_tiles == tile_index)
        if np.any(mask):
            assigned_surface[mask] = SURFACE_IDS.index(surface)
    assigned_surface[active & (assigned_surface < 0)] = SURFACE_IDS.index(
        "plain"
    )


def _surface_for_tile_name(tile_name: str) -> str:
    base = str(tile_name).split(":", 1)[0]
    return {
        "deep_ocean": "deep_ocean",
        "close_ocean": "shelf",
        "shallow_waters": "shallow_water",
        "sand": "sand",
        "soil_low": "plain",
        "soil_high": "upland",
        "hills": "hills",
        "mountains": "rocks",
    }.get(base, "plain")


def _nearest_source_indices(
    source_mask: np.ndarray,
) -> Tuple[np.ndarray, np.ndarray]:
    """Approximate a nearest-cell Voronoi field with jump flooding."""
    mask = np.asarray(source_mask, dtype=bool)
    if mask.ndim != 2 or not np.any(mask):
        raise ValueError("nearest-class propagation needs a source cell")
    height, width = mask.shape
    grid_y, grid_x = np.indices((height, width), dtype=np.int32)
    owner_y = np.where(mask, grid_y, -1).astype(np.int32)
    owner_x = np.where(mask, grid_x, -1).astype(np.int32)

    step = 1
    while step < max(width, height):
        step <<= 1
    step >>= 1
    while step >= 1:
        for delta_y, delta_x in (
            (-step, -step),
            (-step, 0),
            (-step, step),
            (0, -step),
            (0, step),
            (step, -step),
            (step, 0),
            (step, step),
        ):
            target_y = (
                slice(-delta_y, height)
                if delta_y < 0
                else slice(0, height - delta_y)
            )
            source_y = (
                slice(0, height + delta_y)
                if delta_y < 0
                else slice(delta_y, height)
            )
            target_x = (
                slice(-delta_x, width)
                if delta_x < 0
                else slice(0, width - delta_x)
            )
            source_x = (
                slice(0, width + delta_x)
                if delta_x < 0
                else slice(delta_x, width)
            )

            candidate_y = owner_y[source_y, source_x].copy()
            candidate_x = owner_x[source_y, source_x].copy()
            valid = candidate_y >= 0
            if not np.any(valid):
                continue
            current_y = owner_y[target_y, target_x]
            current_x = owner_x[target_y, target_x]
            target_grid_y = grid_y[target_y, target_x]
            target_grid_x = grid_x[target_y, target_x]
            candidate_distance = (
                (candidate_y.astype(np.int64) - target_grid_y) ** 2
                + (candidate_x.astype(np.int64) - target_grid_x) ** 2
            )
            current_valid = current_y >= 0
            current_distance = (
                (current_y.astype(np.int64) - target_grid_y) ** 2
                + (current_x.astype(np.int64) - target_grid_x) ** 2
            )
            update = valid & (
                ~current_valid | (candidate_distance < current_distance)
            )
            current_y[update] = candidate_y[update]
            current_x[update] = candidate_x[update]
        step >>= 1

    missing = owner_y < 0
    if np.any(missing):
        fallback_y, fallback_x = np.argwhere(mask)[0]
        owner_y[missing] = int(fallback_y)
        owner_x[missing] = int(fallback_x)
    return owner_y, owner_x


def _surface_slope_caps(
    assigned_surface: np.ndarray,
    seed: int,
) -> np.ndarray:
    """Build deterministic per-cell rise caps with rare steep terrain."""
    shape = assigned_surface.shape
    caps = np.full(
        shape,
        math.tan(math.radians(20.0)) * 1000.0,
        dtype=np.float32,
    )
    variation = np.abs(
        _quasi_isotropic_relief(
            shape[0],
            shape[1],
            seed,
            amplitude_scale=1.0,
        )
    )
    lower = float(np.percentile(variation, 82.0))
    upper = float(np.percentile(variation, 99.5))
    tail = np.clip(
        (variation - lower) / max(1.0, upper - lower),
        0.0,
        1.0,
    )

    hills = assigned_surface == SURFACE_IDS.index("hills")
    rocks = assigned_surface == SURFACE_IDS.index("rocks")
    summits = assigned_surface == SURFACE_IDS.index("summit")
    if np.any(hills):
        degrees = 20.0 + 30.0 * np.power(tail[hills], 2.6)
        caps[hills] = np.tan(np.radians(degrees)) * 1000.0
    if np.any(rocks):
        degrees = 20.0 + 64.0 * np.power(tail[rocks], 3.5)
        caps[rocks] = np.tan(np.radians(degrees)) * 1000.0
    if np.any(summits):
        degrees = 20.0 + 68.0 * np.power(tail[summits], 4.0)
        caps[summits] = np.tan(np.radians(degrees)) * 1000.0
    return caps


def _limit_elevation_rise(
    values: np.ndarray,
    maximum_cardinal_rise,
    maximum_diagonal_rise,
    passes: int,
) -> np.ndarray:
    result = np.asarray(values, dtype=np.float32).copy()
    cardinal = np.broadcast_to(
        np.asarray(maximum_cardinal_rise, dtype=np.float32),
        result.shape,
    )
    diagonal = np.broadcast_to(
        np.asarray(maximum_diagonal_rise, dtype=np.float32),
        result.shape,
    )
    for _ in range(max(1, passes)):
        horizontal = np.maximum(cardinal[:, 1:], cardinal[:, :-1])
        result[:, 1:] = np.minimum(
            result[:, 1:],
            result[:, :-1] + horizontal,
        )
        result[:, :-1] = np.minimum(
            result[:, :-1],
            result[:, 1:] + horizontal,
        )
        vertical = np.maximum(cardinal[1:, :], cardinal[:-1, :])
        result[1:, :] = np.minimum(
            result[1:, :],
            result[:-1, :] + vertical,
        )
        result[:-1, :] = np.minimum(
            result[:-1, :],
            result[1:, :] + vertical,
        )
        down_right = np.maximum(diagonal[1:, 1:], diagonal[:-1, :-1])
        result[1:, 1:] = np.minimum(
            result[1:, 1:],
            result[:-1, :-1] + down_right,
        )
        result[:-1, :-1] = np.minimum(
            result[:-1, :-1],
            result[1:, 1:] + down_right,
        )
        down_left = np.maximum(diagonal[1:, :-1], diagonal[:-1, 1:])
        result[1:, :-1] = np.minimum(
            result[1:, :-1],
            result[:-1, 1:] + down_left,
        )
        result[:-1, 1:] = np.minimum(
            result[:-1, 1:],
            result[1:, :-1] + down_left,
        )
    return result


def _validate_polygon(
    vertices: Sequence[Tuple[int, int]],
    name: str,
) -> None:
    if len(vertices) < 3:
        raise ValueError(f"{name} must contain at least three vertices")
    if len(vertices) > MAXIMUM_REGION_VERTICES:
        raise ValueError(
            f"{name} has more than {MAXIMUM_REGION_VERTICES} vertices"
        )
    if len(set(vertices)) != len(vertices):
        raise ValueError(f"{name} contains duplicate vertices")

    count = len(vertices)
    for first in range(count):
        first_next = (first + 1) % count
        for second in range(first + 1, count):
            second_next = (second + 1) % count
            if (
                first == second
                or first_next == second
                or second_next == first
            ):
                continue
            if _segments_intersect(
                vertices[first],
                vertices[first_next],
                vertices[second],
                vertices[second_next],
            ):
                raise ValueError(f"{name} is self-intersecting")
    if _polygon_area_twice(vertices) == 0:
        raise ValueError(f"{name} has zero area")


def _validate_line(
    vertices: Sequence[Tuple[int, int]],
    name: str,
) -> None:
    if len(vertices) < 2:
        raise ValueError(f"{name} must contain at least two vertices")
    if len(vertices) > MAXIMUM_REGION_VERTICES:
        raise ValueError(
            f"{name} has more than {MAXIMUM_REGION_VERTICES} vertices"
        )
    for first, second in zip(vertices, vertices[1:]):
        if first == second:
            raise ValueError(
                f"{name} contains consecutive duplicate vertices"
            )


def _polygon_area_twice(vertices: Sequence[Tuple[int, int]]) -> int:
    area = 0
    previous_x, previous_y = vertices[-1]
    for x, y in vertices:
        area += previous_x * y - x * previous_y
        previous_x, previous_y = x, y
    return area


def _segments_intersect(
    first_start: Tuple[int, int],
    first_end: Tuple[int, int],
    second_start: Tuple[int, int],
    second_end: Tuple[int, int],
) -> bool:
    first_orientation = _orientation(
        first_start,
        first_end,
        second_start,
    )
    second_orientation = _orientation(
        first_start,
        first_end,
        second_end,
    )
    third_orientation = _orientation(
        second_start,
        second_end,
        first_start,
    )
    fourth_orientation = _orientation(
        second_start,
        second_end,
        first_end,
    )
    if (
        first_orientation == 0
        and _point_on_segment(second_start, first_start, first_end)
    ):
        return True
    if (
        second_orientation == 0
        and _point_on_segment(second_end, first_start, first_end)
    ):
        return True
    if (
        third_orientation == 0
        and _point_on_segment(first_start, second_start, second_end)
    ):
        return True
    if (
        fourth_orientation == 0
        and _point_on_segment(first_end, second_start, second_end)
    ):
        return True
    return (
        (first_orientation > 0) != (second_orientation > 0)
        and (third_orientation > 0) != (fourth_orientation > 0)
    )


def _orientation(
    start: Tuple[int, int],
    end: Tuple[int, int],
    point: Tuple[int, int],
) -> int:
    return (
        (end[0] - start[0]) * (point[1] - start[1])
        - (end[1] - start[1]) * (point[0] - start[0])
    )


def _point_on_segment(
    point: Tuple[int, int],
    start: Tuple[int, int],
    end: Tuple[int, int],
) -> bool:
    return (
        min(start[0], end[0]) <= point[0] <= max(start[0], end[0])
        and min(start[1], end[1]) <= point[1] <= max(start[1], end[1])
    )


def _mapping(value: Any, name: str) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise ValueError(f"{name} must be a JSON object")
    return value


def _integer(value: Any, name: str, minimum: int, maximum: int) -> int:
    if isinstance(value, bool):
        raise ValueError(f"{name} must be an integer")
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        raise ValueError(f"{name} must be an integer")
    if parsed != value and not isinstance(value, str):
        raise ValueError(f"{name} must be an integer")
    if parsed < minimum or parsed > maximum:
        raise ValueError(f"{name} must be between {minimum} and {maximum}")
    return parsed


def _number(
    value: Any,
    name: str,
    minimum: float,
    maximum: float,
) -> float:
    if isinstance(value, bool):
        raise ValueError(f"{name} must be a number")
    try:
        parsed = float(value)
    except (TypeError, ValueError):
        raise ValueError(f"{name} must be a number")
    if not math.isfinite(parsed) or parsed < minimum or parsed > maximum:
        raise ValueError(f"{name} must be between {minimum} and {maximum}")
    return parsed


def _align4(value: int) -> int:
    return (value + 3) & ~3


def _tiff_ascii(tag: int, value: str) -> Tuple[int, int, int, bytes]:
    data = value.encode("ascii", errors="replace") + b"\0"
    return tag, 2, len(data), data


def _tiff_short(tag: int, value: int) -> Tuple[int, int, int, bytes]:
    return tag, 3, 1, struct.pack("<H", value)


def _tiff_shorts(
    tag: int,
    values: Sequence[int],
) -> Tuple[int, int, int, bytes]:
    return tag, 3, len(values), struct.pack("<" + "H" * len(values), *values)


def _tiff_long(tag: int, value: int) -> Tuple[int, int, int, bytes]:
    return tag, 4, 1, struct.pack("<I", value)


def _tiff_longs(
    tag: int,
    values: Sequence[int],
) -> Tuple[int, int, int, bytes]:
    return tag, 4, len(values), struct.pack("<" + "I" * len(values), *values)


def _tiff_doubles(
    tag: int,
    values: Sequence[float],
) -> Tuple[int, int, int, bytes]:
    return tag, 12, len(values), struct.pack("<" + "d" * len(values), *values)
