import colorsys
import json
import re
import tempfile
import unittest
import zlib
from dataclasses import replace
from pathlib import Path
from unittest.mock import patch

import numpy as np
from PIL import Image, ImageDraw

from imagetomap import (
    convert,
    fit_map_size_to_budget,
    fit_map_size_to_long_side,
    maximum_map_size_for_aspect,
    validate_map_size,
)
from imagetomap.__main__ import process_image
from imagetomap.calibration import (
    ClassificationLine,
    ClassificationProfile,
    ClassificationRegion,
    ClassificationSample,
    GENERATED_ELEVATION_FILE_NAME,
    GENERATED_PROFILE_FILE_NAME,
    SURFACE_IDS,
    _surface_slope_caps,
    classification_processing_extent,
    crop_classification_profile,
    load_classification_profile,
    rasterize_map_boundary,
    write_int16_geotiff,
)
from imagetomap.clustering import (
    ClusteringProfile,
    LEGACY_CLUSTERING_ALGORITHM,
    SEMANTIC_CLUSTERING_ALGORITHM,
    clustering_processing_extent,
    crop_clustering_profile,
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
from imagetomap.georeference import (
    GEOREFERENCE_FILE_NAME,
    RasterGeoreference,
    apply_transform,
    read_raster_georeference,
)
from imagetomap.saves import write_game_save_atomically, write_map_folder
from imagetomap.semantic import (
    NO_CLASS,
    categorical_area_resample,
    derive_semantic_raster,
    make_index_image,
)
from imagetomap.terrain import (
    TerrainClusteringSettings,
    assign_unique_cluster_tiles,
    classify_adaptive_terrain,
    fill_small_land_regions,
)
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
    "terrain_lab_manual_outside_auto",
    "terrain_lab_manual_outside_biotope",
    "terrain_lab_manual_outside_elevation",
    "terrain_lab_cluster_clusters",
    "terrain_lab_cluster_spline_radius",
    "terrain_lab_cluster_smooth_passes",
    "terrain_lab_cluster_min_land_region",
    "terrain_lab_cluster_water_sensitivity",
    "terrain_lab_cluster_analysis_max_dimension",
    "terrain_lab_cluster_algorithm_legacy",
    "terrain_lab_cluster_algorithm_semantic",
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
    "terrain_lab_cluster_budget",
    "terrain_lab_cluster_composition_surfaces",
    "terrain_lab_cluster_composition_biotopes",
)


