import json
import unittest
import zlib
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

from imagetomap import convert, validate_map_size
from imagetomap.consts import (
    ELEVATION_MAXIMUM,
    ELEVATION_MINIMUM,
    ELEVATION_NODATA,
    SAFE_TILES_TUPLE,
    UNPLAYABLE_TILES,
)
from imagetomap.terrain import fill_small_land_regions
from imagetomap.utils import json_loads


WATER_TILES = {"deep_ocean", "close_ocean", "shallow_waters"}
ROOT = Path(__file__).resolve().parents[1]

PARAMETER_TOOLTIP_KEYS = (
    "terrain_lab_elevation_value",
    "terrain_lab_elevation_step",
    "terrain_lab_brush_radius",
    "terrain_lab_hydrology_threshold",
    "terrain_lab_erosion_iterations",
    "terrain_lab_erosion_flow_strength",
    "terrain_lab_erosion_thermal_strength",
    "terrain_lab_erosion_talus",
    "terrain_lab_water_maximum_flood_percent",
    "terrain_lab_water_initial_source_volume",
    "terrain_lab_water_geyser_pulse_volume",
    "terrain_lab_water_cells_per_tick",
    "terrain_lab_water_evaporation_per_climate_step",
)


class TerrainConversionTests(unittest.TestCase):
    def test_mod_parameter_tooltips_are_complete_and_localized(self) -> None:
        locale_directory = ROOT / "worldbox_mod" / "TerrainLab" / "Locales"
        locales = {
            name: json.loads((locale_directory / name).read_text(encoding="utf-8"))
            for name in ("en.json", "ru.json")
        }

        self.assertEqual(set(locales["en.json"]), set(locales["ru.json"]))
        for locale in locales.values():
            for key in PARAMETER_TOOLTIP_KEYS:
                self.assertTrue(locale[key])
                self.assertTrue(locale[f"{key}_description"])

    def test_elevation_nodata_is_reserved(self) -> None:
        self.assertEqual(ELEVATION_NODATA, 9999)
        self.assertEqual((ELEVATION_MINIMUM, ELEVATION_MAXIMUM), (-20000, 9000))

    def test_map_budget_allows_extreme_aspect_ratios(self) -> None:
        validate_map_size(40, 10)
        validate_map_size(23, 20)

    def test_map_budget_rejects_excess_total_cells(self) -> None:
        with self.assertRaisesRegex(ValueError, "TerrainLab limit"):
            validate_map_size(22, 22)

    def test_safe_palette_excludes_gameplay_hazards(self) -> None:
        self.assertFalse(set(SAFE_TILES_TUPLE) & set(UNPLAYABLE_TILES))

    def test_uniform_image_converts(self) -> None:
        source = Image.new("RGB", (128, 128), (100, 120, 140))
        converted = convert(source, width=2, height=2)

        self.assertEqual((converted.width, converted.height), (2, 2))
        self.assertEqual(converted.preview.size, (128, 128))

    def test_small_land_regions_are_removed(self) -> None:
        water = np.ones((12, 12), dtype=bool)
        water[2, 2:4] = False
        water[6:10, 6:10] = False

        cleaned = fill_small_land_regions(water, min_area=8)

        self.assertTrue(cleaned[2, 2])
        self.assertFalse(cleaned[7, 7])

    def test_muted_boundary_water_is_separate_from_land(self) -> None:
        source = Image.new("RGB", (256, 256), (0, 0, 0))
        draw = ImageDraw.Draw(source)
        draw.ellipse((16, 8, 240, 248), fill=(91, 96, 87))
        draw.rectangle((78, 64, 184, 198), fill=(166, 119, 71))

        converted = convert(source, width=4, height=4)
        pixels = converted.preview.load()
        palette = SAFE_TILES_TUPLE

        self.assertIn(palette[pixels[42, 128]], WATER_TILES)
        self.assertNotIn(palette[pixels[128, 128]], WATER_TILES)

    def test_map_payload_has_expected_size_and_safe_tiles(self) -> None:
        source = Image.new("RGB", (64, 64), (120, 160, 90))
        converted = convert(source, width=2, height=3, name="Terrain test")
        payload = json_loads(zlib.decompress(converted.data))

        self.assertEqual((payload["width"], payload["height"]), (2, 3))
        self.assertEqual(payload["mapStats"]["name"], "Terrain test")
        self.assertFalse(set(payload["tileMap"]) & set(UNPLAYABLE_TILES))
        self.assertEqual(
            sum(sum(row) for row in payload["tileAmounts"]),
            2 * 3 * 64 * 64,
        )


if __name__ == "__main__":
    unittest.main()
