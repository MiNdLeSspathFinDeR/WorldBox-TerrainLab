import unittest
import zlib

import numpy as np
from PIL import Image, ImageDraw

from imagetomap import convert, validate_map_size
from imagetomap.consts import ELEVATION_NODATA, SAFE_TILES_TUPLE, UNPLAYABLE_TILES
from imagetomap.terrain import fill_small_land_regions
from imagetomap.utils import json_loads


WATER_TILES = {"deep_ocean", "close_ocean", "shallow_waters"}


class TerrainConversionTests(unittest.TestCase):
    def test_elevation_nodata_is_reserved(self) -> None:
        self.assertEqual(ELEVATION_NODATA, 9999)

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
