from typing import Iterable, Optional, Tuple

from copy import deepcopy
from decimal import ROUND_CEILING as CEILING, ROUND_HALF_EVEN as HALF_EVEN, Decimal

from itertools import groupby
from math import ceil
import zlib

from PIL import Image as Img
from PIL.Image import Image

from .calibration import (
    ClassificationProfile,
    apply_manual_classification,
    rasterize_map_boundary,
)
from .consts import CHUNK_SIZE, MAP_TEMPLATE, MAX_MAP_CELLS, SAFE_TILES_TUPLE, TILES
from .models import Map
from .terrain import classify_adaptive_terrain
from .utils import batched, json_dumps, make_palette


def quantize(
    image: Image,
    palette: Image,
    dither: bool,
    width: int,
    height: int,
) -> Tuple[Image, Tuple[int, int]]:
    """Quantize the input image using the WorldBox tile colour palette.

    Parameters
    ----------
    image : PIL.Image.Image
        The image to be quantized
    palette : PIL.Image.Image
        The palette to be quantized with the image
    dither : bool
        Enable dithering for smoother colour transitions
    width : int
        Target width of the map. Set to 0 for automatic sizing
    height : int
        Target height of the map. Set to 0 for automatic sizing

    Returns
    -------
    Tuple[PIL.Image.Image, Tuple[int, int]]
        A tuple containing two values:
            1. The quantized image
            2. The width and height of the map

    Raises
    ------
    ValueError
        If width or height is lower than 0
    """
    image, (width, height) = resize_to_map(
        image=image,
        width=width,
        height=height,
        resample=Img.Resampling.NEAREST,
    )
    palette_image = image.quantize(palette=palette, dither=int(dither))

    return palette_image, (width, height)


def resize_to_map(
    image: Image,
    width: int,
    height: int,
    resample: int,
) -> Tuple[Image, Tuple[int, int]]:
    if width < 0 or height < 0:
        raise ValueError("width and height cannot be lower than 0")

    width_dcm = Decimal(width)
    height_dcm = Decimal(height)

    temp_width = Decimal(max(round(image.size[0] / CHUNK_SIZE), 1))
    temp_height = Decimal(max(round(image.size[1] / CHUNK_SIZE), 1))
    ratio = temp_height / temp_width

    if width_dcm == 0:
        if height_dcm == 0:
            width_dcm, height_dcm = temp_width, temp_height
        else:
            width_dcm = max(
                (height / ratio).to_integral_exact(rounding=HALF_EVEN),
                Decimal(1),
            )
    elif height_dcm == 0:
        height_dcm = max(
            (width * ratio).to_integral_exact(rounding=HALF_EVEN),
            Decimal(1),
        )

    map_width = int(width_dcm)
    map_height = int(height_dcm)
    validate_map_size(map_width, map_height)

    size = (
        map_width * CHUNK_SIZE,
        int(
            ((height_dcm / width_dcm) * width_dcm * CHUNK_SIZE).to_integral_exact(
                rounding=CEILING,
            ),
        ),
    )
    resized_image = image.resize(size=size, resample=resample).convert("RGB")

    return resized_image, (map_width, map_height)


def fit_map_size_to_budget(
    pixel_width: int,
    pixel_height: int,
    maximum_cells: int = MAX_MAP_CELLS,
) -> Tuple[int, int]:
    """Fit an image aspect ratio to the largest practical WorldBox grid."""
    if pixel_width <= 0 or pixel_height <= 0:
        raise ValueError("image dimensions must be greater than 0")
    if maximum_cells < CHUNK_SIZE * CHUNK_SIZE:
        raise ValueError("maximum cell budget cannot fit one WorldBox block")

    maximum_blocks = maximum_cells // (CHUNK_SIZE * CHUNK_SIZE)
    target_ratio = pixel_height / pixel_width
    best_width = 1
    best_height = 1
    best_score = 0.0
    best_area = 0
    best_error = float("inf")

    for map_width in range(1, maximum_blocks + 1):
        maximum_height = maximum_blocks // map_width
        ideal_height = map_width * target_ratio
        candidates = {
            1,
            maximum_height,
            max(1, min(maximum_height, int(ideal_height))),
            max(1, min(maximum_height, ceil(ideal_height))),
            max(1, min(maximum_height, int(round(ideal_height)))),
        }
        for map_height in candidates:
            area = map_width * map_height
            relative_error = abs(map_height / map_width - target_ratio) / target_ratio
            score = area / (1.0 + 4.0 * relative_error)
            if (
                score > best_score
                or score == best_score
                and (
                    area > best_area
                    or area == best_area
                    and relative_error < best_error
                )
            ):
                best_width = map_width
                best_height = map_height
                best_score = score
                best_area = area
                best_error = relative_error

    validate_map_size(best_width, best_height)
    return best_width, best_height


def validate_map_size(width: int, height: int) -> None:
    """Validate the shared TerrainLab cell budget without restricting aspect."""
    if width <= 0 or height <= 0:
        raise ValueError("width and height must be greater than 0")

    cells = width * CHUNK_SIZE * height * CHUNK_SIZE
    if cells > MAX_MAP_CELLS:
        raise ValueError(
            f"map has {cells:,} cells; the TerrainLab limit is "
            f"{MAX_MAP_CELLS:,} cells",
        )