class TerrainConversionTests(unittest.TestCase):
    def test_new_project_save_uses_native_slot_picker_and_guards_empty_path(
        self,
    ) -> None:
        ui_source = (
            ROOT / "worldbox_mod" / "TerrainLab" / "Code" / "TerrainLabUi.cs"
        ).read_text(encoding="utf-8")
        confirm_save_source = ui_source.split(
            "private void ConfirmSaveProject()",
            maxsplit=1,
        )[1].split(
            "private void CancelSaveProject()",
            maxsplit=1,
        )[0]
        self.assertIn(
            'ScrollWindow.showWindow("saves_list");',
            confirm_save_source,
        )
        self.assertNotIn(
            'ScrollWindow.showWindow("save_world_confirm");',
            confirm_save_source,
        )

        patch_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainLabPatches.cs"
        ).read_text(encoding="utf-8")
        self.assertIn(
            "internal static class TerrainLabSaveSlotGuardPatch",
            patch_source,
        )
        self.assertIn(
            "string.IsNullOrWhiteSpace(SaveManager.currentSavePath)",
            patch_source,
        )
        self.assertIn(
            'ScrollWindow.showWindow("saves_list");',
            patch_source,
        )

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
            "long_side_blocks",
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
        self.assertEqual(overlay_source.count("CreateParameterRow(") - 1, 16)
        boundary_controls = overlay_source.split(
            "Transform boundaryModes =",
            maxsplit=1,
        )[1].split(
            "_clearBoundaryButton =",
            maxsplit=1,
        )[0]
        self.assertIn(
            "CreateFlexibleButtonRow(content, 28f);",
            boundary_controls,
        )
        self.assertEqual(boundary_controls.count("CreateFlexibleButton("), 3)
        self.assertNotIn("92f", boundary_controls)
        self.assertIn(
            "element.flexibleWidth = 1f;",
            overlay_source,
        )
        self.assertIn("_longSideInput", overlay_source)
        self.assertIn("UpdateMapSizePreview()", overlay_source)
        self.assertIn(
            "TerrainMapLimits.TryGetMaximumBlockDimensions(",
            overlay_source,
        )
        self.assertIn("CreateCompositionPalette(", overlay_source)
        self.assertIn("SetCompositionCategory(true)", overlay_source)
        self.assertIn("SetCompositionCategory(false)", overlay_source)
        self.assertIn(
            "TerrainImageUiVisuals.GetActivitySprite(toggle.isOn)",
            overlay_source,
        )
        self.assertIn(
            "new UnityColor(1f, 0.82f, 0.22f, 1f)",
            overlay_source,
        )
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

    def test_inspector_remains_active_with_other_tools_and_layers(self) -> None:
        editor_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainLabEditor.cs"
        ).read_text(encoding="utf-8")
        ui_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainLabUi.cs"
        ).read_text(encoding="utf-8")

        self.assertIn(
            "public bool InspectorEnabled { get; private set; }",
            editor_source,
        )
        self.assertIn(
            "public void SetInspectorEnabled(bool enabled)",
            editor_source,
        )
        select_start = ui_source.index(
            "private void SelectEditorTool(TerrainEditorTool tool)"
        )
        select_end = ui_source.index(
            "private void UpdateEditorToolSelection()",
            select_start,
        )
        select_source = ui_source[select_start:select_end]
        self.assertLess(
            select_source.index("tool == TerrainEditorTool.Inspect"),
            select_source.index("if (_editor.Tool == tool)"),
        )
        self.assertEqual(
            select_source.count("_editor.SetInspectorEnabled(enabled);"),
            1,
        )
        self.assertIn("_editor.InspectorEnabled;", ui_source)
        self.assertIn(
            "GetDataOverlayCellValue(_dataOverlay.Mode, state, tile)",
            ui_source,
        )

    def test_layer_legends_match_gis_layer_semantics(self) -> None:
        legend_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainLayerLegend.cs"
        ).read_text(encoding="utf-8")
        ui_source = (
            ROOT / "worldbox_mod" / "TerrainLab" / "Code" / "TerrainLabUi.cs"
        ).read_text(encoding="utf-8")
        locale_directory = ROOT / "worldbox_mod" / "TerrainLab" / "Locales"
        locales = {
            name: json.loads((locale_directory / name).read_text(encoding="utf-8"))
            for name in ("en.json", "ru.json")
        }

        self.assertIn("TerrainLayerLegendKind.Continuous", legend_source)
        self.assertIn("TerrainLayerLegendKind.Categories", legend_source)
        self.assertIn("private const int GradientResolution = 256;", legend_source)
        self.assertIn("typeof(RectMask2D)", legend_source)
        self.assertIn("TerrainLabLegendBiotopeTile_", legend_source)
        self.assertIn("const int repeats = 4;", legend_source)
        self.assertIn("terrainlab/legend/panel_top", legend_source)
        self.assertNotIn("terrainlab/legend/panel_bottom", legend_source)
        self.assertIn("terrainlab/legend/scale_continuous", legend_source)
        self.assertIn("terrainlab/legend/scale_categorical", legend_source)
        self.assertIn(
            "rect.pivot = new Vector2(0.5f, 0f);",
            legend_source,
        )
        self.assertIn(
            "rect.localScale = new Vector3(1f, -1f, 1f);",
            legend_source,
        )
        self.assertIn(
            "ReferenceEquals(target, _bottomCap)",
            legend_source,
        )
        self.assertIn("ToolbarButtons.instance.main_background", legend_source)
        self.assertIn("ToolbarButtons.getSpriteButtonNormal()", legend_source)
        self.assertIn("ResolveWorldSprites(", legend_source)
        self.assertIn("TryGetTileSprite(", legend_source)
        self.assertIn("UpdateLayerLegend(state);", ui_source)
        self.assertIn(
            "_workspaceVisible &&\n"
            "                _classificationOverlay?.IsVisible != true",
            ui_source,
        )

        required_keys = {
            "terrain_lab_legend_maximum_format",
            "terrain_lab_legend_minimum_format",
            "terrain_lab_legend_unit_metres",
            "terrain_lab_legend_unit_degrees",
            "terrain_lab_legend_unit_percent",
            "terrain_lab_legend_contour_minor",
            "terrain_lab_legend_watershed",
            "terrain_lab_legend_direction_northeast",
        }
        for locale in locales.values():
            self.assertTrue(required_keys.issubset(locale))
            for key in required_keys:
                self.assertTrue(locale[key])

        legend_assets = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "GameResources"
            / "terrainlab"
            / "legend"
        )
        for name in (
            "panel_top.png",
            "panel_bottom.png",
            "scale_continuous.png",
            "scale_categorical.png",
        ):
            self.assertTrue((legend_assets / name).is_file())

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

    def test_long_side_map_size_and_displayed_maximum_share_one_budget(self) -> None:
        self.assertEqual(
            fit_map_size_to_long_side(2000, 1000, 20),
            (20, 10),
        )
        self.assertEqual(
            fit_map_size_to_long_side(1000, 2000, 20),
            (10, 20),
        )
        self.assertEqual(
            maximum_map_size_for_aspect(2000, 1000),
            (30, 15),
        )
        self.assertEqual(
            maximum_map_size_for_aspect(1000, 1000),
            (21, 21),
        )
        with self.assertRaisesRegex(ValueError, "maximum.*30x15"):
            fit_map_size_to_long_side(2000, 1000, 31)

    def test_safe_palette_excludes_gameplay_hazards(self) -> None:
        self.assertFalse(set(SAFE_TILES_TUPLE) & set(UNPLAYABLE_TILES))
        expected_seed_biomes = {
            "grass",
            "savanna",
            "jungle",
            "desert",
            "lemon",
            "permafrost",
            "swamp",
            "crystal",
            "enchanted",
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
        }
        available = {
            tile.split(":", 1)[1].removesuffix("_low")
            for tile in SAFE_TILES_TUPLE
            if tile.startswith("soil_low:")
        }
        self.assertTrue(expected_seed_biomes <= available)

    def test_adaptive_clustering_never_outputs_bare_soil(self) -> None:
        pixels = np.zeros((128, 128, 3), dtype=np.uint8)
        pixels[:64, :64] = (122, 74, 62)
        pixels[:64, 64:] = (82, 82, 82)
        pixels[64:, :64] = (150, 142, 130)
        pixels[64:, 64:] = (92, 132, 78)
        source = Image.fromarray(pixels, mode="RGB")
        converted = convert(
            source,
            width=2,
            height=2,
            terrain_clusters=8,
            terrain_smooth=0,
            terrain_min_region=0,
        )
        try:
            selected = {
                SAFE_TILES_TUPLE[int(index)]
                for index in np.unique(np.asarray(converted.preview))
            }
            self.assertNotIn("soil_low", selected)
            self.assertNotIn("soil_high", selected)
            self.assertTrue(
                any(
                    tile.startswith("soil_") and ":" in tile
                    for tile in selected
                )
            )
        finally:
            converted.preview.close()
            source.close()

    def test_legacy_manual_none_soil_becomes_living_biome(self) -> None:
        source = Image.new("RGB", (64, 64), (112, 108, 96))
        profile = ClassificationProfile(
            source_file_name="legacy-none.png",
            source_width=64,
            source_height=64,
            samples=(
                ClassificationSample(4, 4, "plain", "none", 120),
                ClassificationSample(59, 59, "upland", "none", 900),
            ),
        )
        converted = convert(
            source,
            width=1,
            height=1,
            classification_profile=profile,
        )
        try:
            selected = {
                SAFE_TILES_TUPLE[int(index)]
                for index in np.unique(np.asarray(converted.preview))
            }
            self.assertNotIn("soil_low", selected)
            self.assertNotIn("soil_high", selected)
            self.assertTrue(
                all(
                    ":" in tile
                    for tile in selected
                    if tile.startswith("soil_")
                )
            )
        finally:
            converted.preview.close()
            source.close()

    def test_manual_ui_excludes_bare_biotope_and_seeds_native_vegetation(
        self,
    ) -> None:
        catalog_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainImageClassification.cs"
        ).read_text(encoding="utf-8")
        overlay_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainImageClassificationOverlay.cs"
        ).read_text(encoding="utf-8")
        seeder_source = (
            ROOT
            / "worldbox_mod"
            / "TerrainLab"
            / "Code"
            / "TerrainVegetationSeeder.cs"
        ).read_text(encoding="utf-8")

        self.assertIn("SelectableBiotopes = Biotopes", catalog_source)
        self.assertIn("QuickPaletteSurfaces = Surfaces", catalog_source)
        self.assertNotIn(
            "TerrainImageClassificationCatalog.Biotopes",
            overlay_source,
        )
        self.assertIn("CreateBiotopePalette(_viewport)", overlay_source)
        self.assertIn("FindBiotopeSurfaceSprite", overlay_source)
        self.assertIn(
            "TerrainImageUiVisuals.GetSurfaceSprite(id)",
            overlay_source,
        )
        self.assertIn("FindSurfaceDropdownIndex", overlay_source)
        self.assertIn("VertexSnapRadiusSourcePixels = 40", overlay_source)
        self.assertIn("TrySnapToExistingVertex(", overlay_source)
        self.assertIn("snapped = null;\n                return false;", overlay_source)
        self.assertIn("_longSideInput", overlay_source)
        self.assertIn("UpdateMapSizePreview()", overlay_source)
        self.assertIn(
            "new UnityColor(1f, 0.82f, 0.22f, 1f)",
            overlay_source,
        )
        self.assertIn(
            "GetOutputAspectDimensions(out width, out height)",
            overlay_source,
        )
        self.assertIn(
            "TerrainMapLimits.TryGetMaximumBlockDimensions(",
            overlay_source,
        )
        self.assertIn(
            "BuildingActions.tryGrowVegetationRandom(",
            seeder_source,
        )
        for vegetation_type in ("Trees", "Plants", "Bushes"):
            self.assertIn(
                f"VegetationType.{vegetation_type}",
                seeder_source,
            )
        self.assertIn("WorkPerFrame = 96", seeder_source)
        self.assertIn(
            '"terrainlab_initial_vegetation_v2"',
            seeder_source,
        )
        self.assertIn("TilesPerSeed = 48", seeder_source)
        self.assertIn("MaximumSeedCount = 16384", seeder_source)
        self.assertIn("private static bool TrySeedTile(", seeder_source)
        self.assertIn(
            "MaximumCandidateCount = MaximumSeedCount * 8",
            seeder_source,
        )
        self.assertIn("mapStats.custom_data.addFlag", seeder_source)

    def test_uniform_image_converts(self) -> None:
        source = Image.new("RGB", (128, 128), (100, 120, 140))
        converted = convert(source, width=2, height=2)

        self.assertEqual((converted.width, converted.height), (2, 2))
        self.assertEqual(converted.preview.size, (128, 128))

    def test_requested_land_clusters_remain_distinct(self) -> None:
        pixels = np.empty((120, 300, 3), dtype=np.uint8)
        for index in range(15):
            hue = 0.01 + index * 0.017
            saturation = 0.48 + (index % 4) * 0.12
            value = 0.42 + (index % 5) * 0.11
            color = colorsys.hsv_to_rgb(hue, saturation, value)
            pixels[:, index * 20 : (index + 1) * 20] = [
                round(channel * 255)
                for channel in color
            ]

        source = Image.fromarray(pixels, mode="RGB")
        clustered = classify_adaptive_terrain(
            source,
            SAFE_TILES_TUPLE,
            clustering_settings=TerrainClusteringSettings(
                clusters=15,
                smooth_passes=0,
                min_land_region=0,
            ),
        )
        try:
            selected = {
                SAFE_TILES_TUPLE[int(index)]
                for index in np.unique(np.asarray(clustered))
            }
            self.assertEqual(len(selected), 15)
            self.assertNotIn("soil_low", selected)
            self.assertNotIn("soil_high", selected)
        finally:
            clustered.close()
            source.close()

    def test_cluster_budget_caps_water_and_land_classes_together(self) -> None:
        pixels = np.empty((160, 320, 3), dtype=np.uint8)
        pixels[:, :120] = (34, 92, 168)
        for index in range(10):
            hue = 0.02 + index * 0.035
            color = colorsys.hsv_to_rgb(
                hue,
                0.55 + (index % 3) * 0.12,
                0.48 + (index % 4) * 0.10,
            )
            pixels[:, 120 + index * 20 : 140 + index * 20] = [
                round(channel * 255)
                for channel in color
            ]

        source = Image.fromarray(pixels, mode="RGB")
        clustered = classify_adaptive_terrain(
            source,
            SAFE_TILES_TUPLE,
            clustering_settings=TerrainClusteringSettings(
                clusters=6,
                smooth_passes=0,
                min_land_region=0,
            ),
        )
        try:
            selected = {
                SAFE_TILES_TUPLE[int(index)]
                for index in np.unique(np.asarray(clustered))
            }
            self.assertLessEqual(len(selected), 6)
            self.assertTrue(selected & WATER_TILES)
            self.assertTrue(selected - WATER_TILES)
        finally:
            clustered.close()
            source.close()

    def test_dominant_cluster_gets_first_choice_from_palette(self) -> None:
        scores = np.asarray(
            (
                (0.0, 100.0),
                (0.1, 0.2),
            ),
            dtype=np.float32,
        )

        assignments = assign_unique_cluster_tiles(
            scores,
            np.asarray((1, 100), dtype=np.int64),
        )

        self.assertEqual(assignments.tolist(), [1, 0])

    def test_clustering_profile_round_trips_and_masks_background(self) -> None:
        rng = np.random.default_rng(2026)
        pixels = rng.integers(0, 255, size=(128, 128, 3), dtype=np.uint8)
        pixels[20:109, 20:109] = (112, 126, 104)
        source = Image.fromarray(pixels, mode="RGB")
        profile = ClusteringProfile(
            source_file_name="clustered.png",
            source_width=128,
            source_height=128,
            long_side_blocks=18,
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
            map_boundary=((64, 20), (108, 64), (64, 108), (20, 64)),
        )
        with tempfile.TemporaryDirectory() as temporary_directory:
            path = Path(temporary_directory) / "cluster.json"
            path.write_text(
                json.dumps(profile.to_json_dict()),
                encoding="utf-8",
            )
            restored = load_clustering_profile(path, source.size)
            legacy_payload = profile.to_json_dict()
            legacy_payload["schema_version"] = 2
            legacy_payload["settings"].pop("long_side_blocks")
            path.write_text(
                json.dumps(legacy_payload),
                encoding="utf-8",
            )
            migrated = load_clustering_profile(path, source.size)

        self.assertEqual(restored, profile)
        self.assertEqual(profile.to_json_dict()["schema_version"], 4)
        self.assertEqual(
            restored.algorithm_id,
            SEMANTIC_CLUSTERING_ALGORITHM,
        )
        self.assertEqual(
            migrated.algorithm_id,
            LEGACY_CLUSTERING_ALGORITHM,
        )
        self.assertEqual(restored.long_side_blocks, 18)
        self.assertEqual(migrated.long_side_blocks, 20)
        converted = convert(
            source,
            width=2,
            height=2,
            clustering_profile=restored,
        )
        try:
            extent = clustering_processing_extent(restored)
            working_profile = crop_clustering_profile(restored, extent)
            mask = rasterize_clustering_boundary(
                working_profile,
                128,
                128,
            )
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

    def test_semantic_v2_analyzes_before_worldbox_downsampling(self) -> None:
        source = Image.new("RGB", (300, 180), (80, 120, 160))
        profile = ClusteringProfile(
            source_file_name="native.png",
            source_width=300,
            source_height=180,
            algorithm_id=SEMANTIC_CLUSTERING_ALGORITHM,
            algorithm_version=2,
            analysis_max_dimension=512,
            clusters=4,
        )

        def classify_at_received_size(*args, **kwargs):
            image = kwargs["image"]
            return make_index_image(
                np.zeros((image.height, image.width), dtype=np.uint8),
                SAFE_TILES_TUPLE,
            )

        try:
            with patch(
                "imagetomap.core.classify_adaptive_terrain",
                side_effect=classify_at_received_size,
            ) as classifier:
                converted = convert(
                    source,
                    width=1,
                    height=1,
                    clustering_profile=profile,
                )
            try:
                received = classifier.call_args.kwargs["image"]
                self.assertEqual(received.size, source.size)
                self.assertEqual(converted.preview.size, (64, 64))
                self.assertIsNotNone(converted.semantic)
            finally:
                converted.preview.close()
        finally:
            source.close()

    def test_schema3_profile_preserves_legacy_v1_output(self) -> None:
        y, x = np.indices((128, 128))
        pixels = np.stack(
            (
                ((x * 3) % 256).astype(np.uint8),
                ((y * 2) % 256).astype(np.uint8),
                (((x + y) * 2) % 256).astype(np.uint8),
            ),
            axis=2,
        )
        source = Image.fromarray(pixels, mode="RGB")
        payload = ClusteringProfile(
            source_file_name="legacy.png",
            source_width=128,
            source_height=128,
            algorithm_id=LEGACY_CLUSTERING_ALGORITHM,
            algorithm_version=1,
        ).to_json_dict()
        payload["schema_version"] = 3
        payload.pop("algorithm")
        payload["settings"].pop("analysis_max_dimension")
        try:
            with tempfile.TemporaryDirectory() as temporary_directory:
                profile_path = Path(temporary_directory) / "legacy.json"
                profile_path.write_text(
                    json.dumps(payload),
                    encoding="utf-8",
                )
                migrated = load_clustering_profile(
                    profile_path,
                    source.size,
                )
            self.assertEqual(
                migrated.algorithm_id,
                LEGACY_CLUSTERING_ALGORITHM,
            )
            baseline = convert(source, width=2, height=2)
            restored = convert(
                source,
                width=2,
                height=2,
                clustering_profile=migrated,
            )
            try:
                self.assertTrue(
                    np.array_equal(
                        np.asarray(baseline.preview),
                        np.asarray(restored.preview),
                    )
                )
            finally:
                baseline.preview.close()
                restored.preview.close()
        finally:
            source.close()

    def test_categorical_downsampling_votes_by_covered_area(self) -> None:
        tile_names = SAFE_TILES_TUPLE
        deep = tile_names.index("deep_ocean")
        sand = tile_names.index("sand")
        values = np.asarray(
            (
                (deep, deep, sand, sand),
                (deep, sand, sand, sand),
                (sand, sand, deep, deep),
                (sand, sand, deep, sand),
            ),
            dtype=np.uint8,
        )
        source = make_index_image(values, tile_names)
        try:
            reduced = categorical_area_resample(
                source,
                (2, 2),
                tile_names,
            )
            try:
                self.assertEqual(
                    np.asarray(reduced).tolist(),
                    [[deep, sand], [sand, deep]],
                )
            finally:
                reduced.close()
        finally:
            source.close()

    def test_semantic_hostability_blocks_water_and_rock_biotopes(self) -> None:
        tile_names = SAFE_TILES_TUPLE
        values = np.asarray(
            (
                (
                    tile_names.index("deep_ocean"),
                    tile_names.index("hills"),
                    tile_names.index("mountains"),
                    tile_names.index("soil_low:grass_low"),
                ),
            ),
            dtype=np.uint8,
        )
        image = make_index_image(values, tile_names)
        try:
            semantic = derive_semantic_raster(image, tile_names)
            self.assertEqual(semantic.hostable.tolist(), [[False, False, False, True]])
            self.assertEqual(
                semantic.biotope[0, :3].tolist(),
                [NO_CLASS, NO_CLASS, NO_CLASS],
            )
            self.assertNotEqual(int(semantic.biotope[0, 3]), NO_CLASS)
            semantic.validate()
        finally:
            image.close()

    def test_semantic_v2_writes_diagnostic_layers(self) -> None:
        source = Image.new("RGB", (64, 64), (104, 134, 86))
        profile = ClusteringProfile(
            source_file_name="diagnostics.png",
            source_width=64,
            source_height=64,
            clusters=4,
        )
        converted = convert(
            source,
            width=1,
            height=1,
            clustering_profile=profile,
        )
        try:
            with tempfile.TemporaryDirectory() as temporary_directory:
                output = Path(temporary_directory) / "map"
                write_map_folder(output, converted, "Diagnostics")
                diagnostics = output / "terrainlab-semantic"
                self.assertEqual(
                    {path.name for path in diagnostics.iterdir()},
                    {
                        "biotope.png",
                        "confidence.png",
                        "hostability.png",
                        "hydrology.png",
                        "landform.png",
                        "semantic.json",
                        "substrate.png",
                        "theme.png",
                    },
                )
                metadata = json.loads(
                    (diagnostics / "semantic.json").read_text(
                        encoding="utf-8"
                    )
                )
                self.assertEqual(metadata["schema_version"], 1)
                self.assertEqual(metadata["width"], 64)
                self.assertEqual(metadata["height"], 64)
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
            self.assertEqual(int(converted.elevation[2, 2]), 3200)
            self.assertEqual(int(converted.elevation[2, 69]), 120)
            self.assertGreater(
                int(np.ptp(converted.elevation[3:61, 3:58])),
                0,
            )
            self.assertGreater(
                int(np.ptp(converted.elevation[3:61, 70:125])),
                0,
            )
            self.assertGreater(
                float(np.median(converted.elevation[6:58, 6:54])),
                float(np.median(converted.elevation[6:58, 74:122])),
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
            self.assertNotEqual(int(converted.elevation[22, 64]), 2800)
            self.assertLessEqual(
                abs(
                    int(converted.elevation[22, 64])
                    - int(converted.elevation[21, 64])
                ),
                364,
            )
        finally:
            converted.preview.close()
            source.close()

    def test_shared_vertex_conflict_is_averaged_without_vertical_walls(
        self,
    ) -> None:
        source = Image.new("RGB", (128, 64), (112, 126, 104))
        profile = ClassificationProfile(
            source_file_name="shared-vertex.png",
            source_width=128,
            source_height=64,
            samples=(),
            regions=(
                ClassificationRegion(
                    ((2, 2), (64, 2), (64, 61), (2, 61)),
                    "upland",
                    "grass",
                    100,
                    (100, 100, 100, 100),
                ),
                ClassificationRegion(
                    ((64, 2), (125, 2), (125, 61), (64, 61)),
                    "rocks",
                    "none",
                    900,
                    (900, 900, 900, 900),
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
            self.assertEqual(int(converted.elevation[2, 64]), 500)
            cardinal = max(
                int(np.max(np.abs(np.diff(converted.elevation, axis=0)))),
                int(np.max(np.abs(np.diff(converted.elevation, axis=1)))),
            )
            self.assertLessEqual(cardinal, 364)
        finally:
            converted.preview.close()
            source.close()

    def test_clustering_composition_can_select_one_safe_living_biome(
        self,
    ) -> None:
        source = Image.new("RGB", (64, 64), (104, 134, 86))
        profile = ClusteringProfile(
            source_file_name="composition.png",
            source_width=64,
            source_height=64,
            clusters=4,
            min_land_region=0,
            allowed_surfaces=("plain",),
            allowed_biotopes=("birch",),
        )
        converted = convert(
            source,
            width=1,
            height=1,
            clustering_profile=profile,
        )
        try:
            selected = {
                SAFE_TILES_TUPLE[int(index)]
                for index in np.unique(np.asarray(converted.preview))
            }
            self.assertEqual(selected, {"soil_low:birch_low"})
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
        boundary = ((64, 20), (108, 64), (64, 108), (20, 64))
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
            extent = classification_processing_extent(noisy_profile)
            working_profile = crop_classification_profile(
                noisy_profile,
                extent,
            )
            mask = rasterize_map_boundary(working_profile, 128, 128)
            self.assertIsNotNone(mask)
            self.assertTrue(np.array_equal(noisy_tiles, clean_tiles))
            self.assertTrue(
                np.array_equal(noisy.elevation, clean.elevation)
            )
            deep_ocean = SAFE_TILES_TUPLE.index("deep_ocean")
            self.assertTrue(np.all(noisy_tiles[~mask] == deep_ocean))
            self.assertGreater(
                int(np.ptp(noisy.elevation[~mask])),
                0,
            )
            self.assertTrue(np.all(noisy.elevation[~mask] <= -151))
            left, top, right, bottom = extent
            plain_position = (
                int(((36 - left + 0.5) / (right - left)) * 128),
                int(((64 - top + 0.5) / (bottom - top)) * 128),
            )
            rock_position = (
                int(((92 - left + 0.5) / (right - left)) * 128),
                int(((64 - top + 0.5) / (bottom - top)) * 128),
            )
            self.assertEqual(
                SAFE_TILES_TUPLE[noisy.preview.getpixel(plain_position)],
                "soil_low:grass_low",
            )
            self.assertEqual(
                SAFE_TILES_TUPLE[noisy.preview.getpixel(rock_position)],
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
            map_boundary=((64, 20), (108, 64), (64, 108), (20, 64)),
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
            extent = classification_processing_extent(profile)
            working_profile = crop_classification_profile(profile, extent)
            mask = rasterize_map_boundary(working_profile, 128, 128)
            tiles = np.asarray(converted.preview)
            sand = SAFE_TILES_TUPLE.index("sand")
            self.assertTrue(np.all(tiles[~mask] == sand))
            outside = converted.elevation[~mask]
            self.assertGreater(int(np.ptp(outside)), 0)
            self.assertLess(abs(float(np.median(outside)) - 7.0), 600.0)
        finally:
            converted.preview.close()
            source.close()

    def test_auto_outside_continues_nearest_class_and_dem(self) -> None:
        source = Image.new("RGB", (128, 96), (112, 126, 104))
        profile = ClassificationProfile(
            source_file_name="auto-outside.png",
            source_width=128,
            source_height=96,
            samples=(
                ClassificationSample(34, 48, "plain", "grass", 200),
                ClassificationSample(92, 48, "rocks", "none", 3200),
            ),
            map_boundary=((18, 48), (50, 12), (110, 48), (50, 84)),
            outside_surface="auto",
            outside_biotope="none",
            outside_elevation=-4000,
        )
        converted = convert(
            source,
            width=2,
            height=1,
            classification_profile=profile,
        )
        try:
            extent = classification_processing_extent(profile)
            working = crop_classification_profile(profile, extent)
            mask = rasterize_map_boundary(working, 128, 64)
            tiles = np.asarray(converted.preview)
            inside_tiles = np.unique(tiles[mask])
            self.assertTrue(np.all(np.isin(tiles[~mask], inside_tiles)))
            outside_dem = converted.elevation[~mask]
            self.assertGreater(int(np.ptp(outside_dem)), 0)
            self.assertGreater(float(np.median(outside_dem)), -1000.0)
            self.assertLess(
                int(np.max(np.abs(np.diff(converted.elevation, axis=1)))),
                9001,
            )
        finally:
            converted.preview.close()
            source.close()

    def test_surface_slope_caps_keep_steep_angles_in_rare_tails(self) -> None:
        surfaces = np.full(
            (192, 256),
            SURFACE_IDS.index("plain"),
            dtype=np.int8,
        )
        surfaces[:, 64:128] = SURFACE_IDS.index("hills")
        surfaces[:, 128:192] = SURFACE_IDS.index("rocks")
        surfaces[:, 192:] = SURFACE_IDS.index("summit")
        caps = _surface_slope_caps(surfaces, 20260719)
        degrees = np.degrees(np.arctan(caps / 1000.0))

        plain = degrees[:, :64]
        hills = degrees[:, 64:128]
        rocks = degrees[:, 128:192]
        summits = degrees[:, 192:]
        self.assertLessEqual(float(np.max(plain)), 20.01)
        self.assertLessEqual(float(np.max(hills)), 50.01)
        self.assertLess(float(np.median(hills)), 24.0)
        self.assertLess(float(np.median(rocks)), 26.0)
        self.assertLess(float(np.median(summits)), 25.0)
        self.assertGreater(float(np.max(rocks)), 55.0)
        self.assertGreater(float(np.max(summits)), 70.0)

    def test_manual_polygon_profile_round_trips_and_rejects_bow_tie(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            profile_path = Path(temporary_directory) / "polygon.json"
            profile = ClassificationProfile(
                source_file_name="polygon.png",
                source_width=64,
                source_height=64,
                samples=(),
                long_side_blocks=19,
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
            self.assertEqual(profile.to_json_dict()["schema_version"], 3)
            self.assertEqual(restored.long_side_blocks, 19)
            self.assertEqual(restored.regions, profile.regions)
            self.assertEqual(restored.lines, profile.lines)
            self.assertEqual(restored.map_boundary, profile.map_boundary)
            self.assertEqual(restored.outside_surface, "sand")
            self.assertEqual(restored.outside_elevation, 4)

            legacy_payload = profile.to_json_dict()
            legacy_payload["schema_version"] = 2
            legacy_payload["settings"].pop("long_side_blocks")
            profile_path.write_text(
                json.dumps(legacy_payload),
                encoding="utf-8",
            )
            migrated = load_classification_profile(
                profile_path,
                (64, 64),
            )
            self.assertEqual(migrated.long_side_blocks, 20)

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

    def test_source_georeference_survives_resize_and_geotiff_round_trip(
        self,
    ) -> None:
        source_reference = RasterGeoreference(
            source_file_name="rotated-source.tif",
            source_width=128,
            source_height=64,
            raster_width=128,
            raster_height=64,
            source_raster_to_crs=(30.0, 0.01, 0.002, 60.0, 0.001, -0.01),
            raster_to_crs=(30.0, 0.01, 0.002, 60.0, 0.001, -0.01),
            crs_wkt=(
                'GEOGCS["WGS 84",DATUM["WGS_1984",'
                'SPHEROID["WGS 84",6378137,298.257223563]],'
                'PRIMEM["Greenwich",0],'
                'UNIT["degree",0.0174532925199433],'
                'AUTHORITY["EPSG","4326"]]'
            ),
            crs_projjson='{"type":"GeographicCRS","name":"WGS 84"}',
            epsg=4326,
            crs_kind="geographic",
            pixel_interpretation="area",
            geo_key_directory=(
                1,
                1,
                0,
                3,
                1024,
                0,
                1,
                2,
                1025,
                0,
                1,
                1,
                2048,
                0,
                1,
                4326,
            ),
        )
        reference = source_reference.resampled(64, 64)
        self.assertEqual(
            reference.raster_to_crs,
            (30.0, 0.02, 0.002, 60.0, 0.002, -0.01),
        )
        self.assertEqual(len(reference.wgs84_control_points), 25)
        self.assertTrue(
            np.allclose(
                apply_transform(reference.worldbox_cell_to_crs, 0.0, 0.0),
                apply_transform(reference.raster_to_crs, 0.0, 64.0),
            )
        )
        source_xy = apply_transform(
            reference.worldbox_cell_to_crs,
            17.5,
            23.25,
        )
        self.assertTrue(
            np.allclose(
                apply_transform(
                    reference.crs_to_worldbox_cell,
                    *source_xy,
                ),
                (17.5, 23.25),
            )
        )

        source = Image.new("RGB", (64, 64), (90, 140, 80))
        profile = ClassificationProfile(
            source_file_name="rotated-source.tif",
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
        converted = replace(converted, georeference=reference)
        try:
            with tempfile.TemporaryDirectory() as temporary_directory:
                destination = Path(temporary_directory) / "save1"
                write_game_save_atomically(
                    output_path=destination,
                    converted_map=converted,
                    name="Georeferenced map",
                )
                dem_path = destination / GENERATED_ELEVATION_FILE_NAME
                self.assertTrue((destination / GEOREFERENCE_FILE_NAME).is_file())
                self.assertTrue(
                    dem_path.with_name(
                        dem_path.stem + ".terrainlab-georef.json"
                    ).is_file()
                )
                self.assertTrue(dem_path.with_suffix(".tfw").is_file())
                self.assertTrue(dem_path.with_suffix(".prj").is_file())
                with Image.open(dem_path) as dem:
                    self.assertIn(34264, dem.tag_v2)
                    self.assertNotIn(33550, dem.tag_v2)
                    self.assertNotIn(33922, dem.tag_v2)

                restored = read_raster_georeference(
                    dem_path,
                    expected_size=(64, 64),
                )
                self.assertIsNotNone(restored)
                self.assertEqual(restored.epsg, 4326)
                self.assertEqual(restored.pixel_interpretation, "area")
                self.assertTrue(
                    np.allclose(
                        restored.raster_to_crs,
                        reference.raster_to_crs,
                    )
                )

                point_reference = replace(
                    reference,
                    pixel_interpretation="point",
                    geo_key_directory=(
                        1,
                        1,
                        0,
                        3,
                        1024,
                        0,
                        1,
                        2,
                        1025,
                        0,
                        1,
                        2,
                        2048,
                        0,
                        1,
                        4326,
                    ),
                )
                point_path = destination / "pixel-point.tif"
                write_int16_geotiff(
                    point_path,
                    converted.elevation,
                    point_reference,
                )
                restored_point = read_raster_georeference(
                    point_path,
                    expected_size=(64, 64),
                )
                self.assertIsNotNone(restored_point)
                self.assertEqual(
                    restored_point.pixel_interpretation,
                    "point",
                )
                self.assertTrue(
                    np.allclose(
                        restored_point.raster_to_crs,
                        point_reference.raster_to_crs,
                    )
                )
        finally:
            converted.preview.close()
            source.close()

    def test_process_image_carries_source_georeference_into_map_output(
        self,
    ) -> None:
        source_reference = RasterGeoreference(
            source_file_name="source.tif",
            source_width=128,
            source_height=64,
            raster_width=128,
            raster_height=64,
            source_raster_to_crs=(500000.0, 20.0, 0.0, 6200000.0, 0.0, -20.0),
            raster_to_crs=(500000.0, 20.0, 0.0, 6200000.0, 0.0, -20.0),
            crs_wkt=(
                'PROJCS["WGS 84 / UTM zone 37N",'
                'GEOGCS["WGS 84",DATUM["WGS_1984",'
                'SPHEROID["WGS 84",6378137,298.257223563]],'
                'PRIMEM["Greenwich",0],'
                'UNIT["degree",0.0174532925199433]],'
                'PROJECTION["Transverse_Mercator"],'
                'PARAMETER["latitude_of_origin",0],'
                'PARAMETER["central_meridian",39],'
                'PARAMETER["scale_factor",0.9996],'
                'PARAMETER["false_easting",500000],'
                'PARAMETER["false_northing",0],'
                'UNIT["metre",1],AUTHORITY["EPSG","32637"]]'
            ),
            epsg=32637,
            crs_kind="projected",
            pixel_interpretation="area",
            geo_key_directory=(
                1,
                1,
                0,
                3,
                1024,
                0,
                1,
                1,
                1025,
                0,
                1,
                1,
                3072,
                0,
                1,
                32637,
            ),
        )
        profile = ClassificationProfile(
            source_file_name="source.tif",
            source_width=128,
            source_height=64,
            samples=(
                ClassificationSample(4, 4, "deep_ocean", "auto", -4000),
                ClassificationSample(123, 59, "summit", "none", 5000),
            ),
        )
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            source_path = root / "source.tif"
            profile_path = root / "source-profile.json"
            write_int16_geotiff(
                source_path,
                np.arange(128 * 64, dtype=np.int16).reshape((64, 128)),
                source_reference,
            )
            profile_path.write_text(
                json.dumps(profile.to_json_dict()),
                encoding="utf-8",
            )
            output_root = root / "output"
            process_image(
                source_path,
                {
                    "map_name": None,
                    "output": output_root,
                    "save_to_game": False,
                    "game_saves_dir": None,
                    "classification_profile": profile_path,
                    "no_classification_profile": False,
                    "clustering_profile": None,
                    "no_clustering_profile": True,
                    "width": 1,
                    "height": 1,
                    "fit_budget": False,
                    "dither": False,
                    "algorithm": "terrain",
                    "terrain_clusters": 8,
                    "terrain_smooth": 0,
                    "terrain_min_region": 0,
                },
                SAFE_TILES_TUPLE,
            )
            map_directory = output_root / "source"
            metadata = json.loads(
                (map_directory / GEOREFERENCE_FILE_NAME).read_text(
                    encoding="utf-8"
                )
            )
            self.assertEqual(metadata["epsg"], 32637)
            self.assertEqual(
                metadata["source_raster_to_crs"],
                [500000.0, 20.0, 0.0, 6200000.0, 0.0, -20.0],
            )
            self.assertEqual(
                metadata["raster_to_crs"],
                [500000.0, 40.0, 0.0, 6200000.0, 0.0, -20.0],
            )
            restored = read_raster_georeference(
                map_directory / GENERATED_ELEVATION_FILE_NAME
            )
            self.assertIsNotNone(restored)
            self.assertEqual(restored.epsg, 32637)
            self.assertTrue(
                np.allclose(
                    restored.raster_to_crs,
                    metadata["raster_to_crs"],
                )
            )

    def test_published_extent_updates_aspect_and_georeference(self) -> None:
        profile = ClassificationProfile(
            source_file_name="extent.tif",
            source_width=128,
            source_height=128,
            samples=(
                ClassificationSample(24, 20, "plain", "grass", 100),
                ClassificationSample(94, 40, "rocks", "none", 3000),
            ),
            long_side_blocks=20,
            map_boundary=((20, 10), (99, 10), (99, 49), (20, 49)),
        )
        extent = classification_processing_extent(profile)
        self.assertEqual(extent, (20, 10, 100, 50))
        self.assertEqual(
            fit_map_size_to_long_side(
                extent[2] - extent[0],
                extent[3] - extent[1],
                profile.long_side_blocks,
            ),
            (20, 10),
        )

        reference = RasterGeoreference(
            source_file_name="extent.tif",
            source_width=128,
            source_height=128,
            raster_width=128,
            raster_height=128,
            source_raster_to_crs=(
                500000.0,
                10.0,
                0.0,
                6200000.0,
                0.0,
                -10.0,
            ),
            raster_to_crs=(
                500000.0,
                10.0,
                0.0,
                6200000.0,
                0.0,
                -10.0,
            ),
            epsg=32637,
            crs_kind="projected",
        )
        cropped = reference.cropped(*extent)
        self.assertEqual(
            (cropped.source_width, cropped.source_height),
            (128, 128),
        )
        self.assertEqual(
            (cropped.raster_width, cropped.raster_height),
            (80, 40),
        )
        self.assertEqual(
            cropped.raster_to_crs,
            (500200.0, 10.0, 0.0, 6199900.0, 0.0, -10.0),
        )
        output = cropped.resampled(1280, 640)
        self.assertTrue(
            np.allclose(
                output.raster_to_crs,
                (500200.0, 0.625, 0.0, 6199900.0, 0.0, -0.625),
            )
        )

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
