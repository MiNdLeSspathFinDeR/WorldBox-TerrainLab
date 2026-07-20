from dataclasses import dataclass
import json
from pathlib import Path
from typing import Any, Dict, Mapping, Optional, Sequence, Tuple

import numpy as np
from PIL import Image as Img
from PIL.Image import Image

from .consts import TILES


SEMANTIC_DIAGNOSTICS_DIRECTORY = "terrainlab-semantic"
SEMANTIC_DIAGNOSTICS_SCHEMA_VERSION = 1
NO_CLASS = 255

LANDFORM_CLASSES = (
    "water_or_void",
    "plain",
    "lowland",
    "upland",
    "hills",
    "summit",
    "depression",
)
SUBSTRATE_CLASSES = (
    "none",
    "soil",
    "sand_sediment",
    "stony_soil",
    "bare_rock",
)
HYDROLOGY_CLASSES = (
    "none",
    "deep_ocean",
    "continental_shelf",
    "shallow_open_water",
    "river_or_lake",
)
THEME_CLASSES = (
    "none",
    "enchanted",
    "corrupted",
    "infernal",
    "candy",
    "crystal",
    "singularity",
    "paradox",
)
THEME_BIOTOPES = frozenset(THEME_CLASSES[1:])

LANDFORM_COLORS = (
    (36, 96, 176),
    (150, 190, 83),
    (126, 175, 70),
    (198, 161, 84),
    (112, 103, 88),
    (226, 226, 216),
    (108, 89, 136),
)
SUBSTRATE_COLORS = (
    (30, 35, 42),
    (125, 86, 55),
    (229, 205, 128),
    (139, 132, 112),
    (70, 73, 76),
)
HYDROLOGY_COLORS = (
    (30, 35, 42),
    (40, 85, 180),
    (58, 132, 214),
    (80, 190, 225),
    (48, 158, 212),
)
THEME_COLORS = (
    (30, 35, 42),
    (140, 220, 106),
    (111, 85, 108),
    (156, 54, 38),
    (255, 150, 176),
    (104, 234, 222),
    (75, 65, 102),
    (184, 120, 215),
)


@dataclass(frozen=True)
class SemanticRaster:
    landform: np.ndarray
    substrate: np.ndarray
    hydrology: np.ndarray
    biotope: np.ndarray
    theme: np.ndarray
    hostable: np.ndarray
    confidence: np.ndarray
    biotope_classes: Tuple[str, ...]

    def validate(self) -> None:
        shape = self.landform.shape
        if len(shape) != 2:
            raise ValueError("semantic layers must be two-dimensional")
        for name, layer in self.layers().items():
            if layer.shape != shape:
                raise ValueError(
                    f"semantic layer {name} does not match the raster"
                )
        illegal = (~self.hostable) & (self.biotope != NO_CLASS)
        if np.any(illegal):
            raise ValueError(
                "non-hostable terrain contains a biotope assignment"
            )

    def layers(self) -> Mapping[str, np.ndarray]:
        return {
            "landform": self.landform,
            "substrate": self.substrate,
            "hydrology": self.hydrology,
            "biotope": self.biotope,
            "theme": self.theme,
            "hostability": self.hostable,
            "confidence": self.confidence,
        }

    def metadata(self) -> Dict[str, Any]:
        height, width = self.landform.shape
        return {
            "schema_version": SEMANTIC_DIAGNOSTICS_SCHEMA_VERSION,
            "width": width,
            "height": height,
            "nodata_code": NO_CLASS,
            "hostability_invariant": (
                "water, hills, summits, and bare rock cannot carry biotopes"
            ),
            "classes": {
                "landform": list(LANDFORM_CLASSES),
                "substrate": list(SUBSTRATE_CLASSES),
                "hydrology": list(HYDROLOGY_CLASSES),
                "biotope": list(self.biotope_classes),
                "theme": list(THEME_CLASSES),
            },
        }


def categorical_area_resample(
    image: Image,
    size: Tuple[int, int],
    tile_names: Sequence[str],
) -> Image:
    """Resize labels by area voting without interpolating category numbers."""
    result, _ = categorical_area_resample_with_confidence(
        image,
        size,
        tile_names,
    )
    return result


