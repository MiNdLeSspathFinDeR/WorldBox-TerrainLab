from dataclasses import dataclass, replace
import json
import math
from pathlib import Path
from typing import Any, Dict, Mapping, Optional, Sequence, Tuple

import numpy as np
from PIL import Image as Img
from PIL import ImageDraw

from .calibration import BIOTOPE_IDS, SURFACE_IDS
from .consts import CHUNK_SIZE, MAX_MAP_CELLS
from .terrain import TerrainClusteringSettings


CLUSTERING_PROFILE_SUFFIX = ".terrainlab-clustering.json"
GENERATED_CLUSTERING_PROFILE_FILE_NAME = "terrainlab-clustering.json"
MAXIMUM_CLUSTERING_PROFILE_BYTES = 1024 * 1024
MAXIMUM_BOUNDARY_VERTICES = 256
CLUSTERING_PROFILE_SCHEMA_VERSION = 4
LEGACY_CLUSTERING_ALGORITHM = "adaptive_v1"
SEMANTIC_CLUSTERING_ALGORITHM = "semantic_v2"
SUPPORTED_CLUSTERING_ALGORITHMS = {
    LEGACY_CLUSTERING_ALGORITHM: 1,
    SEMANTIC_CLUSTERING_ALGORITHM: 2,
}
DEFAULT_ANALYSIS_MAX_DIMENSION = 2048
MAXIMUM_ANALYSIS_DIMENSION = 4096
DEFAULT_CLUSTERING_BIOTOPES = tuple(
    biotope for biotope in BIOTOPE_IDS if biotope not in {"auto", "none"}
)


@dataclass(frozen=True)
class ClusteringProfile:
    source_file_name: str
    source_width: int
    source_height: int
    algorithm_id: str = SEMANTIC_CLUSTERING_ALGORITHM
    algorithm_version: int = 2
    analysis_max_dimension: int = DEFAULT_ANALYSIS_MAX_DIMENSION
    long_side_blocks: int = 20
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
    map_boundary: Tuple[Tuple[int, int], ...] = ()
    allowed_surfaces: Tuple[str, ...] = SURFACE_IDS
    allowed_biotopes: Tuple[str, ...] = DEFAULT_CLUSTERING_BIOTOPES

    def to_json_dict(self) -> Dict[str, Any]:
        self.validate_algorithm()
        payload: Dict[str, Any] = {
            "schema_version": CLUSTERING_PROFILE_SCHEMA_VERSION,
            "source": {
                "file_name": self.source_file_name,
                "width": self.source_width,
                "height": self.source_height,
            },
            "algorithm": {
                "id": self.algorithm_id,
                "version": self.algorithm_version,
            },
            "settings": {
                "analysis_max_dimension": self.analysis_max_dimension,
                "long_side_blocks": self.long_side_blocks,
                "clusters": self.clusters,
                "spline_radius": self.spline_radius,
                "smooth_passes": self.smooth_passes,
                "min_land_region": self.min_land_region,
                "water_sensitivity": self.water_sensitivity,
                "color_weight": self.color_weight,
                "luma_weight": self.luma_weight,
                "saturation_weight": self.saturation_weight,
                "texture_weight": self.texture_weight,
                "slope_weight": self.slope_weight,
                "spatial_weight": self.spatial_weight,
                "detail_weight": self.detail_weight,
                "sample_limit": self.sample_limit,
                "kmeans_iterations": self.kmeans_iterations,
                "random_seed": self.random_seed,
            },
            "composition": {
                "allowed_surfaces": list(self.allowed_surfaces),
                "allowed_biotopes": list(self.allowed_biotopes),
            },
        }
        if self.map_boundary:
            payload["map_boundary"] = {
                "vertices": [
                    {"x": vertex[0], "y": vertex[1]}
                    for vertex in self.map_boundary
                ]
            }
        return payload

    def validate_algorithm(self) -> None:
        expected_version = SUPPORTED_CLUSTERING_ALGORITHMS.get(
            self.algorithm_id
        )
        if expected_version is None:
            raise ValueError(
                "algorithm_id must be adaptive_v1 or semantic_v2"
            )
        if self.algorithm_version != expected_version:
            raise ValueError(
                f"unsupported {self.algorithm_id} algorithm version: "
                f"{self.algorithm_version}"
            )
        if (
            self.analysis_max_dimension < 512
            or self.analysis_max_dimension > MAXIMUM_ANALYSIS_DIMENSION
        ):
            raise ValueError(
                "analysis_max_dimension must be between 512 and 4096"
            )

    def to_terrain_settings(self) -> TerrainClusteringSettings:
        self.validate_algorithm()
        return TerrainClusteringSettings(
            clusters=self.clusters,
            spline_radius=self.spline_radius,
            smooth_passes=self.smooth_passes,
            min_land_region=self.min_land_region,
            water_sensitivity=self.water_sensitivity,
            color_weight=self.color_weight,
            luma_weight=self.luma_weight,
            saturation_weight=self.saturation_weight,
            texture_weight=self.texture_weight,
            slope_weight=self.slope_weight,
            spatial_weight=self.spatial_weight,
            detail_weight=self.detail_weight,
            sample_limit=self.sample_limit,
            kmeans_iterations=self.kmeans_iterations,
            random_seed=self.random_seed,
            allowed_surfaces=self.allowed_surfaces,
            allowed_biotopes=self.allowed_biotopes,
        )