def build_map(
    tile_image: Image,
    width: int,
    height: int,
    tiles: Iterable[str],
    name: str,
    elevation=None,
    classification_profile: Optional[ClassificationProfile] = None,
) -> Map:
    tiles = tuple(tiles)
    flipped_image = tile_image.transpose(Img.Transpose.FLIP_TOP_BOTTOM)
    tile_array, tile_amounts = [], []

    for batch in batched(flipped_image.getdata(), width * CHUNK_SIZE):
        array, amounts = zip(
            *((key, sum(1 for _ in group)) for key, group in groupby(batch))
        )

        tile_array.append(array)
        tile_amounts.append(amounts)

    map_data = deepcopy(MAP_TEMPLATE)
    map_data["width"] = width
    map_data["height"] = height
    map_data["camera_pos_x"] = float(width * CHUNK_SIZE // 2)
    map_data["camera_pos_y"] = float(height * CHUNK_SIZE // 2)
    map_data["camera_zoom"] = float(max(width, height) * 20)
    map_data["mapStats"]["name"] = name
    map_data["tileMap"] = tiles
    map_data["tileArray"] = tile_array
    map_data["tileAmounts"] = tile_amounts

    return Map(
        data=zlib.compress(json_dumps(map_data), 9),
        width=width,
        height=height,
        preview=tile_image,
        elevation=elevation,
        classification_profile=(
            classification_profile.to_json_dict()
            if classification_profile is not None
            else None
        ),
    )


def convert(
    image: Image,
    dither: bool = False,
    width: int = 0,
    height: int = 0,
    tiles: Iterable[str] = SAFE_TILES_TUPLE,
    name: str = "Define Imagination",
    algorithm: str = "terrain",
    terrain_clusters: int = 14,
    terrain_smooth: int = 1,
    terrain_min_region: int = 32,
    classification_profile: Optional[ClassificationProfile] = None,
) -> Map:
    """Convert an image to a WorldBox map.

    Parameters
    ----------
    image : PIL.Image.Image
        The image to be converted
    dither : bool, default: False
        Enable dithering for smoother colour transitions
    width : int, default: 0
        Target width of the map. Set to 0 for automatic sizing
    height : int, default: 0
        Target height of the map. Set to 0 for automatic sizing
    tiles : iterable of str, default: imagetomap.consts.SAFE_TILES_TUPLE
        An iterable object that yields tile names that will be used
    name : str, default: "Define Imagination"
        The world name stored in the generated map metadata
    algorithm : str, default: "terrain"
        Conversion algorithm. Use "terrain" for adaptive playable terrain
        classification.
    terrain_min_region : int, default: 32
        Remove isolated land components smaller than this many tiles.

    Returns
    -------
    imagetomap.models.Map
        The converted map
    """
    tiles = tuple(dict.fromkeys(tiles))
    if not tiles:
        raise ValueError("at least one WorldBox tile must be enabled")

    unknown_tiles = tuple(tile for tile in tiles if tile not in TILES)
    if unknown_tiles:
        raise ValueError(f"unknown WorldBox tiles: {', '.join(unknown_tiles)}")
    if algorithm not in {"terrain", "palette"}:
        raise ValueError("algorithm must be either 'terrain' or 'palette'")
    if terrain_clusters < 4 or terrain_clusters > 64:
        raise ValueError("terrain_clusters must be between 4 and 64")
    if terrain_smooth < 0 or terrain_smooth > 8:
        raise ValueError("terrain_smooth must be between 0 and 8")
    if terrain_min_region < 0 or terrain_min_region > 4096:
        raise ValueError("terrain_min_region must be between 0 and 4096")

    if algorithm == "terrain":
        resized_image, (width, height) = resize_to_map(
            image=image,
            width=width,
            height=height,
            resample=Img.Resampling.LANCZOS,
        )
        boundary_mask = (
            rasterize_map_boundary(
                classification_profile,
                resized_image.width,
                resized_image.height,
            )
            if classification_profile is not None
            else None
        )
        tile_image = classify_adaptive_terrain(
            image=resized_image,
            tiles=tiles,
            clusters=terrain_clusters,
            smooth_passes=terrain_smooth,
            min_land_region=terrain_min_region,
            valid_mask=boundary_mask,
        )
        elevation = None
        if classification_profile is not None:
            manual_tile_image, elevation = apply_manual_classification(
                image=resized_image,
                automatic_tiles=tile_image,
                tile_names=tiles,
                profile=classification_profile,
                boundary_mask=boundary_mask,
            )
            tile_image.close()
            tile_image = manual_tile_image
        return build_map(
            tile_image=tile_image,
            width=width,
            height=height,
            tiles=tiles,
            name=name,
            elevation=elevation,
            classification_profile=classification_profile,
        )

    palette = make_palette(tiles=tiles)
    quantized_image, (width, height) = quantize(
        image=image,
        palette=palette,
        dither=dither,
        width=width,
        height=height,
    )
    return build_map(
        tile_image=quantized_image,
        width=width,
        height=height,
        tiles=tiles,
        name=name,
    )