def categorical_area_resample_with_confidence(
    image: Image,
    size: Tuple[int, int],
    tile_names: Sequence[str],
) -> Tuple[Image, np.ndarray]:
    """Return area-voted labels and their winning coverage as UInt8."""
    if size[0] <= 0 or size[1] <= 0:
        raise ValueError("categorical target dimensions must be positive")
    indices = np.asarray(image, dtype=np.uint8)
    if indices.ndim != 2:
        raise ValueError("categorical source image must contain one band")
    if image.size == size:
        return (
            make_index_image(indices.copy(), tile_names),
            np.full(indices.shape, 255, dtype=np.uint8),
        )
    if size[0] >= image.width and size[1] >= image.height:
        nearest = image.resize(size, resample=Img.Resampling.NEAREST)
        try:
            return (
                make_index_image(
                    np.asarray(nearest, dtype=np.uint8).copy(),
                    tile_names,
                ),
                np.full((size[1], size[0]), 255, dtype=np.uint8),
            )
        finally:
            nearest.close()

    best_score = np.zeros((size[1], size[0]), dtype=np.uint8)
    best_label = np.zeros((size[1], size[0]), dtype=np.uint8)
    first = True
    for label_value in np.unique(indices):
        label = int(label_value)
        mask = Img.fromarray(
            (indices == label).astype(np.uint8) * 255,
            mode="L",
        )
        try:
            coverage_image = mask.resize(
                size,
                resample=Img.Resampling.BOX,
            )
            try:
                coverage = np.asarray(
                    coverage_image,
                    dtype=np.uint8,
                )
                replace = coverage > best_score
                if first:
                    replace = np.ones(replace.shape, dtype=bool)
                    first = False
                best_score[replace] = coverage[replace]
                best_label[replace] = label
            finally:
                coverage_image.close()
        finally:
            mask.close()
    return make_index_image(best_label, tile_names), best_score


def derive_semantic_raster(
    tile_image: Image,
    tile_names: Sequence[str],
    confidence: Optional[np.ndarray] = None,
) -> SemanticRaster:
    indices = np.asarray(tile_image, dtype=np.uint8)
    if indices.ndim != 2:
        raise ValueError("tile image must contain one categorical band")

    shape = indices.shape
    landform = np.zeros(shape, dtype=np.uint8)
    substrate = np.zeros(shape, dtype=np.uint8)
    hydrology = np.zeros(shape, dtype=np.uint8)
    biotope = np.full(shape, NO_CLASS, dtype=np.uint8)
    theme = np.zeros(shape, dtype=np.uint8)
    if confidence is None:
        confidence_layer = np.full(shape, 255, dtype=np.uint8)
    else:
        confidence_layer = np.asarray(confidence, dtype=np.uint8).copy()
        if confidence_layer.shape != shape:
            raise ValueError(
                "semantic confidence must match the tile raster"
            )

    biotope_names = tuple(
        sorted(
            {
                parsed
                for tile in tile_names
                for parsed in (_parse_biotope(tile),)
                if parsed is not None and parsed not in THEME_BIOTOPES
            }
        )
    )
    biotope_lookup = {
        identifier: index for index, identifier in enumerate(biotope_names)
    }
    theme_lookup = {
        identifier: index for index, identifier in enumerate(THEME_CLASSES)
    }

    for tile_index_value in np.unique(indices):
        tile_index = int(tile_index_value)
        if tile_index >= len(tile_names):
            confidence_layer[indices == tile_index] = 0
            continue
        tile = tile_names[tile_index]
        mask = indices == tile_index
        if tile == "deep_ocean":
            hydrology[mask] = 1
            continue
        if tile == "close_ocean":
            hydrology[mask] = 2
            continue
        if tile == "shallow_waters":
            hydrology[mask] = 3
            continue
        if tile == "sand":
            landform[mask] = 1
            substrate[mask] = 2
            continue
        if tile == "hills":
            landform[mask] = 4
            substrate[mask] = 3
            continue
        if tile == "mountains":
            landform[mask] = 5
            substrate[mask] = 4
            continue
        if tile.startswith("soil_low"):
            landform[mask] = 2
            substrate[mask] = 1
        elif tile.startswith("soil_high"):
            landform[mask] = 3
            substrate[mask] = 1
        else:
            landform[mask] = 1
            substrate[mask] = 4
            confidence_layer[mask] = np.minimum(
                confidence_layer[mask],
                96,
            )

        parsed_biotope = _parse_biotope(tile)
        if parsed_biotope in theme_lookup and parsed_biotope != "none":
            theme[mask] = theme_lookup[parsed_biotope]
        elif parsed_biotope in biotope_lookup:
            biotope[mask] = biotope_lookup[parsed_biotope]

    hostable = (
        (hydrology == 0)
        & np.isin(landform, np.asarray((1, 2, 3, 6), dtype=np.uint8))
        & np.isin(substrate, np.asarray((1, 2, 3), dtype=np.uint8))
    )
    biotope[~hostable] = NO_CLASS
    result = SemanticRaster(
        landform=landform,
        substrate=substrate,
        hydrology=hydrology,
        biotope=biotope,
        theme=theme,
        hostable=hostable,
        confidence=confidence_layer,
        biotope_classes=biotope_names,
    )
    result.validate()
    return result