def clustering_processing_extent(
    profile: ClusteringProfile,
) -> Tuple[int, int, int, int]:
    """Return the published clustering boundary as a PIL crop box."""
    if not profile.map_boundary:
        return (0, 0, profile.source_width, profile.source_height)
    x_values = [vertex[0] for vertex in profile.map_boundary]
    y_values = [vertex[1] for vertex in profile.map_boundary]
    left = max(0, min(x_values))
    top = max(0, min(y_values))
    right = min(profile.source_width, max(x_values) + 1)
    bottom = min(profile.source_height, max(y_values) + 1)
    if right <= left or bottom <= top:
        raise ValueError("clustering map boundary has an empty extent")
    return (left, top, right, bottom)


def crop_clustering_profile(
    profile: ClusteringProfile,
    extent: Tuple[int, int, int, int],
) -> ClusteringProfile:
    """Shift a clustering profile into its cropped processing extent."""
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
        raise ValueError("clustering crop is outside the source image")
    return replace(
        profile,
        source_width=width,
        source_height=height,
        map_boundary=tuple(
            (vertex[0] - left, vertex[1] - top)
            for vertex in profile.map_boundary
        ),
    )


def clustering_profile_path(image_path: Path) -> Path:
    return image_path.with_name(image_path.name + CLUSTERING_PROFILE_SUFFIX)


def load_clustering_profile(
    path: Path,
    source_size: Optional[Tuple[int, int]] = None,
) -> ClusteringProfile:
    path = Path(path)
    if not path.is_file():
        raise FileNotFoundError(f"clustering profile was not found: {path}")
    if path.stat().st_size > MAXIMUM_CLUSTERING_PROFILE_BYTES:
        raise ValueError("clustering profile exceeds 1 MiB")

    payload = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(payload, dict) or payload.get("schema_version") not in {
        1,
        2,
        3,
        CLUSTERING_PROFILE_SCHEMA_VERSION,
    }:
        raise ValueError(
            "clustering profile schema_version must be 1, 2, 3, or 4"
        )
    schema_version = int(payload["schema_version"])

    source = _mapping(payload.get("source"), "source")
    source_file_name = str(source.get("file_name") or "").strip()
    source_width = _integer(source.get("width"), "source.width", 1, 1_000_000)
    source_height = _integer(
        source.get("height"),
        "source.height",
        1,
        1_000_000,
    )
    if source_size is not None and (
        source_width != source_size[0] or source_height != source_size[1]
    ):
        raise ValueError(
            "clustering profile source dimensions do not match the image"
        )

    settings = _mapping(payload.get("settings", {}), "settings")
    algorithm_id, algorithm_version = _load_algorithm(
        payload.get("algorithm"),
        schema_version,
    )
    composition_value = payload.get("composition")
    composition = (
        {}
        if composition_value is None
        else _mapping(composition_value, "composition")
    )
    allowed_surfaces = _identifier_list(
        composition.get("allowed_surfaces", SURFACE_IDS),
        "composition.allowed_surfaces",
        SURFACE_IDS,
    )
    allowed_biotopes = _identifier_list(
        composition.get(
            "allowed_biotopes",
            DEFAULT_CLUSTERING_BIOTOPES,
        ),
        "composition.allowed_biotopes",
        DEFAULT_CLUSTERING_BIOTOPES,
    )
    boundary = _load_boundary(
        payload.get("map_boundary"),
        source_width,
        source_height,
    )
    profile = ClusteringProfile(
        source_file_name=source_file_name,
        source_width=source_width,
        source_height=source_height,
        algorithm_id=algorithm_id,
        algorithm_version=algorithm_version,
        analysis_max_dimension=_integer(
            settings.get(
                "analysis_max_dimension",
                DEFAULT_ANALYSIS_MAX_DIMENSION,
            ),
            "analysis_max_dimension",
            512,
            MAXIMUM_ANALYSIS_DIMENSION,
        ),
        long_side_blocks=_integer(
            settings.get("long_side_blocks", 20),
            "long_side_blocks",
            1,
            MAX_MAP_CELLS // (CHUNK_SIZE * CHUNK_SIZE),
        ),
        clusters=_integer(settings.get("clusters", 14), "clusters", 4, 64),
        spline_radius=_integer(
            settings.get("spline_radius", 0),
            "spline_radius",
            0,
            12,
        ),
        smooth_passes=_integer(
            settings.get("smooth_passes", 1),
            "smooth_passes",
            0,
            8,
        ),
        min_land_region=_integer(
            settings.get("min_land_region", 32),
            "min_land_region",
            0,
            4096,
        ),
        water_sensitivity=_number(
            settings.get("water_sensitivity", 1.0),
            "water_sensitivity",
            0.5,
            2.0,
        ),
        color_weight=_number(
            settings.get("color_weight", 1.0),
            "color_weight",
            0.0,
            3.0,
        ),
        luma_weight=_number(
            settings.get("luma_weight", 1.0),
            "luma_weight",
            0.0,
            3.0,
        ),
        saturation_weight=_number(
            settings.get("saturation_weight", 1.0),
            "saturation_weight",
            0.0,
            3.0,
        ),
        texture_weight=_number(
            settings.get("texture_weight", 0.0),
            "texture_weight",
            0.0,
            3.0,
        ),
        slope_weight=_number(
            settings.get("slope_weight", 1.0),
            "slope_weight",
            0.0,
            3.0,
        ),
        spatial_weight=_number(
            settings.get("spatial_weight", 0.0),
            "spatial_weight",
            0.0,
            3.0,
        ),
        detail_weight=_number(
            settings.get("detail_weight", 0.65),
            "detail_weight",
            0.0,
            1.0,
        ),
        sample_limit=_integer(
            settings.get("sample_limit", 60_000),
            "sample_limit",
            1000,
            250_000,
        ),
        kmeans_iterations=_integer(
            settings.get("kmeans_iterations", 18),
            "kmeans_iterations",
            1,
            100,
        ),
        random_seed=_integer(
            settings.get("random_seed", 1729),
            "random_seed",
            0,
            2_147_483_647,
        ),
        map_boundary=boundary,
        allowed_surfaces=allowed_surfaces,
        allowed_biotopes=allowed_biotopes,
    )
    _validate_weights(profile)
    return profile


