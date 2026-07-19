from __future__ import annotations

import json
import math
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Callable, Dict, List, Mapping, Optional, Sequence, Tuple

from PIL import Image


GEOREFERENCE_FILE_NAME = "terrainlab-georeference.json"
GEOREFERENCE_FORMAT = "terrainlab-raster-georeference"
GEOREFERENCE_SCHEMA_VERSION = "1.0.0"
WORLDBOX_METRES_PER_CELL = 1000.0

GeoTransform = Tuple[float, float, float, float, float, float]


@dataclass(frozen=True)
class Wgs84ControlPoint:
    local_x: float
    local_y: float
    source_x: float
    source_y: float
    longitude: float
    latitude: float

    def to_json_dict(self) -> Dict[str, float]:
        return {
            "local_x": self.local_x,
            "local_y": self.local_y,
            "source_x": self.source_x,
            "source_y": self.source_y,
            "longitude": self.longitude,
            "latitude": self.latitude,
        }


@dataclass(frozen=True)
class RasterGeoreference:
    source_file_name: str
    source_width: int
    source_height: int
    raster_width: int
    raster_height: int
    source_raster_to_crs: GeoTransform
    raster_to_crs: GeoTransform
    crs_wkt: Optional[str] = None
    crs_projjson: Optional[str] = None
    epsg: Optional[int] = None
    crs_kind: str = "unknown"
    pixel_interpretation: str = "area"
    vertical_epsg: Optional[int] = None
    vertical_crs_name: Optional[str] = None
    geo_key_directory: Tuple[int, ...] = ()
    geo_double_params: Tuple[float, ...] = ()
    geo_ascii_params: str = ""
    wgs84_wkt: Optional[str] = None
    wgs84_projjson: Optional[str] = None
    wgs84_control_points: Tuple[Wgs84ControlPoint, ...] = ()

    def validate(
        self,
        expected_size: Optional[Tuple[int, int]] = None,
    ) -> None:
        if self.source_width <= 0 or self.source_height <= 0:
            raise ValueError("source georeference dimensions must be positive")
        if self.raster_width <= 0 or self.raster_height <= 0:
            raise ValueError("output georeference dimensions must be positive")
        if expected_size is not None and (
            self.raster_width,
            self.raster_height,
        ) != expected_size:
            raise ValueError(
                "georeference dimensions do not match the output raster"
            )
        _validate_transform(self.source_raster_to_crs)
        _validate_transform(self.raster_to_crs)
        if self.pixel_interpretation not in {"area", "point"}:
            raise ValueError(
                "pixel interpretation must be either area or point"
            )
        for code in (self.epsg, self.vertical_epsg):
            if code is not None and not 1 <= int(code) <= 999999:
                raise ValueError("EPSG code is outside the supported range")
        if len(self.geo_key_directory) > 4096:
            raise ValueError("GeoKey directory exceeds the safe limit")
        if any(
            int(value) < 0 or int(value) > 65535
            for value in self.geo_key_directory
        ):
            raise ValueError("GeoKey directory contains a non-UInt16 value")
        if len(self.geo_double_params) > 4096 or not all(
            math.isfinite(float(value)) for value in self.geo_double_params
        ):
            raise ValueError("GeoDoubleParams are invalid")
        if len(self.geo_ascii_params) > 65535:
            raise ValueError("GeoAsciiParams exceed the safe limit")
        if len(self.wgs84_control_points) > 256:
            raise ValueError("WGS84 control grid exceeds the safe limit")
        for point in self.wgs84_control_points:
            if not all(
                math.isfinite(value)
                for value in (
                    point.local_x,
                    point.local_y,
                    point.source_x,
                    point.source_y,
                    point.longitude,
                    point.latitude,
                )
            ):
                raise ValueError("WGS84 control grid contains non-finite data")
            if (
                not 0.0 <= point.local_x <= self.raster_width
                or not 0.0 <= point.local_y <= self.raster_height
                or not -180.000001 <= point.longitude <= 180.000001
                or not -90.000001 <= point.latitude <= 90.000001
            ):
                raise ValueError("WGS84 control point is outside its domain")

    @property
    def worldbox_cell_to_crs(self) -> GeoTransform:
        return raster_to_worldbox_transform(
            self.raster_to_crs,
            self.raster_height,
            1.0,
        )

    @property
    def worldbox_metre_to_crs(self) -> GeoTransform:
        return raster_to_worldbox_transform(
            self.raster_to_crs,
            self.raster_height,
            WORLDBOX_METRES_PER_CELL,
        )

    @property
    def crs_to_worldbox_cell(self) -> GeoTransform:
        return invert_transform(self.worldbox_cell_to_crs)

    @property
    def crs_to_worldbox_metre(self) -> GeoTransform:
        return invert_transform(self.worldbox_metre_to_crs)

    def resampled(self, width: int, height: int) -> "RasterGeoreference":
        self.validate()
        if width <= 0 or height <= 0:
            raise ValueError("georeferenced raster dimensions must be positive")
        x_scale = self.raster_width / float(width)
        y_scale = self.raster_height / float(height)
        source = self.raster_to_crs
        output_transform = (
            source[0],
            source[1] * x_scale,
            source[2] * y_scale,
            source[3],
            source[4] * x_scale,
            source[5] * y_scale,
        )
        updated = replace(
            self,
            raster_width=width,
            raster_height=height,
            raster_to_crs=output_transform,
            wgs84_control_points=(),
        )
        return replace(
            updated,
            wgs84_control_points=build_wgs84_control_points(updated),
        )

    def cropped(
        self,
        left: int,
        top: int,
        right: int,
        bottom: int,
    ) -> "RasterGeoreference":
        """Return the georeference of a pixel-aligned raster crop."""
        self.validate()
        if (
            left < 0
            or top < 0
            or right > self.raster_width
            or bottom > self.raster_height
            or right <= left
            or bottom <= top
        ):
            raise ValueError("georeference crop is outside the raster")
        transform = self.raster_to_crs
        cropped_transform = (
            transform[0] + transform[1] * left + transform[2] * top,
            transform[1],
            transform[2],
            transform[3] + transform[4] * left + transform[5] * top,
            transform[4],
            transform[5],
        )
        updated = replace(
            self,
            raster_width=right - left,
            raster_height=bottom - top,
            raster_to_crs=cropped_transform,
            wgs84_control_points=(),
        )
        return replace(
            updated,
            wgs84_control_points=build_wgs84_control_points(updated),
        )

    def to_json_dict(self) -> Dict[str, Any]:
        self.validate()
        return {
            "format": GEOREFERENCE_FORMAT,
            "schema_version": GEOREFERENCE_SCHEMA_VERSION,
            "source_file_name": self.source_file_name,
            "source_width": self.source_width,
            "source_height": self.source_height,
            "raster_width": self.raster_width,
            "raster_height": self.raster_height,
            "source_raster_to_crs": list(self.source_raster_to_crs),
            "raster_to_crs": list(self.raster_to_crs),
            "worldbox_cell_to_crs": list(self.worldbox_cell_to_crs),
            "worldbox_metre_to_crs": list(self.worldbox_metre_to_crs),
            "crs_to_worldbox_cell": list(self.crs_to_worldbox_cell),
            "crs_to_worldbox_metre": list(self.crs_to_worldbox_metre),
            "worldbox_metres_per_cell": WORLDBOX_METRES_PER_CELL,
            "crs_wkt": self.crs_wkt,
            "crs_projjson": self.crs_projjson,
            "epsg": self.epsg,
            "crs_kind": self.crs_kind,
            "pixel_interpretation": self.pixel_interpretation,
            "vertical_epsg": self.vertical_epsg,
            "vertical_crs_name": self.vertical_crs_name,
            "geo_key_directory": list(self.geo_key_directory),
            "geo_double_params": list(self.geo_double_params),
            "geo_ascii_params": self.geo_ascii_params,
            "wgs84_epsg": 4326,
            "wgs84_wkt": self.wgs84_wkt,
            "wgs84_projjson": self.wgs84_projjson,
            "wgs84_control_points": [
                point.to_json_dict() for point in self.wgs84_control_points
            ],
        }