def write_semantic_diagnostics(
    output_path: Path,
    semantic: SemanticRaster,
) -> None:
    semantic.validate()
    directory = output_path / SEMANTIC_DIAGNOSTICS_DIRECTORY
    directory.mkdir(exist_ok=True, parents=True)
    _save_categorical(
        directory / "landform.png",
        semantic.landform,
        LANDFORM_COLORS,
    )
    _save_categorical(
        directory / "substrate.png",
        semantic.substrate,
        SUBSTRATE_COLORS,
    )
    _save_categorical(
        directory / "hydrology.png",
        semantic.hydrology,
        HYDROLOGY_COLORS,
    )
    _save_categorical(
        directory / "theme.png",
        semantic.theme,
        THEME_COLORS,
    )
    _save_categorical(
        directory / "biotope.png",
        semantic.biotope,
        _biotope_colors(len(semantic.biotope_classes)),
        nodata=(30, 35, 42),
    )
    _save_grayscale(
        directory / "hostability.png",
        semantic.hostable.astype(np.uint8) * 255,
    )
    _save_grayscale(
        directory / "confidence.png",
        semantic.confidence,
    )
    (directory / "semantic.json").write_text(
        json.dumps(semantic.metadata(), ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


def make_index_image(
    indices: np.ndarray,
    tile_names: Sequence[str],
) -> Image:
    if len(tile_names) > 256:
        raise ValueError("WorldBox tile palette cannot exceed 256 entries")
    image = Img.fromarray(indices.astype(np.uint8, copy=False), mode="P")
    palette = []
    for tile in tile_names:
        palette.extend(TILES[tile])
    palette.extend([0, 0, 0] * (256 - len(tile_names)))
    image.putpalette(palette[: 256 * 3])
    return image


def _parse_biotope(tile: str) -> Optional[str]:
    if ":" not in tile:
        return None
    suffix = tile.split(":", 1)[1]
    if suffix.endswith("_low"):
        return suffix[:-4]
    if suffix.endswith("_high"):
        return suffix[:-5]
    return None


def _save_categorical(
    path: Path,
    values: np.ndarray,
    colors: Sequence[Tuple[int, int, int]],
    nodata: Tuple[int, int, int] = (0, 0, 0),
) -> None:
    rgb = np.empty(values.shape + (3,), dtype=np.uint8)
    rgb[:] = nodata
    for index, color in enumerate(colors):
        rgb[values == index] = color
    image = Img.fromarray(rgb, mode="RGB")
    try:
        image.save(path, optimize=True)
    finally:
        image.close()


def _save_grayscale(path: Path, values: np.ndarray) -> None:
    image = Img.fromarray(values.astype(np.uint8, copy=False), mode="L")
    try:
        image.save(path, optimize=True)
    finally:
        image.close()


def _biotope_colors(count: int) -> Tuple[Tuple[int, int, int], ...]:
    colors = []
    for index in range(count):
        hue = (index * 0.618033988749895) % 1.0
        sector = int(hue * 6.0)
        fraction = hue * 6.0 - sector
        high = 218
        low = 82
        middle = int(low + (high - low) * fraction)
        colors.append(
            (
                (high, middle, low),
                (middle, high, low),
                (low, high, middle),
                (low, middle, high),
                (middle, low, high),
                (high, low, middle),
            )[sector % 6]
        )
    return tuple(colors)
