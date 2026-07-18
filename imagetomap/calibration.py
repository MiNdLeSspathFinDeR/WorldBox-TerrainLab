from dataclasses import dataclass
import json
import math
from pathlib import Path
import struct
from typing import Any, Dict, Mapping, Optional, Sequence, Tuple

import numpy as np
from PIL import Image as Img
from PIL.Image import Image

from .consts import ELEVATION_MAXIMUM, ELEVATION_MINIMUM, ELEVATION_NODATA
from .terrain import (
    gradient_magnitude,
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
}


@dataclass(frozen=True)
class ClassificationSample:
    x: int
    y: int
    surface: str
    biotope: str
    elevation: int


@dataclass(frozen=True)
class ClassificationProfile:
    source_file_name: str
    source_width: int
    source_height: int
    samples: Tuple[ClassificationSample, ...]
    color_weight: float = 0.55
    texture_weight: float = 0.20
    spatial_weight: float = 0.25
    appearance_tolerance: float = 0.65
    local_influence: float = 0.08
    elevation_power: float = 2.0
    elevation_smoothing: int = 1
    interpolate_elevation_globally: bool = True

    def to_json_dict(self) -> Dict[str, Any]:
        return {
            "schema_version": 1,
            "source": {
                "file_name": self.source_file_name,
                "width": self.source_width,
                "height": self.source_height,
            },
            "settings": {
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
        }


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

    payload = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(payload, dict) or payload.get("schema_version") != 1:
        raise ValueError("classification profile schema_version must be 1")

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

    raw_samples = payload.get("samples")
    if not isinstance(raw_samples, list) or not raw_samples:
        raise ValueError("classification profile must contain at least one sample")
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
    )