def read_raster_georeference(
    path: Path,
    expected_size: Optional[Tuple[int, int]] = None,
) -> Optional[RasterGeoreference]:
    path = Path(path)
    raw = _read_tiff_geotags(path)
    gdal_metadata = _read_with_gdal(path)
    width, height = expected_size or gdal_metadata.get("size") or _image_size(path)

    geo_keys = tuple(int(value) for value in raw.get("geo_key_directory", ()))
    pixel_interpretation = str(
        gdal_metadata.get("pixel_interpretation")
        or _pixel_interpretation(geo_keys)
    ).lower()
    if pixel_interpretation not in {"area", "point"}:
        pixel_interpretation = "area"

    transform = gdal_metadata.get("transform")
    if transform is None:
        transform = _transform_from_geotags(raw, pixel_interpretation)
    if transform is None:
        transform = _transform_from_world_file(path)
    if transform is None:
        return None
    transform = _validate_transform(transform)

    epsg = _optional_int(gdal_metadata.get("epsg"))
    if epsg is None:
        epsg = _horizontal_epsg_from_geokeys(geo_keys)
    vertical_epsg = _vertical_epsg_from_geokeys(geo_keys)
    crs_kind = str(
        gdal_metadata.get("crs_kind")
        or _crs_kind_from_geokeys(geo_keys)
    )
    crs_wkt = _clean_optional_text(gdal_metadata.get("crs_wkt"))
    if crs_wkt is None:
        crs_wkt = _read_prj(path)
    crs_projjson = _clean_optional_text(gdal_metadata.get("crs_projjson"))

    if not geo_keys and epsg is not None:
        geo_keys = build_geo_key_directory(
            epsg,
            crs_kind,
            pixel_interpretation,
            vertical_epsg,
        )

    georeference = RasterGeoreference(
        source_file_name=path.name,
        source_width=width,
        source_height=height,
        raster_width=width,
        raster_height=height,
        source_raster_to_crs=transform,
        raster_to_crs=transform,
        crs_wkt=crs_wkt,
        crs_projjson=crs_projjson,
        epsg=epsg,
        crs_kind=crs_kind,
        pixel_interpretation=pixel_interpretation,
        vertical_epsg=vertical_epsg,
        vertical_crs_name=_clean_optional_text(
            gdal_metadata.get("vertical_crs_name")
        ),
        geo_key_directory=geo_keys,
        geo_double_params=tuple(
            float(value) for value in raw.get("geo_double_params", ())
        ),
        geo_ascii_params=str(raw.get("geo_ascii_params") or ""),
        wgs84_wkt=_clean_optional_text(gdal_metadata.get("wgs84_wkt")),
        wgs84_projjson=_clean_optional_text(
            gdal_metadata.get("wgs84_projjson")
        ),
    )
    return replace(
        georeference,
        wgs84_control_points=build_wgs84_control_points(georeference),
    )