def _load_algorithm(
    value: Any,
    schema_version: int,
) -> Tuple[str, int]:
    if schema_version < CLUSTERING_PROFILE_SCHEMA_VERSION:
        return (
            LEGACY_CLUSTERING_ALGORITHM,
            SUPPORTED_CLUSTERING_ALGORITHMS[LEGACY_CLUSTERING_ALGORITHM],
        )

    algorithm = _mapping(value, "algorithm")
    identifier = str(algorithm.get("id") or "").strip().lower()
    if identifier not in SUPPORTED_CLUSTERING_ALGORITHMS:
        raise ValueError(
            "algorithm.id must be adaptive_v1 or semantic_v2"
        )
    version = _integer(
        algorithm.get("version"),
        "algorithm.version",
        1,
        2_147_483_647,
    )
    expected = SUPPORTED_CLUSTERING_ALGORITHMS[identifier]
    if version != expected:
        raise ValueError(
            f"unsupported {identifier} algorithm version: {version}"
        )
    return identifier, version


def rasterize_clustering_boundary(
    profile: ClusteringProfile,
    width: int,
    height: int,
) -> Optional[np.ndarray]:
    if not profile.map_boundary:
        return None
    if width <= 0 or height <= 0:
        raise ValueError("clustering boundary target dimensions must be positive")

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
        mask = np.asarray(mask_image, dtype=np.uint8).copy() > 0
        if not np.any(mask):
            raise ValueError("clustering map boundary is empty")
        return mask
    finally:
        mask_image.close()


def _load_boundary(
    value: Any,
    width: int,
    height: int,
) -> Tuple[Tuple[int, int], ...]:
    if value is None:
        return ()
    boundary = _mapping(value, "map_boundary")
    raw_vertices = boundary.get("vertices")
    if not isinstance(raw_vertices, list):
        raise ValueError("map_boundary.vertices must be a JSON array")
    if len(raw_vertices) < 3 or len(raw_vertices) > MAXIMUM_BOUNDARY_VERTICES:
        raise ValueError("clustering boundary needs 3..256 vertices")

    vertices = []
    for index, raw_vertex in enumerate(raw_vertices):
        vertex = _mapping(raw_vertex, f"map_boundary.vertices[{index}]")
        point = (
            _integer(vertex.get("x"), "boundary.x", 0, width - 1),
            _integer(vertex.get("y"), "boundary.y", 0, height - 1),
        )
        if vertices and vertices[-1] == point:
            raise ValueError("clustering boundary has consecutive duplicates")
        vertices.append(point)
    if len(vertices) >= 4 and vertices[0] == vertices[-1]:
        vertices.pop()
    if len(set(vertices)) != len(vertices):
        raise ValueError("clustering boundary contains duplicate vertices")
    if _polygon_self_intersects(vertices):
        raise ValueError("clustering boundary self-intersects")
    return tuple(vertices)


