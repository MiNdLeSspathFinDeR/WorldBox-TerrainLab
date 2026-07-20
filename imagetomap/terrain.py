import colorsys
from collections import deque
from dataclasses import dataclass
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

import numpy as np
from PIL import Image as Img
from PIL.Image import Image

from .consts import TILES


LIVING_SOIL_BIOMES = (
    "grass",
    "savanna",
    "jungle",
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
CLUSTER_SURFACE_IDS = (
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
CLUSTER_BIOTOPE_IDS = LIVING_SOIL_BIOMES


@dataclass(frozen=True)
class TerrainState:
    luma_low: float
    luma_mid: float
    luma_high: float
    sat_mid: float
    sat_high: float
    slope_mid: float
    slope_high: float
    water_threshold: float


@dataclass(frozen=True)
class TerrainClusteringSettings:
    clusters: int = 14
    spline_radius: int = 0
    smooth_passes: int = 1
    min_land_region: int = 32
    water_sensitivity: float = 1.0
    color_weight: float = 1.0
    luma_weight: float = 1.0
    saturation_weight: float = 1.0
    texture_weight: float = 0.0
    slope_weight: float = 1.0
    spatial_weight: float = 0.0
    detail_weight: float = 0.65
    sample_limit: int = 60_000
    kmeans_iterations: int = 18
    random_seed: int = 1729
    allowed_surfaces: Tuple[str, ...] = CLUSTER_SURFACE_IDS
    allowed_biotopes: Tuple[str, ...] = CLUSTER_BIOTOPE_IDS


def classify_adaptive_terrain(
    image: Image,
    tiles: Iterable[str],
    clusters: int = 14,
    smooth_passes: int = 1,
    min_land_region: int = 32,
    valid_mask: Optional[np.ndarray] = None,
    clustering_settings: Optional[TerrainClusteringSettings] = None,
) -> Image:
    """Classify a map image into playable WorldBox terrain tiles.

    The classifier is intentionally deterministic and local: it adapts to the
    source map's own luminance/saturation range, detects water before land
    clustering, then classifies land clusters with slope and color features.
    """
    tile_names = tuple(tile for tile in tiles if tile in TILES)
    if not tile_names:
        raise ValueError("No valid WorldBox tiles supplied")
    settings = clustering_settings or TerrainClusteringSettings(
        clusters=clusters,
        smooth_passes=smooth_passes,
        min_land_region=min_land_region,
    )
    validate_clustering_settings(settings)

    rgb = np.asarray(image.convert("RGB"), dtype=np.float32) / 255.0
    if valid_mask is None:
        analysis_mask = np.ones(rgb.shape[:2], dtype=bool)
    else:
        analysis_mask = np.asarray(valid_mask, dtype=bool)
        if analysis_mask.shape != rgb.shape[:2]:
            raise ValueError(
                "terrain classification mask must match the image"
            )
        if not np.any(analysis_mask):
            raise ValueError("terrain classification mask is empty")
        if "deep_ocean" not in tile_names:
            raise ValueError(
                "terrain classification mask requires deep_ocean"
            )
        interior = rgb[analysis_mask]
        stride = max(1, interior.shape[0] // 60_000)
        fill = np.median(interior[::stride], axis=0)
        rgb = rgb.copy()
        rgb[~analysis_mask] = fill

    feature_rgb = make_spline_feature_rgb(
        rgb,
        settings.spline_radius,
        settings.detail_weight,
    )
    red = feature_rgb[:, :, 0]
    green = feature_rgb[:, :, 1]
    blue = feature_rgb[:, :, 2]

    hue, saturation, value = rgb_to_hsv(feature_rgb)
    luma = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue)
    dark_percentile = float(np.percentile(luma[analysis_mask], 0.5))
    void_threshold = min(max(dark_percentile * 1.25, 0.018), 0.06)
    void_mask = (luma <= void_threshold) & analysis_mask
    non_void = (~void_mask) & analysis_mask

    luma_low, luma_mid, luma_high = percentiles(luma[non_void], (4.0, 50.0, 96.0))
    sat_mid, sat_high = percentiles(saturation[non_void], (50.0, 82.0))
    luma_norm = normalize(luma, luma_low, luma_high)

    slope = gradient_magnitude(luma_norm)
    slope_mid, slope_high = percentiles(slope[non_void], (50.0, 86.0))

    water_score = make_water_score(red, green, blue, hue, saturation, luma_norm)
    water_threshold = max(
        0.02,
        adaptive_water_threshold(water_score, non_void)
        / settings.water_sensitivity,
    )
    state = TerrainState(
        luma_low=luma_low,
        luma_mid=luma_mid,
        luma_high=luma_high,
        sat_mid=sat_mid,
        sat_high=sat_high,
        slope_mid=slope_mid,
        slope_high=slope_high,
        water_threshold=water_threshold,
    )

    indices = np.full(
        luma.shape,
        living_soil_index(tile_names, "low"),
        dtype=np.uint8,
    )
    water_mask = detect_water(water_score, hue, saturation, luma_norm, non_void, state)
    if state.sat_mid < 0.28:
        water_mask |= detect_muted_boundary_water(
            saturation=saturation,
            luma_norm=luma_norm,
            non_void=non_void,
            void_mask=void_mask,
        )
    water_mask |= void_mask
    water_mask |= ~analysis_mask
    if settings.min_land_region > 1:
        water_mask = fill_small_land_regions(
            water_mask,
            settings.min_land_region,
        )

    assign_water(indices, water_mask, void_mask, tile_names)
    if valid_mask is not None:
        indices[~analysis_mask] = tile_index(tile_names, "deep_ocean")

    water_class_count = count_tile_classes(indices, water_mask)
    land_mask = analysis_mask & ~water_mask
    assign_land_clusters(
        indices=indices,
        tile_names=tile_names,
        land_mask=land_mask,
        luma_norm=luma_norm,
        saturation=saturation,
        hue=hue,
        slope=slope,
        red=red,
        green=green,
        blue=blue,
        settings=settings,
        state=state,
        cluster_budget=max(1, settings.clusters - water_class_count),
    )

    if settings.smooth_passes > 0:
        indices = smooth_tiles(
            indices,
            water_mask,
            tile_names,
            passes=settings.smooth_passes,
        )
    indices = replace_bare_soil(indices, tile_names)
    indices = constrain_cluster_composition(
        indices,
        tile_names,
        settings,
        analysis_mask,
    )
    if valid_mask is not None:
        indices[~analysis_mask] = tile_index(tile_names, "deep_ocean")
    indices = enforce_cluster_budget(
        indices,
        tile_names,
        settings.clusters,
    )

    return make_index_image(indices, tile_names)


def validate_clustering_settings(settings: TerrainClusteringSettings) -> None:
    if settings.clusters < 4 or settings.clusters > 64:
        raise ValueError("clustering clusters must be between 4 and 64")
    if settings.spline_radius < 0 or settings.spline_radius > 12:
        raise ValueError("clustering spline_radius must be between 0 and 12")
    if settings.smooth_passes < 0 or settings.smooth_passes > 8:
        raise ValueError("clustering smooth_passes must be between 0 and 8")
    if settings.min_land_region < 0 or settings.min_land_region > 4096:
        raise ValueError("clustering min_land_region must be between 0 and 4096")
    if settings.sample_limit < 1000 or settings.sample_limit > 250_000:
        raise ValueError("clustering sample_limit must be between 1000 and 250000")
    if settings.kmeans_iterations < 1 or settings.kmeans_iterations > 100:
        raise ValueError(
            "clustering kmeans_iterations must be between 1 and 100"
        )
    if settings.random_seed < 0 or settings.random_seed > 2_147_483_647:
        raise ValueError("clustering random_seed is outside supported range")
    if not settings.allowed_surfaces:
        raise ValueError("clustering requires at least one surface class")
    if not settings.allowed_biotopes:
        raise ValueError("clustering requires at least one biotope class")
    if any(
        surface not in CLUSTER_SURFACE_IDS
        for surface in settings.allowed_surfaces
    ):
        raise ValueError("clustering contains an unsupported surface class")
    if any(
        biotope not in CLUSTER_BIOTOPE_IDS
        for biotope in settings.allowed_biotopes
    ):
        raise ValueError("clustering contains an unsupported biotope class")
    weighted = (
        settings.color_weight,
        settings.luma_weight,
        settings.saturation_weight,
        settings.texture_weight,
        settings.slope_weight,
        settings.spatial_weight,
    )
    if any(not np.isfinite(value) or value < 0.0 or value > 3.0 for value in weighted):
        raise ValueError("clustering feature weights must be between 0 and 3")
    if sum(weighted) <= 0.0:
        raise ValueError("at least one clustering feature weight must be positive")
    if (
        not np.isfinite(settings.water_sensitivity)
        or settings.water_sensitivity < 0.5
        or settings.water_sensitivity > 2.0
    ):
        raise ValueError(
            "clustering water_sensitivity must be between 0.5 and 2"
        )
    if (
        not np.isfinite(settings.detail_weight)
        or settings.detail_weight < 0.0
        or settings.detail_weight > 1.0
    ):
        raise ValueError("clustering detail_weight must be between 0 and 1")


def make_spline_feature_rgb(
    rgb: np.ndarray,
    radius: int,
    detail_weight: float,
) -> np.ndarray:
    if radius <= 0:
        return rgb
    smoothed = np.stack(
        tuple(smooth_float(rgb[:, :, channel], radius) for channel in range(3)),
        axis=2,
    )
    detail = float(np.clip(detail_weight, 0.0, 1.0))
    return (
        smoothed * (1.0 - detail) + rgb * detail
    ).astype(np.float32)


def rgb_to_hsv(rgb: np.ndarray) -> Tuple[np.ndarray, np.ndarray, np.ndarray]:
    red = rgb[:, :, 0]
    green = rgb[:, :, 1]
    blue = rgb[:, :, 2]

    max_channel = np.max(rgb, axis=2)
    min_channel = np.min(rgb, axis=2)
    delta = max_channel - min_channel

    hue = np.zeros_like(max_channel)
    non_zero = delta > 1e-6

    red_max = (max_channel == red) & non_zero
    green_max = (max_channel == green) & non_zero
    blue_max = (max_channel == blue) & non_zero

    hue[red_max] = ((green[red_max] - blue[red_max]) / delta[red_max]) % 6.0
    hue[green_max] = ((blue[green_max] - red[green_max]) / delta[green_max]) + 2.0
    hue[blue_max] = ((red[blue_max] - green[blue_max]) / delta[blue_max]) + 4.0
    hue /= 6.0

    saturation = np.zeros_like(max_channel)
    non_black = max_channel > 1e-6
    saturation[non_black] = delta[non_black] / max_channel[non_black]

    return hue, saturation, max_channel


def percentiles(values: np.ndarray, points: Sequence[float]) -> Tuple[float, ...]:
    if values.size == 0:
        return tuple(0.0 for _ in points)
    return tuple(float(value) for value in np.percentile(values, points))


def normalize(values: np.ndarray, low: float, high: float) -> np.ndarray:
    if high <= low:
        return np.zeros_like(values, dtype=np.float32)
    return np.clip((values - low) / (high - low), 0.0, 1.0).astype(np.float32)


def gradient_magnitude(values: np.ndarray) -> np.ndarray:
    y_grad = np.zeros_like(values, dtype=np.float32)
    x_grad = np.zeros_like(values, dtype=np.float32)
    y_grad[1:, :] = values[1:, :] - values[:-1, :]
    x_grad[:, 1:] = values[:, 1:] - values[:, :-1]
    return np.sqrt((x_grad * x_grad) + (y_grad * y_grad)).astype(np.float32)


def local_roughness(values: np.ndarray) -> np.ndarray:
    padded = np.pad(values, 1, mode="edge")
    center = padded[1:-1, 1:-1]
    roughness = np.zeros(values.shape, dtype=np.float32)
    roughness += np.abs(center - padded[:-2, 1:-1])
    roughness += np.abs(center - padded[2:, 1:-1])
    roughness += np.abs(center - padded[1:-1, :-2])
    roughness += np.abs(center - padded[1:-1, 2:])
    return roughness / 4.0


def smooth_float(values: np.ndarray, passes: int) -> np.ndarray:
    result = values.astype(np.float32, copy=True)
    for _ in range(passes):
        padded = np.pad(result, 1, mode="edge")
        smoothed = np.zeros(result.shape, dtype=np.float32)
        for row_offset in range(3):
            for col_offset in range(3):
                smoothed += padded[
                    row_offset : row_offset + result.shape[0],
                    col_offset : col_offset + result.shape[1],
                ]
        result = smoothed / 9.0
    return result


def make_water_score(
    red: np.ndarray,
    green: np.ndarray,
    blue: np.ndarray,
    hue: np.ndarray,
    saturation: np.ndarray,
    luma_norm: np.ndarray,
) -> np.ndarray:
    coolness = (blue - red) + (0.35 * (green - red))
    blue_hue = ((hue >= 0.45) & (hue <= 0.72)).astype(np.float32)
    cyan_hue = ((hue >= 0.38) & (hue < 0.45)).astype(np.float32)
    score = (1.55 * coolness) + (0.42 * blue_hue) + (0.22 * cyan_hue)
    score += 0.18 * saturation
    score -= 0.16 * np.maximum(luma_norm - 0.80, 0.0)
    return score.astype(np.float32)


def adaptive_water_threshold(water_score: np.ndarray, non_void: np.ndarray) -> float:
    values = water_score[non_void]
    if values.size == 0:
        return 0.0
    high = float(np.percentile(values, 88.0))
    mid = float(np.percentile(values, 65.0))
    return max((high * 0.72) + (mid * 0.28), 0.06)


def detect_water(
    water_score: np.ndarray,
    hue: np.ndarray,
    saturation: np.ndarray,
    luma_norm: np.ndarray,
    non_void: np.ndarray,
    state: TerrainState,
) -> np.ndarray:
    cool_hue = ((hue >= 0.40) & (hue <= 0.74)) | (water_score > state.water_threshold * 1.35)
    not_snow = ~((luma_norm > 0.86) & (saturation < max(state.sat_mid * 0.65, 0.12)))
    water = non_void & cool_hue & not_snow & (water_score >= state.water_threshold)

    neighbors = neighbor_count(water)
    strong_water = water_score >= state.water_threshold + 0.12
    water &= (neighbors >= 2) | strong_water
    return water


def detect_muted_boundary_water(
    saturation: np.ndarray,
    luma_norm: np.ndarray,
    non_void: np.ndarray,
    void_mask: np.ndarray,
) -> np.ndarray:
    """Find desaturated water connected to the map paper's outer boundary."""
    if not np.any(non_void):
        return np.zeros(non_void.shape, dtype=bool)

    roughness = smooth_float(local_roughness(luma_norm), passes=2)
    sat_cut = float(np.percentile(saturation[non_void], 55.0))
    rough_cut = float(np.percentile(roughness[non_void], 60.0))

    candidate = (
        non_void
        & (saturation <= sat_cut)
        & (roughness <= rough_cut)
        & (luma_norm < 0.72)
    )
    candidate = binary_close(candidate, iterations=3)

    coarse_size = fit_size(candidate.shape, max_dimension=512)
    coarse_candidate = resize_mask(candidate, coarse_size, threshold=0.38)
    coarse_candidate = binary_close(coarse_candidate, iterations=1)
    coarse_void = resize_mask(void_mask, coarse_size, threshold=0.10)
    seed_zone = binary_dilate(coarse_void, iterations=2)

    edge = np.zeros(coarse_candidate.shape, dtype=bool)
    edge[[0, -1], :] = True
    edge[:, [0, -1]] = True
    seeds = coarse_candidate & (seed_zone | edge)
    if not np.any(seeds):
        return np.zeros(candidate.shape, dtype=bool)

    connected = flood_connected(coarse_candidate, seeds)
    full_size = (candidate.shape[1], candidate.shape[0])
    return resize_mask(connected, full_size, threshold=0.50) & candidate


def assign_water(
    indices: np.ndarray,
    water_mask: np.ndarray,
    void_mask: np.ndarray,
    tile_names: Sequence[str],
) -> None:
    if not np.any(water_mask):
        return

    inner = binary_erode(water_mask, iterations=2, border_value=True)
    deep = binary_erode(inner, iterations=4, border_value=True) | void_mask
    close = inner & ~deep
    shallow = water_mask & ~inner & ~deep

    indices[deep] = tile_index(tile_names, "deep_ocean")
    indices[close] = tile_index(tile_names, "close_ocean", "shallow_waters")
    indices[shallow] = tile_index(tile_names, "shallow_waters", "close_ocean")


def assign_land_clusters(
    indices: np.ndarray,
    tile_names: Sequence[str],
    land_mask: np.ndarray,
    luma_norm: np.ndarray,
    saturation: np.ndarray,
    hue: np.ndarray,
    slope: np.ndarray,
    red: np.ndarray,
    green: np.ndarray,
    blue: np.ndarray,
    settings: TerrainClusteringSettings,
    state: TerrainState,
    cluster_budget: int,
) -> None:
    land_positions = np.flatnonzero(land_mask.ravel())
    if land_positions.size == 0:
        return

    slope_norm = normalize(slope, state.slope_mid * 0.35, state.slope_high)
    features = make_features(
        luma_norm,
        saturation,
        hue,
        slope_norm,
        red,
        green,
        blue,
        settings,
    )
    flat_features = features.reshape((-1, features.shape[-1]))
    land_features = flat_features[land_positions]

    centers = fit_kmeans(
        land_features,
        max(1, cluster_budget),
        sample_limit=settings.sample_limit,
        iterations=settings.kmeans_iterations,
        random_seed=settings.random_seed,
    )
    cluster_labels = np.empty(land_positions.size, dtype=np.uint8)
    chunk_size = 250_000
    for start in range(0, land_positions.size, chunk_size):
        stop = min(start + chunk_size, land_positions.size)
        cluster_labels[start:stop] = nearest_centers(
            land_features[start:stop],
            centers,
        )
    cluster_populations = np.bincount(
        cluster_labels,
        minlength=centers.shape[0],
    )
    center_tiles = classify_centers(
        centers,
        tile_names,
        state,
        settings,
        cluster_populations,
    )

    flat_indices = indices.ravel()
    for start in range(0, land_positions.size, chunk_size):
        stop = min(start + chunk_size, land_positions.size)
        pos = land_positions[start:stop]
        flat_indices[pos] = center_tiles[cluster_labels[start:stop]]


def make_features(
    luma_norm: np.ndarray,
    saturation: np.ndarray,
    hue: np.ndarray,
    slope: np.ndarray,
    red: np.ndarray,
    green: np.ndarray,
    blue: np.ndarray,
    settings: TerrainClusteringSettings,
) -> np.ndarray:
    green_score = green - np.maximum(red, blue)
    red_score = red - np.maximum(green, blue * 0.75)
    coolness = (blue - red) + (0.35 * (green - red))
    hue_angle = hue * (2.0 * np.pi)
    texture = local_roughness(luma_norm)
    height, width = luma_norm.shape
    x_axis = np.linspace(-0.5, 0.5, width, dtype=np.float32)
    y_axis = np.linspace(-0.5, 0.5, height, dtype=np.float32)
    spatial_x = np.broadcast_to(x_axis, (height, width))
    spatial_y = np.broadcast_to(y_axis[:, None], (height, width))

    return np.stack(
        (
            luma_norm * 1.45 * settings.luma_weight,
            saturation * 0.85 * settings.saturation_weight,
            np.sin(hue_angle) * 0.35 * settings.color_weight,
            np.cos(hue_angle) * 0.35 * settings.color_weight,
            slope * settings.slope_weight,
            green_score * 1.25 * settings.color_weight,
            red_score * 1.10 * settings.color_weight,
            coolness * 1.20 * settings.color_weight,
            texture * settings.texture_weight,
            spatial_x * settings.spatial_weight,
            spatial_y * settings.spatial_weight,
        ),
        axis=2,
    ).astype(np.float32)


def fit_kmeans(
    features: np.ndarray,
    clusters: int,
    sample_limit: int = 60_000,
    iterations: int = 18,
    random_seed: int = 1729,
) -> np.ndarray:
    sample_size = min(features.shape[0], sample_limit)
    rng = np.random.default_rng(random_seed)
    if features.shape[0] > sample_size:
        sample = features[rng.choice(features.shape[0], size=sample_size, replace=False)]
    else:
        sample = features

    unique_clusters = min(clusters, max(1, sample.shape[0]))
    centers = initialize_kmeans_centers(sample, unique_clusters, rng)

    for _ in range(iterations):
        labels = nearest_centers(sample, centers)
        next_centers = centers.copy()
        nearest_distance = np.sum(
            (sample - centers[labels]) ** 2,
            axis=1,
        )
        for label in range(unique_clusters):
            members = sample[labels == label]
            if members.size:
                next_centers[label] = members.mean(axis=0)
                continue

            replacement = int(np.argmax(nearest_distance))
            next_centers[label] = sample[replacement]
            nearest_distance[replacement] = -1.0
        if np.allclose(centers, next_centers, atol=1e-4):
            break
        centers = next_centers

    return centers


def initialize_kmeans_centers(
    sample: np.ndarray,
    clusters: int,
    rng: np.random.Generator,
) -> np.ndarray:
    """Choose deterministic k-means++ seeds without collapsing rare colours."""
    centers = np.empty((clusters, sample.shape[1]), dtype=sample.dtype)
    first = int(rng.integers(sample.shape[0]))
    centers[0] = sample[first]
    chosen = np.zeros(sample.shape[0], dtype=bool)
    chosen[first] = True
    closest_distance = np.sum((sample - centers[0]) ** 2, axis=1)

    for index in range(1, clusters):
        total_distance = float(np.sum(closest_distance))
        if total_distance > 1e-12:
            probabilities = closest_distance / total_distance
            selected = int(rng.choice(sample.shape[0], p=probabilities))
        else:
            remaining = np.flatnonzero(~chosen)
            selected = (
                int(remaining[int(rng.integers(remaining.size))])
                if remaining.size
                else int(rng.integers(sample.shape[0]))
            )

        centers[index] = sample[selected]
        chosen[selected] = True
        distance = np.sum((sample - centers[index]) ** 2, axis=1)
        closest_distance = np.minimum(closest_distance, distance)

    return centers


def nearest_centers(features: np.ndarray, centers: np.ndarray) -> np.ndarray:
    feature_norms = np.sum(features * features, axis=1, keepdims=True)
    center_norms = np.sum(centers * centers, axis=1, keepdims=True).T
    distances = feature_norms + center_norms - (2.0 * features @ centers.T)
    return np.argmin(distances, axis=1)


def classify_centers(
    centers: np.ndarray,
    tile_names: Sequence[str],
    state: TerrainState,
    settings: TerrainClusteringSettings,
    cluster_populations: Optional[np.ndarray] = None,
) -> np.ndarray:
    descriptors = []
    heuristic_tiles = []
    for index, center in enumerate(centers):
        luma = unweight(center[0], 1.45, settings.luma_weight, 0.5)
        saturation = unweight(
            center[1],
            0.85,
            settings.saturation_weight,
            state.sat_mid,
        )
        slope = unweight(center[4], 1.0, settings.slope_weight, 0.0)
        green_score = unweight(
            center[5],
            1.25,
            settings.color_weight,
            0.0,
        )
        red_score = unweight(
            center[6],
            1.10,
            settings.color_weight,
            0.0,
        )
        coolness = unweight(
            center[7],
            1.20,
            settings.color_weight,
            0.0,
        )
        hue_sine = unweight(
            center[2],
            0.35,
            settings.color_weight,
            0.0,
        )
        hue_cosine = unweight(
            center[3],
            0.35,
            settings.color_weight,
            1.0,
        )
        hue = float(
            (np.arctan2(hue_sine, hue_cosine) / (2.0 * np.pi)) % 1.0
        )

        tile = classify_land_tile(
            luma=luma,
            saturation=saturation,
            slope=slope,
            green_score=green_score,
            red_score=red_score,
            coolness=coolness,
            hue=hue,
            state=state,
        )
        descriptors.append(
            (
                luma,
                saturation,
                slope,
                green_score,
                red_score,
                coolness,
                hue,
            )
        )
        heuristic_tiles.append(tile)

    candidates = eligible_land_cluster_tiles(
        tile_names,
        settings,
    )
    if not candidates:
        raise ValueError(
            "selected clustering composition has no land tiles in the palette"
        )

    scores = cluster_candidate_scores(
        descriptors,
        heuristic_tiles,
        tile_names,
        candidates,
    )
    assignments = assign_unique_cluster_tiles(
        scores,
        cluster_populations,
    )
    return np.asarray(
        [candidates[assignment] for assignment in assignments],
        dtype=np.uint8,
    )


def eligible_land_cluster_tiles(
    tile_names: Sequence[str],
    settings: TerrainClusteringSettings,
) -> List[int]:
    allowed_surfaces = set(settings.allowed_surfaces)
    allowed_biotopes = set(settings.allowed_biotopes)
    water_surfaces = {
        "deep_ocean",
        "shelf",
        "shallow_water",
        "river_lake",
    }
    eligible = [
        index
        for index, tile in enumerate(tile_names)
        if tile not in {"soil_low", "soil_high"}
        and _tile_allowed_for_composition(
            tile,
            allowed_surfaces,
            allowed_biotopes,
        )
    ]
    land = [
        index
        for index in eligible
        if not set(_tile_surface_options(tile_names[index])) & water_surfaces
    ]
    return land or eligible


def cluster_candidate_scores(
    descriptors: Sequence[Tuple[float, ...]],
    heuristic_tiles: Sequence[str],
    tile_names: Sequence[str],
    candidates: Sequence[int],
) -> np.ndarray:
    candidate_descriptors = [
        cluster_tile_descriptor(tile_names[index])
        for index in candidates
    ]
    lumas = np.asarray(
        [descriptor[0] for descriptor in candidate_descriptors],
        dtype=np.float32,
    )
    luma_span = float(np.ptp(lumas))
    if luma_span > 1e-6:
        lumas = (lumas - float(np.min(lumas))) / luma_span
    else:
        lumas.fill(0.5)

    scores = np.empty(
        (len(descriptors), len(candidates)),
        dtype=np.float32,
    )
    for center_index, descriptor in enumerate(descriptors):
        (
            luma,
            saturation,
            slope,
            green_score,
            red_score,
            coolness,
            hue,
        ) = descriptor
        heuristic = heuristic_tiles[center_index]
        heuristic_base = heuristic.split(":", 1)[0]
        for candidate_position, candidate_index in enumerate(candidates):
            candidate_name = tile_names[candidate_index]
            (
                _,
                candidate_saturation,
                candidate_slope,
                candidate_green,
                candidate_red,
                candidate_coolness,
                candidate_hue,
            ) = candidate_descriptors[candidate_position]
            hue_distance = abs(hue - candidate_hue)
            hue_distance = min(hue_distance, 1.0 - hue_distance)
            hue_weight = 0.25 + (
                1.25 * max(saturation, candidate_saturation)
            )
            score = (
                abs(luma - float(lumas[candidate_position])) * 1.25
                + abs(saturation - candidate_saturation) * 0.70
                + hue_distance * hue_weight
                + abs(slope - candidate_slope) * 1.10
                + abs(green_score - candidate_green) * 0.30
                + abs(red_score - candidate_red) * 0.25
                + abs(coolness - candidate_coolness) * 0.25
            )
            candidate_base = candidate_name.split(":", 1)[0]
            if candidate_name == heuristic:
                score -= 0.70
            elif candidate_base == heuristic_base:
                score -= 0.32
            scores[center_index, candidate_position] = score
    return scores


def cluster_tile_descriptor(
    tile: str,
) -> Tuple[float, float, float, float, float, float, float]:
    red, green, blue = (
        channel / 255.0
        for channel in TILES[tile]
    )
    hue, saturation, _ = colorsys.rgb_to_hsv(red, green, blue)
    luma = 0.2126 * red + 0.7152 * green + 0.0722 * blue
    green_score = green - max(red, blue)
    red_score = red - max(green, blue * 0.75)
    coolness = (blue - red) + (0.35 * (green - red))
    base = tile.split(":", 1)[0]
    slope = {
        "sand": 0.04,
        "soil_low": 0.12,
        "soil_high": 0.34,
        "hills": 0.62,
        "mountains": 0.88,
    }.get(base, 0.0)
    return (
        luma,
        saturation,
        slope,
        green_score,
        red_score,
        coolness,
        hue,
    )


def assign_unique_cluster_tiles(
    scores: np.ndarray,
    cluster_populations: Optional[np.ndarray] = None,
) -> np.ndarray:
    """Give dominant source clusters first choice from the enabled palette."""
    center_count, candidate_count = scores.shape
    if cluster_populations is None:
        populations = np.ones(center_count, dtype=np.int64)
    else:
        populations = np.asarray(cluster_populations, dtype=np.int64)
        if populations.shape != (center_count,):
            raise ValueError(
                "cluster populations must match the number of centers"
            )
    assignments = np.full(center_count, -1, dtype=np.int32)
    unassigned = set(range(center_count))
    available = set(range(candidate_count))

    while unassigned and available:
        selected_center = -1
        selected_candidate = -1
        selected_rank = None
        available_array = np.asarray(sorted(available), dtype=np.int32)
        for center in sorted(unassigned):
            ordered = available_array[
                np.argsort(scores[center, available_array], kind="stable")
            ]
            best = int(ordered[0])
            best_score = float(scores[center, best])
            second_score = (
                float(scores[center, int(ordered[1])])
                if ordered.size > 1
                else best_score + 1_000_000.0
            )
            rank = (
                int(populations[center]),
                second_score - best_score,
                -best_score,
                -center,
            )
            if selected_rank is None or rank > selected_rank:
                selected_rank = rank
                selected_center = center
                selected_candidate = best

        assignments[selected_center] = selected_candidate
        unassigned.remove(selected_center)
        available.remove(selected_candidate)

    for center in sorted(unassigned):
        assignments[center] = int(np.argmin(scores[center]))
    return assignments


def count_tile_classes(
    indices: np.ndarray,
    mask: np.ndarray,
) -> int:
    if not np.any(mask):
        return 0
    return int(np.unique(indices[mask]).size)


def enforce_cluster_budget(
    indices: np.ndarray,
    tile_names: Sequence[str],
    maximum_classes: int,
) -> np.ndarray:
    """Collapse rare overflow classes into the nearest dominant class."""
    classes, counts = np.unique(indices, return_counts=True)
    if classes.size <= maximum_classes:
        return indices

    ranked = sorted(
        zip(classes.tolist(), counts.tolist()),
        key=lambda item: (-item[1], item[0]),
    )
    kept = [int(tile) for tile, _ in ranked[:maximum_classes]]
    water_bases = {"deep_ocean", "close_ocean", "shallow_waters"}
    result = indices.copy()

    for source_value, _ in ranked[maximum_classes:]:
        source = int(source_value)
        source_name = tile_names[source]
        source_is_water = source_name.split(":", 1)[0] in water_bases
        same_domain = [
            candidate
            for candidate in kept
            if (
                tile_names[candidate].split(":", 1)[0] in water_bases
            )
            == source_is_water
        ]
        candidates = same_domain or kept
        source_color = np.asarray(TILES[source_name], dtype=np.float32)
        replacement = min(
            candidates,
            key=lambda candidate: (
                float(
                    np.sum(
                        (
                            np.asarray(
                                TILES[tile_names[candidate]],
                                dtype=np.float32,
                            )
                            - source_color
                        )
                        ** 2
                    )
                ),
                candidate,
            ),
        )
        result[result == source] = replacement

    if np.unique(result).size > maximum_classes:
        raise RuntimeError("failed to enforce clustering class budget")
    return result


def unweight(
    value: float,
    scale: float,
    weight: float,
    fallback: float,
) -> float:
    divisor = scale * weight
    return fallback if divisor <= 1e-8 else float(value / divisor)


def classify_land_tile(
    luma: float,
    saturation: float,
    slope: float,
    green_score: float,
    red_score: float,
    coolness: float,
    hue: float,
    state: TerrainState,
) -> str:
    warm = 0.06 <= hue <= 0.18
    yellow = 0.11 <= hue <= 0.24
    greenish = 0.20 <= hue <= 0.43

    if luma > 0.82 and saturation < max(state.sat_mid, 0.18):
        return "soil_high:permafrost_high" if coolness > -0.08 else "sand"
    if slope > 0.58 and luma < 0.70:
        return "mountains" if luma < 0.46 or slope > 0.78 else "hills"
    if luma < 0.18:
        return "mountains" if slope > 0.34 else "soil_high:swamp_high"
    if coolness > 0.10 and luma > 0.55 and saturation < 0.25:
        return "soil_low:permafrost_low"
    if green_score > 0.08 and greenish:
        if luma < 0.38:
            return "soil_high:jungle_high"
        if saturation > 0.38:
            return "soil_low:jungle_low"
        return "soil_low:grass_low"
    if yellow and luma > 0.58:
        if saturation > 0.38:
            return "soil_low:savanna_low"
        return "soil_low:desert_low" if luma > 0.66 else "soil_high:desert_high"
    if warm and red_score > 0.05:
        return (
            "soil_high:savanna_high"
            if luma < 0.45
            else "soil_low:savanna_low"
        )
    if saturation < 0.16 and luma < 0.52:
        return "hills" if slope > 0.25 else "soil_high:grass_high"
    if saturation < 0.20 and luma > 0.70:
        return "sand"
    if luma < 0.36:
        return "soil_high:grass_high"
    return "soil_low:grass_low"


def smooth_tiles(
    indices: np.ndarray,
    water_mask: np.ndarray,
    tile_names: Sequence[str],
    passes: int,
) -> np.ndarray:
    water_tiles = {
        index
        for index, tile in enumerate(tile_names)
        if tile in {"deep_ocean", "close_ocean", "shallow_waters"}
    }
    result = indices.copy()
    for _ in range(passes):
        own_neighbors = np.zeros(result.shape, dtype=np.uint8)
        best_neighbors = np.zeros(result.shape, dtype=np.uint8)
        best_tile = result.copy()

        for tile in np.unique(result):
            tile_mask = result == tile
            counts = neighbor_count(tile_mask)
            own_neighbors[tile_mask] = counts[tile_mask]

            domain = water_mask if int(tile) in water_tiles else ~water_mask
            better = domain & (counts > best_neighbors)
            best_neighbors[better] = counts[better]
            best_tile[better] = tile

        next_result = result.copy()
        isolated = own_neighbors <= 1
        next_result[isolated] = best_tile[isolated]
        result = next_result
    return result


def constrain_cluster_composition(
    indices: np.ndarray,
    tile_names: Sequence[str],
    settings: TerrainClusteringSettings,
    active_mask: np.ndarray,
) -> np.ndarray:
    allowed_surfaces = set(settings.allowed_surfaces)
    allowed_biotopes = set(settings.allowed_biotopes)
    eligible = [
        index
        for index, tile in enumerate(tile_names)
        if tile not in {"soil_low", "soil_high"}
        and _tile_allowed_for_composition(
            tile,
            allowed_surfaces,
            allowed_biotopes,
        )
    ]
    if not eligible:
        raise ValueError(
            "selected clustering composition has no tiles in the palette"
        )

    result = indices.copy()
    water_surfaces = {
        "deep_ocean",
        "shelf",
        "shallow_water",
        "river_lake",
    }
    eligible_water = [
        index
        for index in eligible
        if set(_tile_surface_options(tile_names[index])) & water_surfaces
    ]
    eligible_land = [index for index in eligible if index not in eligible_water]

    for source_index in np.unique(result[active_mask]):
        source_index = int(source_index)
        source_tile = tile_names[source_index]
        if _tile_allowed_for_composition(
            source_tile,
            allowed_surfaces,
            allowed_biotopes,
        ):
            continue

        source_is_water = bool(
            set(_tile_surface_options(source_tile)) & water_surfaces
        )
        candidates = (
            eligible_water
            if source_is_water and eligible_water
            else eligible_land
            if not source_is_water and eligible_land
            else eligible
        )
        source_color = np.asarray(TILES[source_tile], dtype=np.float32)
        replacement = min(
            candidates,
            key=lambda candidate: float(
                np.sum(
                    (
                        np.asarray(
                            TILES[tile_names[candidate]],
                            dtype=np.float32,
                        )
                        - source_color
                    )
                    ** 2
                )
            ),
        )
        replace_mask = active_mask & (result == source_index)
        result[replace_mask] = replacement
    return result


def _tile_allowed_for_composition(
    tile: str,
    allowed_surfaces: set[str],
    allowed_biotopes: set[str],
) -> bool:
    if not set(_tile_surface_options(tile)) & allowed_surfaces:
        return False
    biotope = _tile_biotope(tile)
    return biotope is None or biotope in allowed_biotopes


def _tile_surface_options(tile: str) -> Tuple[str, ...]:
    base = tile.split(":", 1)[0]
    if base == "deep_ocean":
        return ("deep_ocean",)
    if base == "close_ocean":
        return ("shelf",)
    if base == "shallow_waters":
        return ("shallow_water", "river_lake")
    if base == "sand":
        return ("sand",)
    if base == "soil_low":
        return ("plain", "lowland", "depression")
    if base == "soil_high":
        return ("upland",)
    if base == "hills":
        return ("hills",)
    if base == "mountains":
        return ("rocks", "summit")
    return ()


def _tile_biotope(tile: str) -> Optional[str]:
    if ":" not in tile:
        return None
    suffix = tile.split(":", 1)[1]
    if suffix.endswith("_low"):
        suffix = suffix[:-4]
    elif suffix.endswith("_high"):
        suffix = suffix[:-5]
    return "wasteland" if suffix == "waste" else suffix


def fit_size(shape: Tuple[int, int], max_dimension: int) -> Tuple[int, int]:
    height, width = shape
    scale = min(1.0, max_dimension / max(height, width))
    return max(1, round(width * scale)), max(1, round(height * scale))


def resize_mask(
    mask: np.ndarray,
    size: Tuple[int, int],
    threshold: float,
) -> np.ndarray:
    image = Img.fromarray(mask.astype(np.uint8) * 255)
    resized = image.resize(size, resample=Img.Resampling.BOX)
    return np.asarray(resized, dtype=np.uint8) >= round(255 * threshold)


def binary_dilate(mask: np.ndarray, iterations: int) -> np.ndarray:
    result = mask.copy()
    for _ in range(iterations):
        result |= neighbor_count(result) > 0
    return result


def binary_erode(
    mask: np.ndarray,
    iterations: int,
    border_value: bool = False,
) -> np.ndarray:
    result = mask.copy()
    for _ in range(iterations):
        result &= neighbor_count(result, border_value=border_value) == 8
    return result


def binary_close(mask: np.ndarray, iterations: int) -> np.ndarray:
    return binary_erode(binary_dilate(mask, iterations), iterations)


def flood_connected(mask: np.ndarray, seeds: np.ndarray) -> np.ndarray:
    reached = seeds & mask
    queue = deque(zip(*np.where(reached)))
    height, width = mask.shape

    while queue:
        row, col = queue.popleft()
        for next_row, next_col in (
            (row - 1, col),
            (row + 1, col),
            (row, col - 1),
            (row, col + 1),
        ):
            if (
                0 <= next_row < height
                and 0 <= next_col < width
                and mask[next_row, next_col]
                and not reached[next_row, next_col]
            ):
                reached[next_row, next_col] = True
                queue.append((next_row, next_col))

    return reached


def fill_small_land_regions(water_mask: np.ndarray, min_area: int) -> np.ndarray:
    """Turn tiny 8-connected land components back into water."""
    land_mask = ~water_mask
    parents = []
    areas = []
    runs = []

    def find(label: int) -> int:
        root = label
        while parents[root] != root:
            root = parents[root]
        while parents[label] != label:
            next_label = parents[label]
            parents[label] = root
            label = next_label
        return root

    def union(first: int, second: int) -> None:
        first_root = find(first)
        second_root = find(second)
        if first_root == second_root:
            return
        if areas[first_root] < areas[second_root]:
            first_root, second_root = second_root, first_root
        parents[second_root] = first_root
        areas[first_root] += areas[second_root]

    previous_runs = []
    for row_index, row in enumerate(land_mask):
        padded = np.pad(row.astype(np.int8), (1, 1), mode="constant")
        changes = np.diff(padded)
        starts = np.flatnonzero(changes == 1)
        ends = np.flatnonzero(changes == -1)
        current_runs = []
        previous_index = 0

        for start_value, end_value in zip(starts, ends):
            start = int(start_value)
            end = int(end_value)
            label = len(parents)
            parents.append(label)
            areas.append(end - start)

            while (
                previous_index < len(previous_runs)
                and previous_runs[previous_index][2] < start
            ):
                previous_index += 1

            overlap_index = previous_index
            while (
                overlap_index < len(previous_runs)
                and previous_runs[overlap_index][1] <= end
            ):
                union(label, previous_runs[overlap_index][0])
                overlap_index += 1

            run = (label, start, end)
            current_runs.append(run)
            runs.append((row_index, start, end, label))

        previous_runs = current_runs

    result = water_mask.copy()
    for row, start, end, label in runs:
        if areas[find(label)] < min_area:
            result[row, start:end] = True
    return result


def neighbor_count(mask: np.ndarray, border_value: bool = False) -> np.ndarray:
    padded = np.pad(
        mask.astype(np.uint8),
        1,
        mode="constant",
        constant_values=int(border_value),
    )
    return (
        padded[:-2, :-2]
        + padded[:-2, 1:-1]
        + padded[:-2, 2:]
        + padded[1:-1, :-2]
        + padded[1:-1, 2:]
        + padded[2:, :-2]
        + padded[2:, 1:-1]
        + padded[2:, 2:]
    )


def tile_index(tile_names: Sequence[str], *preferred: str) -> int:
    lookup: Dict[str, int] = {tile: index for index, tile in enumerate(tile_names)}
    for tile in preferred:
        if tile in lookup:
            return lookup[tile]
    return 0


def living_soil_index(
    tile_names: Sequence[str],
    level: str,
    preferred_biotope: str = "grass",
) -> int:
    if level not in {"low", "high"}:
        raise ValueError("living soil level must be low or high")

    lookup: Dict[str, int] = {tile: index for index, tile in enumerate(tile_names)}
    biotopes = (preferred_biotope,) + tuple(
        biome
        for biome in LIVING_SOIL_BIOMES
        if biome != preferred_biotope
    )
    for biome in biotopes:
        candidate = f"soil_{level}:{biome}_{level}"
        if candidate in lookup:
            return lookup[candidate]

    other_level = "high" if level == "low" else "low"
    for biome in biotopes:
        candidate = f"soil_{other_level}:{biome}_{other_level}"
        if candidate in lookup:
            return lookup[candidate]

    raise ValueError(
        "adaptive terrain conversion requires at least one living soil "
        "tile with a biome suffix"
    )


def replace_bare_soil(
    indices: np.ndarray,
    tile_names: Sequence[str],
) -> np.ndarray:
    lookup = {tile: index for index, tile in enumerate(tile_names)}
    result = indices
    for bare_tile, level in (("soil_low", "low"), ("soil_high", "high")):
        bare_index = lookup.get(bare_tile)
        if bare_index is None or not np.any(result == bare_index):
            continue
        if result is indices:
            result = indices.copy()
        result[result == bare_index] = living_soil_index(tile_names, level)
    return result


def make_index_image(indices: np.ndarray, tile_names: Sequence[str]) -> Image:
    image = Img.fromarray(indices, mode="P")
    palette = []
    for tile in tile_names:
        palette.extend(TILES[tile])
    palette.extend([0, 0, 0] * (256 - len(tile_names)))
    image.putpalette(palette[: 256 * 3])
    return image