def write_map_georeference(directory: Path, georeference: RasterGeoreference) -> Path:
    georeference.validate()
    path = Path(directory) / GEOREFERENCE_FILE_NAME
    path.write_text(
        json.dumps(
            georeference.to_json_dict(),
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    return path


def write_raster_georeference_sidecars(
    raster_path: Path,
    georeference: RasterGeoreference,
) -> None:
    georeference.validate()
    raster_path = Path(raster_path)
    transform = georeference.raster_to_crs
    world_file = (
        transform[1],
        transform[4],
        transform[2],
        transform[5],
        transform[0] + 0.5 * transform[1] + 0.5 * transform[2],
        transform[3] + 0.5 * transform[4] + 0.5 * transform[5],
    )
    raster_path.with_suffix(".tfw").write_text(
        "\n".join(_format_number(value) for value in world_file) + "\n",
        encoding="utf-8",
    )
    if georeference.crs_wkt:
        raster_path.with_suffix(".prj").write_text(
            georeference.crs_wkt.rstrip() + "\n",
            encoding="utf-8",
        )
    raster_path.with_name(
        raster_path.stem + ".terrainlab-georef.json"
    ).write_text(
        json.dumps(
            georeference.to_json_dict(),
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


def build_geo_key_directory(
    epsg: int,
    crs_kind: str,
    pixel_interpretation: str,
    vertical_epsg: Optional[int] = None,
) -> Tuple[int, ...]:
    raster_type = 2 if pixel_interpretation == "point" else 1
    projected = crs_kind in {"projected", "compound_projected"}
    keys = [
        (1024, 0, 1, 1 if projected else 2),
        (1025, 0, 1, raster_type),
        (
            3072 if projected else 2048,
            0,
            1,
            int(epsg) if int(epsg) <= 65535 else 32767,
        ),
    ]
    if vertical_epsg is not None and int(vertical_epsg) <= 65535:
        keys.append((4096, 0, 1, int(vertical_epsg)))
    keys.sort(key=lambda item: item[0])
    flattened: List[int] = [1, 1, 0, len(keys)]
    for key in keys:
        flattened.extend(key)
    return tuple(flattened)


def raster_to_worldbox_transform(
    transform: Sequence[float],
    raster_height: int,
    local_units_per_cell: float,
) -> GeoTransform:
    if local_units_per_cell <= 0:
        raise ValueError("local units per WorldBox cell must be positive")
    gt = _validate_transform(transform)
    return (
        gt[0] + gt[2] * raster_height,
        gt[1] / local_units_per_cell,
        -gt[2] / local_units_per_cell,
        gt[3] + gt[5] * raster_height,
        gt[4] / local_units_per_cell,
        -gt[5] / local_units_per_cell,
    )


def apply_transform(transform: Sequence[float], x: float, y: float) -> Tuple[float, float]:
    gt = _validate_transform(transform)
    return (
        gt[0] + x * gt[1] + y * gt[2],
        gt[3] + x * gt[4] + y * gt[5],
    )


def invert_transform(transform: Sequence[float]) -> GeoTransform:
    gt = _validate_transform(transform)
    determinant = gt[1] * gt[5] - gt[2] * gt[4]
    return (
        (-gt[5] * gt[0] + gt[2] * gt[3]) / determinant,
        gt[5] / determinant,
        -gt[2] / determinant,
        (gt[4] * gt[0] - gt[1] * gt[3]) / determinant,
        -gt[4] / determinant,
        gt[1] / determinant,
    )


def build_wgs84_control_points(
    georeference: RasterGeoreference,
) -> Tuple[Wgs84ControlPoint, ...]:
    converter = _wgs84_converter(georeference)
    if converter is None:
        return ()
    local_transform = georeference.worldbox_cell_to_crs
    points: List[Wgs84ControlPoint] = []
    for y_fraction in (0.0, 0.25, 0.5, 0.75, 1.0):
        local_y = georeference.raster_height * y_fraction
        for x_fraction in (0.0, 0.25, 0.5, 0.75, 1.0):
            local_x = georeference.raster_width * x_fraction
            source_x, source_y = apply_transform(
                local_transform,
                local_x,
                local_y,
            )
            try:
                longitude, latitude = converter(source_x, source_y)
            except (ValueError, RuntimeError, OverflowError):
                continue
            if not all(
                math.isfinite(value)
                for value in (source_x, source_y, longitude, latitude)
            ):
                continue
            points.append(
                Wgs84ControlPoint(
                    local_x=local_x,
                    local_y=local_y,
                    source_x=source_x,
                    source_y=source_y,
                    longitude=longitude,
                    latitude=latitude,
                )
            )
    return tuple(points)


def _read_with_gdal(path: Path) -> Dict[str, Any]:
    bindings = _load_osgeo()
    if bindings is None:
        return {}
    gdal, osr = bindings
    try:
        dataset = gdal.OpenEx(
            str(path),
            gdal.OF_RASTER | gdal.OF_READONLY,
        )
        if dataset is None:
            return {}
        try:
            result: Dict[str, Any] = {
                "size": (dataset.RasterXSize, dataset.RasterYSize),
            }
            transform = dataset.GetGeoTransform(can_return_null=True)
            if transform is None and dataset.GetGCPCount() >= 3:
                transform = gdal.GCPsToGeoTransform(dataset.GetGCPs())
            if transform is not None:
                result["transform"] = tuple(float(value) for value in transform)
            area_or_point = dataset.GetMetadataItem("AREA_OR_POINT")
            if area_or_point:
                result["pixel_interpretation"] = area_or_point.lower()

            spatial_ref = dataset.GetSpatialRef()
            if spatial_ref is None and dataset.GetGCPCount() > 0:
                spatial_ref = dataset.GetGCPSpatialRef()
            if spatial_ref is not None:
                spatial_ref = spatial_ref.Clone()
                result.update(_describe_spatial_reference(spatial_ref, osr))
            return result
        finally:
            dataset = None
    except Exception:
        return {}


def _describe_spatial_reference(spatial_ref: Any, osr: Any) -> Dict[str, Any]:
    result: Dict[str, Any] = {}
    try:
        spatial_ref.AutoIdentifyEPSG()
    except Exception:
        pass
    result["crs_wkt"] = spatial_ref.ExportToWkt(["FORMAT=WKT2_2019"])
    try:
        result["crs_projjson"] = spatial_ref.ExportToPROJJSON()
    except Exception:
        pass
    if spatial_ref.IsCompound():
        horizontal = spatial_ref.Clone()
        horizontal.StripVertical()
        kind = "compound_projected" if horizontal.IsProjected() else "compound_geographic"
    else:
        horizontal = spatial_ref
        kind = (
            "projected"
            if horizontal.IsProjected()
            else "geographic"
            if horizontal.IsGeographic()
            else "engineering"
            if horizontal.IsLocal()
            else "unknown"
        )
    result["crs_kind"] = kind
    result["epsg"] = _authority_code(horizontal)
    result["vertical_crs_name"] = (
        spatial_ref.GetAttrValue("VERTCRS")
        or spatial_ref.GetAttrValue("VERT_CS")
    )

    wgs84 = osr.SpatialReference()
    wgs84.ImportFromEPSG(4326)
    result["wgs84_wkt"] = wgs84.ExportToWkt(["FORMAT=WKT2_2019"])
    try:
        result["wgs84_projjson"] = wgs84.ExportToPROJJSON()
    except Exception:
        pass
    return result


def _wgs84_converter(
    georeference: RasterGeoreference,
) -> Optional[Callable[[float, float], Tuple[float, float]]]:
    if georeference.epsg == 4326:
        return lambda x, y: (x, y)
    if georeference.epsg == 3857:
        return _web_mercator_to_wgs84

    bindings = _load_osgeo()
    if bindings is None:
        return None
    _gdal, osr = bindings
    try:
        source = osr.SpatialReference()
        if georeference.crs_wkt:
            source.ImportFromWkt(georeference.crs_wkt)
        elif georeference.epsg is not None:
            source.ImportFromEPSG(georeference.epsg)
        else:
            return None
        if source.IsCompound():
            source.StripVertical()
        target = osr.SpatialReference()
        target.ImportFromEPSG(4326)
        source.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        target.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        operation = osr.CreateCoordinateTransformation(source, target)
        if operation is None:
            return None

        def convert(x: float, y: float) -> Tuple[float, float]:
            transformed = operation.TransformPoint(x, y)
            return float(transformed[0]), float(transformed[1])

        return convert
    except Exception:
        return None


def _web_mercator_to_wgs84(x: float, y: float) -> Tuple[float, float]:
    radius = 6378137.0
    longitude = math.degrees(x / radius)
    latitude = math.degrees(2.0 * math.atan(math.exp(y / radius)) - math.pi / 2.0)
    return longitude, latitude


def _load_osgeo() -> Optional[Tuple[Any, Any]]:
    try:
        import osgeo
        from osgeo import gdal, osr

        bundled_proj = Path(osgeo.__file__).resolve().parent / "data" / "proj"
        if (bundled_proj / "proj.db").is_file():
            osr.SetPROJSearchPaths([str(bundled_proj)])
        gdal.UseExceptions()
        osr.UseExceptions()
        return gdal, osr
    except (ImportError, OSError, RuntimeError):
        return None


def _read_tiff_geotags(path: Path) -> Dict[str, Any]:
    if path.suffix.lower() not in {".tif", ".tiff"}:
        return {}
    try:
        with Image.open(path) as image:
            tags = image.tag_v2
            return {
                "model_pixel_scale": _number_tuple(tags.get(33550)),
                "model_tiepoint": _number_tuple(tags.get(33922)),
                "model_transformation": _number_tuple(tags.get(34264)),
                "geo_key_directory": _integer_tuple(tags.get(34735)),
                "geo_double_params": _number_tuple(tags.get(34736)),
                "geo_ascii_params": _ascii_value(tags.get(34737)),
            }
    except (OSError, ValueError, KeyError):
        return {}


def _transform_from_geotags(
    tags: Mapping[str, Any],
    pixel_interpretation: str,
) -> Optional[GeoTransform]:
    matrix = tags.get("model_transformation") or ()
    if len(matrix) >= 16:
        transform = (
            float(matrix[3]),
            float(matrix[0]),
            float(matrix[1]),
            float(matrix[7]),
            float(matrix[4]),
            float(matrix[5]),
        )
        return _pixel_point_to_corner(transform, pixel_interpretation)
    scale = tags.get("model_pixel_scale") or ()
    tiepoint = tags.get("model_tiepoint") or ()
    if len(scale) >= 2 and len(tiepoint) >= 6:
        scale_x = float(scale[0])
        scale_y = float(scale[1])
        raster_x = float(tiepoint[0])
        raster_y = float(tiepoint[1])
        model_x = float(tiepoint[3])
        model_y = float(tiepoint[4])
        transform = (
            model_x - raster_x * scale_x,
            scale_x,
            0.0,
            model_y + raster_y * scale_y,
            0.0,
            -scale_y,
        )
        return _pixel_point_to_corner(transform, pixel_interpretation)
    return None


def _pixel_point_to_corner(
    transform: GeoTransform,
    pixel_interpretation: str,
) -> GeoTransform:
    if pixel_interpretation != "point":
        return transform
    return (
        transform[0] - 0.5 * transform[1] - 0.5 * transform[2],
        transform[1],
        transform[2],
        transform[3] - 0.5 * transform[4] - 0.5 * transform[5],
        transform[4],
        transform[5],
    )


def _transform_from_world_file(path: Path) -> Optional[GeoTransform]:
    candidates = [
        path.with_suffix(".tfw"),
        path.with_suffix(path.suffix + "w"),
    ]
    for candidate in candidates:
        if not candidate.is_file():
            continue
        try:
            values = [
                float(line.strip())
                for line in candidate.read_text(encoding="utf-8-sig").splitlines()
                if line.strip()
            ]
        except (OSError, ValueError):
            continue
        if len(values) != 6:
            continue
        a, d, b, e, center_x, center_y = values
        return (
            center_x - 0.5 * a - 0.5 * b,
            a,
            b,
            center_y - 0.5 * d - 0.5 * e,
            d,
            e,
        )
    return None


def _read_prj(path: Path) -> Optional[str]:
    candidate = path.with_suffix(".prj")
    if not candidate.is_file():
        return None
    try:
        return _clean_optional_text(candidate.read_text(encoding="utf-8-sig"))
    except OSError:
        return None


def _horizontal_epsg_from_geokeys(keys: Sequence[int]) -> Optional[int]:
    values = _geokey_values(keys)
    for key_id in (3072, 2048):
        value = values.get(key_id)
        if value is not None and value not in {0, 32767}:
            return value
    return None


def _vertical_epsg_from_geokeys(keys: Sequence[int]) -> Optional[int]:
    value = _geokey_values(keys).get(4096)
    return value if value not in {None, 0, 32767} else None


def _pixel_interpretation(keys: Sequence[int]) -> str:
    return "point" if _geokey_values(keys).get(1025) == 2 else "area"


def _crs_kind_from_geokeys(keys: Sequence[int]) -> str:
    model_type = _geokey_values(keys).get(1024)
    if model_type == 1:
        return "projected"
    if model_type == 2:
        return "geographic"
    if model_type == 3:
        return "geocentric"
    return "unknown"


def _geokey_values(keys: Sequence[int]) -> Dict[int, int]:
    if len(keys) < 4:
        return {}
    count = int(keys[3])
    if count < 0 or len(keys) < 4 + count * 4:
        return {}
    result: Dict[int, int] = {}
    for index in range(count):
        offset = 4 + index * 4
        key_id, location, value_count, value_offset = (
            int(value) for value in keys[offset : offset + 4]
        )
        if location == 0 and value_count == 1:
            result[key_id] = value_offset
    return result


def _authority_code(spatial_ref: Any) -> Optional[int]:
    for node in (None, "PROJCRS", "PROJCS", "GEOGCRS", "GEOGCS"):
        try:
            value = spatial_ref.GetAuthorityCode(node)
        except Exception:
            value = None
        parsed = _optional_int(value)
        if parsed is not None:
            return parsed
    return None


def _validate_transform(values: Sequence[float]) -> GeoTransform:
    if len(values) != 6:
        raise ValueError("raster affine transform must contain six coefficients")
    transform = tuple(float(value) for value in values)
    if not all(math.isfinite(value) for value in transform):
        raise ValueError("raster affine transform contains a non-finite value")
    determinant = transform[1] * transform[5] - transform[2] * transform[4]
    if abs(determinant) <= 1e-18:
        raise ValueError("raster affine transform is not invertible")
    return transform  # type: ignore[return-value]


def _number_tuple(value: Any) -> Tuple[float, ...]:
    if value is None:
        return ()
    values = value if isinstance(value, (tuple, list)) else (value,)
    try:
        return tuple(float(item) for item in values)
    except (TypeError, ValueError):
        return ()


def _integer_tuple(value: Any) -> Tuple[int, ...]:
    if value is None:
        return ()
    values = value if isinstance(value, (tuple, list)) else (value,)
    try:
        return tuple(int(item) for item in values)
    except (TypeError, ValueError):
        return ()


def _ascii_value(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("ascii", errors="replace").rstrip("\0")
    return str(value).rstrip("\0")


def _image_size(path: Path) -> Tuple[int, int]:
    with Image.open(path) as image:
        return image.size


def _clean_optional_text(value: Any) -> Optional[str]:
    if value is None:
        return None
    text = str(value).strip()
    return text or None


def _optional_int(value: Any) -> Optional[int]:
    if value is None or isinstance(value, bool):
        return None
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return None
    return parsed if 1 <= parsed <= 999999 else None


def _format_number(value: float) -> str:
    return format(float(value), ".15g")