def _validate_weights(profile: ClusteringProfile) -> None:
    if (
        profile.color_weight
        + profile.luma_weight
        + profile.saturation_weight
        + profile.texture_weight
        + profile.slope_weight
        + profile.spatial_weight
        <= 0.0
    ):
        raise ValueError("at least one clustering feature weight must be positive")


def _polygon_self_intersects(
    vertices: Sequence[Tuple[int, int]],
) -> bool:
    count = len(vertices)
    for first in range(count):
        first_start = vertices[first]
        first_end = vertices[(first + 1) % count]
        for second in range(first + 1, count):
            if second == first or second == (first + 1) % count:
                continue
            if first == 0 and second == count - 1:
                continue
            second_start = vertices[second]
            second_end = vertices[(second + 1) % count]
            if _segments_intersect(
                first_start,
                first_end,
                second_start,
                second_end,
            ):
                return True
    return False


def _segments_intersect(
    first_start: Tuple[int, int],
    first_end: Tuple[int, int],
    second_start: Tuple[int, int],
    second_end: Tuple[int, int],
) -> bool:
    first_orientation = _orientation(first_start, first_end, second_start)
    second_orientation = _orientation(first_start, first_end, second_end)
    third_orientation = _orientation(second_start, second_end, first_start)
    fourth_orientation = _orientation(second_start, second_end, first_end)
    if (
        first_orientation == 0
        and _on_segment(first_start, second_start, first_end)
    ):
        return True
    if (
        second_orientation == 0
        and _on_segment(first_start, second_end, first_end)
    ):
        return True
    if (
        third_orientation == 0
        and _on_segment(second_start, first_start, second_end)
    ):
        return True
    if (
        fourth_orientation == 0
        and _on_segment(second_start, first_end, second_end)
    ):
        return True
    return (
        first_orientation != second_orientation
        and third_orientation != fourth_orientation
    )


def _orientation(
    first: Tuple[int, int],
    second: Tuple[int, int],
    third: Tuple[int, int],
) -> int:
    value = (
        (second[1] - first[1]) * (third[0] - second[0])
        - (second[0] - first[0]) * (third[1] - second[1])
    )
    return 0 if value == 0 else 1 if value > 0 else -1


def _on_segment(
    first: Tuple[int, int],
    middle: Tuple[int, int],
    last: Tuple[int, int],
) -> bool:
    return (
        min(first[0], last[0]) <= middle[0] <= max(first[0], last[0])
        and min(first[1], last[1]) <= middle[1] <= max(first[1], last[1])
    )


def _mapping(value: Any, name: str) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise ValueError(f"{name} must be a JSON object")
    return value


def _identifier_list(
    value: Any,
    name: str,
    allowed: Sequence[str],
) -> Tuple[str, ...]:
    if not isinstance(value, (list, tuple)) or not value:
        raise ValueError(f"{name} must be a non-empty JSON array")
    identifiers = tuple(str(item).strip().lower() for item in value)
    if len(set(identifiers)) != len(identifiers):
        raise ValueError(f"{name} contains duplicate values")
    unknown = tuple(item for item in identifiers if item not in allowed)
    if unknown:
        raise ValueError(
            f"{name} contains unsupported values: {', '.join(unknown)}"
        )
    return identifiers


def _integer(value: Any, name: str, minimum: int, maximum: int) -> int:
    if isinstance(value, bool):
        raise ValueError(f"{name} must be an integer")
    try:
        number = int(value)
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{name} must be an integer") from exc
    if number < minimum or number > maximum:
        raise ValueError(f"{name} must be between {minimum} and {maximum}")
    return number


def _number(value: Any, name: str, minimum: float, maximum: float) -> float:
    if isinstance(value, bool):
        raise ValueError(f"{name} must be numeric")
    try:
        number = float(value)
    except (TypeError, ValueError) as exc:
        raise ValueError(f"{name} must be numeric") from exc
    if not math.isfinite(number) or number < minimum or number > maximum:
        raise ValueError(f"{name} must be between {minimum} and {maximum}")
    return number