def apply_manual_classification(
    image: Image,
    automatic_tiles: Image,
    tile_names: Sequence[str],
    profile: ClassificationProfile,
) -> Tuple[Image, np.ndarray]:
    """Apply point labels using adaptive colour, texture, and spatial distance."""
    if image.size != automatic_tiles.size:
        raise ValueError("manual classifier inputs must have matching dimensions")
    if (profile.source_width, profile.source_height) == (0, 0):
        raise ValueError("classification profile has invalid source dimensions")

    width, height = image.size
    rgb = np.asarray(image.convert("RGB"), dtype=np.float32) / 255.0
    color_features, texture_features = _make_calibration_features(rgb)
    flat_color = color_features.reshape((-1, color_features.shape[2]))
    flat_texture = texture_features.reshape((-1, texture_features.shape[2]))
    flat_automatic = np.asarray(automatic_tiles, dtype=np.uint8).reshape(-1)
    flat_result = flat_automatic.copy()

    sample_positions, sample_indices = _sample_target_positions(
        profile,
        width,
        height,
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
            for sample, target_index in zip(profile.samples, sample_indices)
        ],
        dtype=np.uint8,
    )

    sample_surface_indices = np.arange(len(profile.samples), dtype=np.int16)
    assigned_sample = np.full(flat_result.shape, -1, dtype=np.int16)
    local_squared = profile.local_influence * profile.local_influence
    chunk_size = max(
        512,
        min(16384, 1_000_000 // max(1, len(profile.samples))),
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
        if len(profile.samples) == 1:
            apply_mask = nearest_spatial <= local_squared
        else:
            apply_mask = (
                nearest_appearance <= profile.appearance_tolerance
            ) | (nearest_spatial <= local_squared)
        target = flat_result[start:end]
        target[apply_mask] = sample_tiles[nearest[apply_mask]]
        assigned = assigned_sample[start:end]
        assigned[apply_mask] = sample_surface_indices[nearest[apply_mask]]

    # The exact sampled cell and its immediate neighbors are authoritative.
    for sample_offset, target_index in enumerate(sample_indices):
        target_y, target_x = divmod(int(target_index), width)
        for y in range(max(0, target_y - 1), min(height, target_y + 2)):
            row_start = y * width
            for x in range(max(0, target_x - 1), min(width, target_x + 2)):
                index = row_start + x
                flat_result[index] = sample_tiles[sample_offset]
                assigned_sample[index] = sample_offset

    result = make_index_image(flat_result.reshape((height, width)), tile_names)
    elevation = _interpolate_elevation(
        width,
        height,
        profile,
        assigned_sample.reshape((height, width)),
    )
    return result, elevation


def write_int16_geotiff(path: Path, values: np.ndarray) -> None:
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
        _tiff_ascii(42113, str(ELEVATION_NODATA)),
    ]
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


def _make_calibration_features(
    rgb: np.ndarray,
) -> Tuple[np.ndarray, np.ndarray]:
    red = rgb[:, :, 0]
    green = rgb[:, :, 1]
    blue = rgb[:, :, 2]
    hue, saturation, _value = rgb_to_hsv(rgb)
    luma = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue)
    low, high = percentiles(luma, (2.0, 98.0))
    luma_norm = normalize(luma, low, high)
    slope = gradient_magnitude(luma_norm)
    roughness = local_roughness(luma_norm)
    slope_low, slope_high = percentiles(slope, (5.0, 95.0))
    rough_low, rough_high = percentiles(roughness, (5.0, 95.0))
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
) -> Tuple[np.ndarray, np.ndarray]:
    positions = []
    indices = []
    for sample in profile.samples:
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

    if soil_level is not None and biotope == "auto":
        automatic_biotope = _extract_biotope(automatic_tile)
        if automatic_biotope is not None:
            candidate = (
                f"soil_{soil_level}:{automatic_biotope}_{soil_level}"
            )
            if candidate in lookup:
                return lookup[candidate]
    elif soil_level is not None and biotope in _BIOTOPE_SUFFIX:
        suffix = _BIOTOPE_SUFFIX[biotope]
        candidate = f"soil_{soil_level}:{suffix}_{soil_level}"
        if candidate in lookup:
            return lookup[candidate]

    for candidate in base_candidates:
        if candidate in lookup:
            return lookup[candidate]
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
    assigned_sample: np.ndarray,
) -> np.ndarray:
    maximum_dimension = 512
    scale = min(1.0, maximum_dimension / max(width, height))
    coarse_width = max(1, int(round(width * scale)))
    coarse_height = max(1, int(round(height * scale)))
    sample_positions, _indices = _sample_target_positions(
        profile,
        coarse_width,
        coarse_height,
    )
    sample_elevations = np.asarray(
        [sample.elevation for sample in profile.samples],
        dtype=np.float32,
    )

    total = coarse_width * coarse_height
    interpolated = np.empty(total, dtype=np.float32)
    chunk_size = max(
        512,
        min(32768, 1_500_000 // max(1, len(profile.samples))),
    )
    nearest_count = min(8, len(profile.samples))
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
        if nearest_count < len(profile.samples):
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

    nodata_mask = np.zeros(full.shape, dtype=bool)
    if not profile.interpolate_elevation_globally:
        nodata_mask = assigned_sample < 0

    sample_positions_full, sample_indices = _sample_target_positions(
        profile,
        width,
        height,
    )
    del sample_positions_full
    for sample, target_index in zip(profile.samples, sample_indices):
        y, x = divmod(int(target_index), width)
        full[y, x] = sample.elevation

    for sample_index, sample in enumerate(profile.samples):
        mask = assigned_sample == sample_index
        if not np.any(mask):
            continue
        if sample.surface == "deep_ocean":
            full[mask] = np.minimum(full[mask], -151.0)
        elif sample.surface == "shelf":
            full[mask] = np.clip(full[mask], -150.0, -6.0)
        elif sample.surface == "shallow_water":
            full[mask] = np.clip(full[mask], -5.0, -1.0)

    full = np.clip(full, ELEVATION_MINIMUM, ELEVATION_MAXIMUM)
    result = np.rint(full).astype(np.int16)
    result[(result == ELEVATION_NODATA) & ~nodata_mask] = ELEVATION_MAXIMUM
    result[nodata_mask] = ELEVATION_NODATA
    return result


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
    data = value.encode("ascii") + b"\0"
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
