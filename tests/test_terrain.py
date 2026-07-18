import json
import re
import tempfile
import unittest
import zlib
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

from imagetomap import convert, fit_map_size_to_budget, validate_map_size
from imagetomap.calibration import (
    ClassificationLine,
    ClassificationProfile,
    ClassificationRegion,
    ClassificationSample,
    GENERATED_ELEVATION_FILE_NAME,
    GENERATED_PROFILE_FILE_NAME,
    load_classification_profile,
    rasterize_map_boundary,
)
from imagetomap.clustering import (
    ClusteringProfile,
    load_clustering_profile,
    rasterize_clustering_boundary,
)
from imagetomap.consts import (
    ELEVATION_MAXIMUM,
    ELEVATION_MINIMUM,
    ELEVATION_NODATA,
    SAFE_TILES_TUPLE,
    UNPLAYABLE_TILES,
)
from imagetomap.saves import write_game_save_atomically
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
    "terrain_lab_water_bank_erosion_radius",
    "terrain_lab_water_orphaned_channel_drain",
    "terrain_lab_manual_image_choice",
    "terrain_lab_manual_open_selected",
    "terrain_lab_manual_previous_image",
    "terrain_lab_manual_next_image",
    "terrain_lab_manual_canvas",
    "terrain_lab_manual_surface",
    "terrain_lab_manual_biotope",
    "terrain_lab_manual_elevation",
    "terrain_lab_manual_global_dem",
    "terrain_lab_manual_mode_point",
    "terrain_lab_manual_mode_line",
    "terrain_lab_manual_mode_polygon",
    "terrain_lab_manual_mode_boundary",
    "terrain_lab_manual_mode_delete_polygon",
    "terrain_lab_manual_delete_all_polygons",
    "terrain_lab_manual_finish_polygon",
    "terrain_lab_manual_cancel_polygon",
    "terrain_lab_manual_finish_boundary",
    "terrain_lab_manual_cancel_boundary",
    "terrain_lab_manual_remove_boundary",
    "terrain_lab_manual_finish_geometry",
    "terrain_lab_manual_cancel_draft",
    "terrain_lab_manual_publish_feature",
    "terrain_lab_manual_line_width",
    "terrain_lab_manual_outside_surface",
    "terrain_lab_manual_outside_biotope",
    "terrain_lab_manual_outside_elevation",
    "terrain_lab_cluster_clusters",
    "terrain_lab_cluster_spline_radius",
    "terrain_lab_cluster_smooth_passes",
    "terrain_lab_cluster_min_land_region",
    "terrain_lab_cluster_water_sensitivity",
    "terrain_lab_cluster_color_weight",
    "terrain_lab_cluster_luma_weight",
    "terrain_lab_cluster_saturation_weight",
    "terrain_lab_cluster_texture_weight",
    "terrain_lab_cluster_slope_weight",
    "terrain_lab_cluster_spatial_weight",
    "terrain_lab_cluster_detail_weight",
    "terrain_lab_cluster_sample_limit",
    "terrain_lab_cluster_iterations",
    "terrain_lab_cluster_seed",
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

        ui_source = (
            ROOT / "worldbox_mod" / "TerrainLab" / "Code" / "TerrainLabUi.cs"
        ).read_text(encoding="utf-8")
        numeric_row_keys = set(
            re.findall(
                r'CreateNumericInputRow\(\s*"[^"]+",\s*"([^"]+)"',
                ui_source,
            )
        )
        self.assertTrue(numeric_row_keys)
        for locale in locales.values():
            for key in numeric_row_keys:
                self.assertTrue(locale[key])
                self.assertTrue(locale[f"{key}_description"])

        tooltip_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainParameterTooltip.cs"
        ).read_text(encoding="utf-8")
        self.assertIn("IPointerEnterHandler", tooltip_source)
        self.assertIn("IPointerClickHandler", tooltip_source)
        self.assertIn("ISelectHandler", tooltip_source)
        self.assertIn("Tooltip.show(", tooltip_source)
        self.assertIn("Tooltip.hideTooltip();", tooltip_source)
        self.assertIn(
            "target.AddComponent<TerrainParameterTooltip>();",
            ui_source,
        )
        self.assertIn(
            '"terrain_lab_water_maximum_flood_percent",\n'
            "                parameters.MaximumFloodPercent.ToString(),\n"
            "                HandleWaterMaximumFloodChanged,\n"
            "                3);",
            ui_source,
        )

    def test_manual_image_picker_requires_explicit_confirmation(self) -> None:
        overlay_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainImageClassificationOverlay.cs"
        ).read_text(encoding="utf-8")

        self.assertIn("private int _pendingImageIndex = -1;", overlay_source)
        self.assertNotIn("private Dropdown _imageDropdown;", overlay_source)
        self.assertIn(
            "private void SelectPendingImage(int index)",
            overlay_source,
        )
        self.assertIn("SelectPendingImage(next);", overlay_source)
        self.assertIn(
            "_fileLabel = CreateFileSelectorField(",
            overlay_source,
        )
        self.assertIn(
            "layout.childControlWidth = true;\n"
            "            layout.childControlHeight = true;\n"
            "            layout.childForceExpandWidth = true;\n"
            "            layout.childForceExpandHeight = false;",
            overlay_source,
        )
        self.assertIn("SetEditorControlsEnabled(false);", overlay_source)
        self.assertNotIn("CycleImage(", overlay_source)
        self.assertIn(
            "private static void StyleDropdownPopup(",
            overlay_source,
        )
        self.assertIn(
            "new UnityColor(0.01f, 0.01f, 0.01f, 1f)",
            overlay_source,
        )
        self.assertIn('arrowText.text = "v";', overlay_source)
        self.assertIn("item.graphic = null;", overlay_source)
        self.assertIn(
            "TerrainImageClassificationDrawMode.MapBoundary",
            overlay_source,
        )
        self.assertIn("_profile.SetMapBoundary(_draftVertices);", overlay_source)
        self.assertIn(
            "_profile.IsInsideMapBoundary(sourceX, sourceY)",
            overlay_source,
        )
        self.assertIn(
            "TerrainImageClassificationDrawMode.DeletePolygon",
            overlay_source,
        )
        self.assertIn(
            "TerrainImageClassificationDrawMode.Line",
            overlay_source,
        )
        self.assertIn("private void PublishFeature()", overlay_source)
        self.assertIn("_profile.AddLine(", overlay_source)
        self.assertIn(
            "_drawMode = TerrainImageClassificationDrawMode.None;",
            overlay_source,
        )
        self.assertIn("ResetClassificationSelection();", overlay_source)
        self.assertIn(
            "GetElevationVertexColor(sample.Elevation)",
            overlay_source,
        )
        self.assertIn(
            "private void ResetFeatureDraft()",
            overlay_source,
        )
        self.assertIn(
            "_surfaceDropdown?.SetValueWithoutNotify(0);",
            overlay_source,
        )
        self.assertIn("int removed = _profile.ClearRegions();", overlay_source)
        self.assertIn(
            "_profile.RemoveRegionAt(sourceX, sourceY)",
            overlay_source,
        )

        confirm_start = overlay_source.index(
            "private void ConfirmImageSelection()"
        )
        confirm_end = overlay_source.index(
            "private void ResetLoadedImage()",
            confirm_start,
        )
        confirm_source = overlay_source[confirm_start:confirm_end]
        self.assertIn("OpenImage(_pendingImageIndex);", confirm_source)

        select_start = overlay_source.index(
            "private void SelectPendingImage(int index)"
        )
        select_end = overlay_source.index(
            "private void MovePendingImage(int direction)",
            select_start,
        )
        select_source = overlay_source[select_start:select_end]
        self.assertLess(
            select_source.index("SaveProfile();"),
            select_source.index("ResetLoadedImage();"),
        )

        graphic_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainImagePolygonGraphic.cs"
        ).read_text(encoding="utf-8")
        self.assertIn(
            "internal static class TerrainImageMorphotypePatterns",
            graphic_source,
        )
        self.assertIn("public override Texture mainTexture", graphic_source)
        self.assertIn("_vertexColor", graphic_source)

        toolbar_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainLabUi.cs"
        ).read_text(encoding="utf-8")
        self.assertIn(
            "railRect.localScale = new Vector3(1f, -1f, 1f);",
            toolbar_source,
        )
        self.assertIn(
            "private const float ToolbarRowGap = 6f;",
            toolbar_source,
        )
        self.assertIn(
            "private const float ToolbarVerticalPadding = 6f;",
            toolbar_source,
        )
        self.assertIn("TerrainLabToolbarRowDivider", toolbar_source)
        self.assertIn("ToolbarDividerShadow", toolbar_source)
        self.assertIn("ToolbarDividerHighlight", toolbar_source)
        self.assertNotIn("TerrainLabToolbarGroupFlag", toolbar_source)

    def test_automatic_clustering_is_independent_and_configurable(self) -> None:
        overlay_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainImageClusteringOverlay.cs"
        ).read_text(encoding="utf-8")
        profile_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainImageClustering.cs"
        ).read_text(encoding="utf-8")
        workspace_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainImageWorkspace.cs"
        ).read_text(encoding="utf-8")
        toolbar_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainLabUi.cs"
        ).read_text(encoding="utf-8")

        self.assertIn(
            'public const string SidecarSuffix = '
            '".terrainlab-clustering.json";',
            profile_source,
        )
        for json_name in (
            "clusters",
            "spline_radius",
            "smooth_passes",
            "min_land_region",
            "water_sensitivity",
            "color_weight",
            "luma_weight",
            "saturation_weight",
            "texture_weight",
            "slope_weight",
            "spatial_weight",
            "detail_weight",
            "sample_limit",
            "kmeans_iterations",
            "random_seed",
        ):
            self.assertIn(f'[JsonProperty("{json_name}")]', profile_source)

        self.assertIn(
            "TerrainImageConversionMode.AutomaticClustering",
            overlay_source,
        )
        self.assertIn("_profile.SetMapBoundary(_draftVertices);", overlay_source)
        self.assertIn("private void ToggleExpertPanel()", overlay_source)
        self.assertEqual(overlay_source.count("CreateParameterRow(") - 1, 15)
        self.assertIn('"image_auto_cluster"', toolbar_source)
        self.assertIn('"image_manual_classify"', toolbar_source)
        self.assertIn(
            'arguments.Append(" --no-classification-profile");',
            workspace_source,
        )
        self.assertIn(
            'arguments.Append(" --clustering-profile ");',
            workspace_source,
        )
        self.assertIn(
            'arguments.Append(" --no-clustering-profile");',
            workspace_source,
        )

    def test_geyser_patch_forwards_the_building_lifecycle(self) -> None:
        patch_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainLabPatches.cs"
        ).read_text(encoding="utf-8")
        service_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainWaterDynamicsService.cs"
        ).read_text(encoding="utf-8")

        self.assertIn(
            "WaterDynamics.NotifyGeyserPulse(\n                    __instance,",
            patch_source,
        )
        self.assertIn(
            "public void NotifyGeyserPulse(Building geyser, int pulseCount)",
            service_source,
        )
        self.assertIn("_geyserBuildings[geyserIndex] = geyser;", service_source)
        self.assertIn("GeyserRemovalGraceSeconds", service_source)

    def test_surface_edits_invalidate_worldbox_path_regions(self) -> None:
        world_state_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainWorldState.cs"
        ).read_text(encoding="utf-8")

        self.assertIn(
            "TileTypeBase previousType = tile.Type;",
            world_state_source,
        )
        self.assertIn(
            "MapAction.checkTileState(tile, previousType);",
            world_state_source,
        )

    def test_elevation_nodata_is_reserved(self) -> None:
        self.assertEqual(ELEVATION_NODATA, 9999)
        self.assertEqual((ELEVATION_MINIMUM, ELEVATION_MAXIMUM), (-20000, 9000))

    def test_map_budget_allows_extreme_aspect_ratios(self) -> None:
        validate_map_size(40, 10)
        validate_map_size(23, 20)

    def test_map_budget_rejects_excess_total_cells(self) -> None:
        with self.assertRaisesRegex(ValueError, "TerrainLab limit"):
            validate_map_size(22, 22)

    def test_budget_fit_preserves_common_and_extreme_aspects(self) -> None:
        self.assertEqual(fit_map_size_to_budget(1000, 1000), (21, 21))
        self.assertEqual(fit_map_size_to_budget(1600, 900), (28, 16))
        self.assertEqual(fit_map_size_to_budget(4000, 100), (120, 3))
        self.assertEqual(fit_map_size_to_budget(100, 4000), (3, 120))

    def test_safe_palette_excludes_gameplay_hazards(self) -> None:
        self.assertFalse(set(SAFE_TILES_TUPLE) & set(UNPLAYABLE_TILES))

    def test_uniform_image_converts(self) -> None:
        source = Image.new("RGB", (128, 128), (100, 120, 140))
        converted = convert(source, width=2, height=2)

        self.assertEqual((converted.width, converted.height), (2, 2))
        self.assertEqual(converted.preview.size, (128, 128))

    def test_clustering_profile_round_trips_and_masks_background(self) -> None:
        rng = np.random.default_rng(2026)
        pixels = rng.integers(0, 255, size=(128, 128, 3), dtype=np.uint8)
        pixels[20:109, 20:109] = (112, 126, 104)
        source = Image.fromarray(pixels, mode="RGB")
        profile = ClusteringProfile(
            source_file_name="clustered.png",
            source_width=128,
            source_height=128,
            clusters=9,
            spline_radius=2,
            smooth_passes=2,
            min_land_region=24,
            water_sensitivity=1.15,
            color_weight=1.25,
            luma_weight=0.9,
            saturation_weight=1.1,
            texture_weight=0.4,
            slope_weight=1.2,
            spatial_weight=0.3,
            detail_weight=0.6,
            sample_limit=12_000,
            kmeans_iterations=24,
            random_seed=77,
            map_boundary=((20, 20), (108, 20), (108, 108), (20, 108)),
        )
        with tempfile.TemporaryDirectory() as temporary_directory:
            path = Path(temporary_directory) / "cluster.json"
            path.write_text(
                json.dumps(profile.to_json_dict()),
                encoding="utf-8",
            )
            restored = load_clustering_profile(path, source.size)

        self.assertEqual(restored, profile)
        converted = convert(
            source,
            width=2,
            height=2,
            clustering_profile=restored,
        )
        try:
            mask = rasterize_clustering_boundary(restored, 128, 128)
            tiles = np.asarray(converted.preview)
            deep_ocean = SAFE_TILES_TUPLE.index("deep_ocean")
            self.assertTrue(np.all(tiles[~mask] == deep_ocean))
            self.assertEqual(
                converted.clustering_profile["settings"]["clusters"],
                9,
            )
            self.assertIsNone(converted.classification_profile)
        finally:
            converted.preview.close()
            source.close()

    def test_manual_and_clustering_profiles_are_exclusive(self) -> None:
        source = Image.new("RGB", (64, 64), (100, 120, 140))
        manual = ClassificationProfile(
            source_file_name="exclusive.png",
            source_width=64,
            source_height=64,
            samples=(
                ClassificationSample(8, 8, "plain", "grass", 100),
                ClassificationSample(48, 48, "rocks", "none", 2500),
            ),
        )
        automatic = ClusteringProfile(
            source_file_name="exclusive.png",
            source_width=64,
            source_height=64,
        )
        try:
            with self.assertRaisesRegex(ValueError, "mutually exclusive"):
                convert(
                    source,
                    width=1,
                    height=1,
                    classification_profile=manual,
                    clustering_profile=automatic,
                )
        finally:
            source.close()

    def test_manual_samples_split_same_colour_and_interpolate_dem(self) -> None:
        source = Image.new("RGB", (128, 64), (112, 126, 104))
        profile = ClassificationProfile(
            source_file_name="same-colour.png",
            source_width=128,
            source_height=64,
            samples=(
                ClassificationSample(8, 32, "rocks", "none", 3200),
                ClassificationSample(119, 32, "plain", "grass", 120),
            ),
        )
        converted = convert(
            source,
            width=2,
            height=1,
            classification_profile=profile,
        )
        try:
            palette = SAFE_TILES_TUPLE
            pixels = converted.preview.load()
            self.assertEqual(palette[pixels[8, 32]], "mountains")
            self.assertEqual(palette[pixels[119, 32]], "soil_low:grass_low")
            self.assertEqual(converted.elevation.shape, (64, 128))
            self.assertEqual(int(converted.elevation[32, 8]), 3200)
            self.assertEqual(int(converted.elevation[32, 119]), 120)
            self.assertGreater(
                int(converted.elevation[32, 32]),
                int(converted.elevation[32, 96]),
            )
            self.assertFalse(np.any(converted.elevation == ELEVATION_NODATA))
        finally:
            converted.preview.close()
            source.close()

    def test_manual_polygons_aggressively_split_same_colour_and_dem(self) -> None:
        source = Image.new("RGB", (128, 64), (112, 126, 104))
        profile = ClassificationProfile(
            source_file_name="same-colour-polygons.png",
            source_width=128,
            source_height=64,
            samples=(),
            regions=(
                ClassificationRegion(
                    ((2, 2), (58, 2), (58, 61), (2, 61)),
                    "rocks",
                    "none",
                    3200,
                ),
                ClassificationRegion(
                    ((69, 2), (125, 2), (125, 61), (69, 61)),
                    "plain",
                    "grass",
                    120,
                ),
            ),
        )
        converted = convert(
            source,
            width=2,
            height=1,
            classification_profile=profile,
        )
        try:
            pixels = converted.preview.load()
            self.assertEqual(
                SAFE_TILES_TUPLE[pixels[20, 32]],
                "mountains",
            )
            self.assertEqual(
                SAFE_TILES_TUPLE[pixels[108, 32]],
                "soil_low:grass_low",
            )
            self.assertEqual(int(converted.elevation[32, 20]), 3200)
            self.assertEqual(int(converted.elevation[32, 108]), 120)
            self.assertGreater(
                int(converted.elevation[32, 48]),
                int(converted.elevation[32, 80]),
            )
            self.assertFalse(np.any(converted.elevation == ELEVATION_NODATA))
        finally:
            converted.preview.close()
            source.close()

    def test_manual_line_is_authoritative_and_trains_conversion(self) -> None:
        source = Image.new("RGB", (128, 64), (112, 126, 104))
        profile = ClassificationProfile(
            source_file_name="line.png",
            source_width=128,
            source_height=64,
            samples=(),
            lines=(
                ClassificationLine(
                    ((8, 32), (64, 20), (119, 32)),
                    "rocks",
                    "none",
                    2800,
                    5,
                ),
            ),
        )
        converted = convert(
            source,
            width=2,
            height=1,
            classification_profile=profile,
        )
        try:
            self.assertEqual(
                SAFE_TILES_TUPLE[converted.preview.getpixel((64, 20))],
                "mountains",
            )
            self.assertEqual(int(converted.elevation[20, 64]), 2800)
            self.assertEqual(int(converted.elevation[22, 64]), 2800)
        finally:
            converted.preview.close()
            source.close()

    def test_map_boundary_excludes_noise_and_forces_deep_ocean(self) -> None:
        first_pixels = np.full((128, 128, 3), (255, 0, 255), dtype=np.uint8)
        second_pixels = np.zeros((128, 128, 3), dtype=np.uint8)
        second_pixels[:, :, 0] = (
            np.indices((128, 128)).sum(axis=0) % 2
        ) * 255
        second_pixels[:, :, 2] = 255 - second_pixels[:, :, 0]
        first_pixels[20:109, 20:109] = (112, 126, 104)
        second_pixels[20:109, 20:109] = (112, 126, 104)
        first = Image.fromarray(first_pixels, mode="RGB")
        second = Image.fromarray(second_pixels, mode="RGB")
        boundary = ((20, 20), (108, 20), (108, 108), (20, 108))
        interior_samples = (
            ClassificationSample(36, 64, "plain", "grass", 120),
            ClassificationSample(92, 64, "rocks", "none", 3200),
        )
        noisy_profile = ClassificationProfile(
            source_file_name="noisy-boundary.png",
            source_width=128,
            source_height=128,
            samples=interior_samples
            + (ClassificationSample(3, 3, "summit", "none", 9000),),
            map_boundary=boundary,
        )
        clean_profile = ClassificationProfile(
            source_file_name="clean-boundary.png",
            source_width=128,
            source_height=128,
            samples=interior_samples,
            map_boundary=boundary,
        )

        noisy = convert(
            first,
            width=2,
            height=2,
            classification_profile=noisy_profile,
        )
        clean = convert(
            second,
            width=2,
            height=2,
            classification_profile=clean_profile,
        )
        try:
            noisy_tiles = np.asarray(noisy.preview)
            clean_tiles = np.asarray(clean.preview)
            mask = rasterize_map_boundary(noisy_profile, 128, 128)
            self.assertIsNotNone(mask)
            self.assertTrue(np.array_equal(noisy_tiles, clean_tiles))
            self.assertTrue(
                np.array_equal(noisy.elevation, clean.elevation)
            )
            deep_ocean = SAFE_TILES_TUPLE.index("deep_ocean")
            self.assertTrue(np.all(noisy_tiles[~mask] == deep_ocean))
            self.assertTrue(np.all(noisy.elevation[~mask] == -4000))
            self.assertEqual(
                SAFE_TILES_TUPLE[noisy.preview.getpixel((36, 64))],
                "soil_low:grass_low",
            )
            self.assertEqual(
                SAFE_TILES_TUPLE[noisy.preview.getpixel((92, 64))],
                "mountains",
            )
        finally:
            noisy.preview.close()
            clean.preview.close()
            first.close()
            second.close()

    def test_map_boundary_supports_custom_safe_outside_class(self) -> None:
        source = Image.new("RGB", (128, 128), (112, 126, 104))
        profile = ClassificationProfile(
            source_file_name="custom-outside.png",
            source_width=128,
            source_height=128,
            samples=(
                ClassificationSample(40, 64, "plain", "grass", 120),
                ClassificationSample(88, 64, "rocks", "none", 2400),
            ),
            map_boundary=((20, 20), (108, 20), (108, 108), (20, 108)),
            outside_surface="sand",
            outside_biotope="none",
            outside_elevation=7,
        )
        converted = convert(
            source,
            width=2,
            height=2,
            classification_profile=profile,
        )
        try:
            mask = rasterize_map_boundary(profile, 128, 128)
            tiles = np.asarray(converted.preview)
            sand = SAFE_TILES_TUPLE.index("sand")
            self.assertTrue(np.all(tiles[~mask] == sand))
            self.assertTrue(np.all(converted.elevation[~mask] == 7))
        finally:
            converted.preview.close()
            source.close()

    def test_manual_polygon_profile_round_trips_and_rejects_bow_tie(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            profile_path = Path(temporary_directory) / "polygon.json"
            profile = ClassificationProfile(
                source_file_name="polygon.png",
                source_width=64,
                source_height=64,
                samples=(),
                regions=(
                    ClassificationRegion(
                        ((4, 4), (50, 4), (50, 50), (4, 50)),
                        "upland",
                        "savanna",
                        900,
                    ),
                ),
                lines=(
                    ClassificationLine(
                        ((8, 54), (32, 40), (55, 54)),
                        "river_lake",
                        "none",
                        300,
                        2,
                    ),
                ),
                map_boundary=((2, 2), (61, 2), (61, 61), (2, 61)),
                outside_surface="sand",
                outside_biotope="none",
                outside_elevation=4,
            )
            profile_path.write_text(
                json.dumps(profile.to_json_dict()),
                encoding="utf-8-sig",
            )
            restored = load_classification_profile(
                profile_path,
                (64, 64),
            )
            self.assertEqual(restored.samples, ())
            self.assertEqual(restored.regions, profile.regions)
            self.assertEqual(restored.lines, profile.lines)
            self.assertEqual(restored.map_boundary, profile.map_boundary)
            self.assertEqual(restored.outside_surface, "sand")
            self.assertEqual(restored.outside_elevation, 4)

            payload = profile.to_json_dict()
            payload["regions"][0]["vertices"] = [
                {"x": 4, "y": 4},
                {"x": 50, "y": 50},
                {"x": 4, "y": 50},
                {"x": 50, "y": 4},
            ]
            profile_path.write_text(
                json.dumps(payload),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "self-intersecting"):
                load_classification_profile(profile_path, (64, 64))

            payload = profile.to_json_dict()
            payload["map_boundary"]["vertices"] = [
                {"x": 2, "y": 2},
                {"x": 61, "y": 61},
                {"x": 2, "y": 61},
                {"x": 61, "y": 2},
            ]
            profile_path.write_text(
                json.dumps(payload),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "self-intersecting"):
                load_classification_profile(profile_path, (64, 64))

    def test_manual_save_contains_profile_and_signed_dem_geotiff(self) -> None:
        source = Image.new("RGB", (64, 64), (90, 140, 80))
        profile = ClassificationProfile(
            source_file_name="manual.png",
            source_width=64,
            source_height=64,
            samples=(
                ClassificationSample(4, 4, "deep_ocean", "auto", -4000),
                ClassificationSample(59, 59, "summit", "none", 5000),
            ),
        )
        converted = convert(
            source,
            width=1,
            height=1,
            classification_profile=profile,
        )
        try:
            with tempfile.TemporaryDirectory() as temporary_directory:
                destination = Path(temporary_directory) / "save1"
                write_game_save_atomically(
                    output_path=destination,
                    converted_map=converted,
                    name="Manual map",
                )
                self.assertTrue(
                    (destination / GENERATED_ELEVATION_FILE_NAME).is_file()
                )
                self.assertTrue(
                    (destination / GENERATED_PROFILE_FILE_NAME).is_file()
                )
                with Image.open(
                    destination / GENERATED_ELEVATION_FILE_NAME
                ) as dem:
                    self.assertEqual(dem.size, (64, 64))
                    self.assertEqual(dem.tag_v2[258], (16,))
                    self.assertEqual(dem.tag_v2[339], (2,))
                    self.assertEqual(
                        dem.tag_v2[42113].rstrip("\0"),
                        str(ELEVATION_NODATA),
                    )
        finally:
            converted.preview.close()
            source.close()

    def test_manual_profile_rejects_reserved_nodata(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            profile_path = Path(temporary_directory) / "invalid.json"
            profile_path.write_text(
                json.dumps(
                    {
                        "schema_version": 1,
                        "source": {
                            "file_name": "invalid.png",
                            "width": 2,
                            "height": 2,
                        },
                        "samples": [
                            {
                                "x": 0,
                                "y": 0,
                                "surface": "plain",
                                "biotope": "auto",
                                "elevation": ELEVATION_NODATA,
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "reserved"):
                load_classification_profile(profile_path, (2, 2))

    def test_game_save_is_published_as_a_complete_directory(self) -> None:
        source = Image.new("RGB", (64, 64), (70, 130, 80))
        converted = convert(source, width=1, height=1)
        try:
            test_temp_root = ROOT / "tests" / "_tmp"
            test_temp_root.mkdir(exist_ok=True)
            with tempfile.TemporaryDirectory(
                dir=test_temp_root,
            ) as temporary_directory:
                destination = Path(temporary_directory) / "save1"
                write_game_save_atomically(
                    output_path=destination,
                    converted_map=converted,
                    name="Atomic map",
                )

                self.assertEqual(
                    {path.name for path in destination.iterdir()},
                    {
                        "map.meta",
                        "map.wbox",
                        "map_stats.s3db",
                        "preview.png",
                        "preview_small.png",
                    },
                )
                self.assertFalse(
                    any(
                        path.name.startswith("terrainlab-staging-save1-")
                        for path in destination.parent.iterdir()
                    ),
                )
        finally:
            converted.preview.close()
            source.close()

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
