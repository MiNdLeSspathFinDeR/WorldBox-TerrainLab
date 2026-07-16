using System;
using System.Collections.Generic;
using System.IO;
using NeoModLoader.api;
using NeoModLoader.General;
using NeoModLoader.General.UI.Prefabs;
using UnityEngine;
using UnityEngine.UI;

namespace TerrainLab
{
    public sealed class TerrainLabUi : MonoBehaviour
    {
        private sealed class ToolbarLayoutButton
        {
            public SimpleButton Button;
            public RectTransform Rect;
            public Image ActivityLamp;
            public ToolbarSection Section;
            public ToolbarButtonRole Role;
            public int Group;
            public int Row;
            public float CenterX;
        }

        private sealed class ToolbarLayoutSeparator
        {
            public RectTransform Rect;
            public Image Marker;
            public ToolbarSection Section;
            public int StartsGroup;
        }

        private enum WorkspaceLayer
        {
            Off,
            Toolbar,
            Settings
        }

        private enum PendingOverlayKind
        {
            None,
            Relief,
            Hydrology,
            Erosion
        }

        private enum ToolbarSection
        {
            Primary,
            Project,
            Terrain,
            Digitizing,
            Analysis,
            Layers
        }

        private enum ToolbarButtonRole
        {
            Critical,
            Section,
            Functional
        }

        private const string WindowId = "terrain_lab_window";
        private const string WindowTitleKey = "terrain_lab_window_title";
        private const string SideButtonKey = "terrain_lab_side_button";
        private const string IconResourceRoot = "terrainlab/icons/";

        private static readonly Color NeutralText = new Color(0.83f, 0.79f, 0.66f, 1f);
        private static readonly Color SuccessText = new Color(0.55f, 0.82f, 0.55f, 1f);
        private static readonly Color WarningText = new Color(0.95f, 0.72f, 0.3f, 1f);
        private static readonly Color ErrorText = new Color(0.95f, 0.52f, 0.46f, 1f);
        private static readonly Color InactiveButton = new Color(0.68f, 0.68f, 0.68f, 1f);
        private static readonly Color ToolbarBackground =
            new Color(0.15f, 0.12f, 0.09f, 0.96f);
        private static readonly Color ActivityGreen =
            new Color(0.24f, 1f, 0.3f, 1f);
        private static readonly Color ActivityAmber =
            new Color(1f, 0.72f, 0.18f, 1f);
        private static readonly Color CriticalOutline =
            new Color(1f, 0.27f, 0.2f, 1f);
        private static readonly Color SectionOutline =
            new Color(0.5f, 0.52f, 0.48f, 0.95f);
        private static readonly Color[] ToolbarGroupColors =
        {
            new Color(0.88f, 0.68f, 0.27f, 0.95f),
            new Color(0.31f, 0.65f, 0.86f, 0.95f),
            new Color(0.28f, 0.75f, 0.64f, 0.95f),
            new Color(0.86f, 0.61f, 0.24f, 0.95f),
            new Color(0.39f, 0.72f, 0.36f, 0.95f),
            new Color(0.56f, 0.64f, 0.74f, 0.95f),
            new Color(0.84f, 0.42f, 0.32f, 0.95f),
            new Color(0.72f, 0.79f, 0.35f, 0.95f),
            new Color(0.29f, 0.69f, 0.78f, 0.95f),
            new Color(0.77f, 0.48f, 0.27f, 0.95f),
            new Color(0.6f, 0.6f, 0.58f, 0.95f)
        };

        private readonly Dictionary<string, SimpleButton> _moduleButtons =
            new Dictionary<string, SimpleButton>();
        private readonly Dictionary<TerrainEditorTool, SimpleButton> _editorToolButtons =
            new Dictionary<TerrainEditorTool, SimpleButton>();
        private readonly Dictionary<TerrainEditorTool, SimpleButton> _toolbarEditorButtons =
            new Dictionary<TerrainEditorTool, SimpleButton>();
        private readonly Dictionary<string, SimpleButton> _toolbarOverlayButtons =
            new Dictionary<string, SimpleButton>();
        private readonly Dictionary<string, SimpleButton> _toolbarCommandButtons =
            new Dictionary<string, SimpleButton>();
        private readonly Dictionary<string, Sprite> _iconCache =
            new Dictionary<string, Sprite>();
        private readonly HashSet<string> _missingIconIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly List<ToolbarLayoutButton> _toolbarLayoutButtons =
            new List<ToolbarLayoutButton>();
        private readonly List<ToolbarLayoutSeparator> _toolbarLayoutSeparators =
            new List<ToolbarLayoutSeparator>();
        private readonly List<SimpleButton> _toolbarButtons =
            new List<SimpleButton>();
        private readonly Dictionary<SimpleButton, Image> _toolbarActivityLamps =
            new Dictionary<SimpleButton, Image>();
        private readonly Dictionary<ToolbarSection, SimpleButton> _toolbarSectionButtons =
            new Dictionary<ToolbarSection, SimpleButton>();
        private readonly List<Image> _sideStateIndicators = new List<Image>();

        private ScrollWindow _window;
        private SimpleButton _sideButton;
        private TerrainLabEditor _editor;
        private TerrainElevationOverlay _elevationOverlay;
        private TerrainDataOverlay _dataOverlay;
        private TerrainReliefOverlay _reliefOverlay;
        private TerrainHydrologyOverlay _hydrologyOverlay;
        private TerrainErosionOverlay _erosionOverlay;
        private Transform _moduleContent;
        private Text _statusText;
        private Text _brushRadiusText;
        private Text _reliefProgressText;
        private Text _hydrologyProgressText;
        private Text _erosionProgressText;
        private GameObject _topToolbar;
        private RectTransform _toolbarContent;
        private Image _toolbarBackground;
        private Image _toolbarLeftRail;
        private Image _toolbarRightRail;
        private SimpleButton _reliefJobButton;
        private SimpleButton _hydrologyJobButton;
        private SimpleButton _waterDynamicsButton;
        private SimpleButton _erosionJobButton;
        private SimpleButton _applyErosionButton;
        private GameObject _mapStatusBar;
        private Text _mapStatusText;
        private string _lastMapStatus = string.Empty;
        private string _selectedModule = "project";
        private string _selectedLayer = "elevation";
        private int _hydrologyThreshold;
        private string _hydrologyParameterProjectId;
        private int _erosionIterations = 8;
        private int _erosionFlowStrength = 35;
        private int _erosionThermalStrength = 15;
        private int _erosionTalusThreshold = 4;
        private string _erosionParameterProjectId;
        private int _lastReliefProgress = -1;
        private int _lastHydrologyProgress = -1;
        private int _lastErosionProgress = -1;
        private string _runningJobId;
        private bool _workspaceVisible;
        private bool _jobsRunningLastFrame;
        private bool _bottomToolbarStyleApplied;
        private float _lastToolbarCanvasWidth = -1f;
        private int _toolbarLayoutGroup;
        private ToolbarSection _activeToolbarSection = ToolbarSection.Primary;
        private ToolbarSection _toolbarCreationSection = ToolbarSection.Primary;
        private ToolbarButtonRole _toolbarCreationRole = ToolbarButtonRole.Functional;
        private Sprite _criticalButtonSprite;
        private Texture2D _indicatorTexture;
        private Sprite _indicatorSprite;
        private Sprite _indicatorOffSprite;
        private Vector2 _indicatorDisplaySize = new Vector2(5f, 5f);
        private bool _usesGameIndicatorSprites;
        private bool _advanceWorkspaceAfterWindowClose;
        private bool _suppressWindowHideAdvance;
        private PendingOverlayKind _pendingOverlayKind;
        private TerrainReliefOverlayMode _pendingReliefOverlay;
        private TerrainHydrologyOverlayMode _pendingHydrologyOverlay;
        private TerrainErosionOverlayMode _pendingErosionOverlay;
        private string _pendingOverlayJobId;
        private bool _initialized;

        public void Initialize(ModDeclare declaration, TerrainLabEditor editor)
        {
            if (_initialized)
            {
                return;
            }

            _editor = editor ?? throw new ArgumentNullException(nameof(editor));
            _elevationOverlay = GetComponent<TerrainElevationOverlay>();
            if (_elevationOverlay == null)
            {
                _elevationOverlay = gameObject.AddComponent<TerrainElevationOverlay>();
            }

            _dataOverlay = GetComponent<TerrainDataOverlay>();
            if (_dataOverlay == null)
            {
                _dataOverlay = gameObject.AddComponent<TerrainDataOverlay>();
            }

            _reliefOverlay = GetComponent<TerrainReliefOverlay>();
            if (_reliefOverlay == null)
            {
                _reliefOverlay = gameObject.AddComponent<TerrainReliefOverlay>();
            }

            _hydrologyOverlay = GetComponent<TerrainHydrologyOverlay>();
            if (_hydrologyOverlay == null)
            {
                _hydrologyOverlay = gameObject.AddComponent<TerrainHydrologyOverlay>();
            }

            _erosionOverlay = GetComponent<TerrainErosionOverlay>();
            if (_erosionOverlay == null)
            {
                _erosionOverlay = gameObject.AddComponent<TerrainErosionOverlay>();
            }

            CreateWindow();
            ScrollWindow.addCallbackHide(HandleWindowHidden);
            CreateTopToolbar();
            CreateSideButton(declaration.GetIcon());
            CreateMapStatusBar();
            _editor.EditApplied += HandleElevationEditApplied;
            _editor.ElevationChanged += HandleElevationChanged;
            _editor.SurfaceSampled += HandleSurfaceSampled;
            _editor.OperationFailed += HandleEditorOperationFailed;
            _editor.RegionPolygonized += HandleRegionPolygonized;
            _editor.RampStarted += HandleRampStarted;

            if (TerrainLabRuntime.Instance != null)
            {
                TerrainLabRuntime.Instance.StateChanged += HandleRuntimeStateChanged;
                TerrainLabRuntime.Instance.WaterDynamics.CellsChanged +=
                    HandleWaterCellsChanged;
                TerrainLabRuntime.Instance.WaterDynamics.ElevationChanged +=
                    HandleWaterElevationChanged;
            }

            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized || _editor == null)
            {
                return;
            }

            if (_advanceWorkspaceAfterWindowClose &&
                (_window == null || !_window.gameObject.activeInHierarchy))
            {
                _advanceWorkspaceAfterWindowClose = false;
                SetWorkspaceVisible(false);
            }

            bool analysisRunning = IsAnalysisRunning();
            _editor.SetInterfaceState(
                _workspaceVisible,
                _workspaceVisible && !analysisRunning);

            TryApplyBottomToolbarStyle();
            UpdateAdaptiveToolbarLayout(false);

            if (_topToolbar != null && _topToolbar.activeSelf != _workspaceVisible)
            {
                _topToolbar.SetActive(_workspaceVisible);
            }

            if (_mapStatusBar != null && _mapStatusBar.activeSelf != _workspaceVisible)
            {
                _mapStatusBar.SetActive(_workspaceVisible);
            }

            UpdateSideButtonState();

            if (_workspaceVisible)
            {
                UpdateMapStatusBar();
                UpdateAnalysisProgress();
                UpdateToolbarState();
            }

            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            if (_elevationOverlay != null && _elevationOverlay.IsVisible &&
                !_elevationOverlay.References(state))
            {
                _elevationOverlay.Clear();
            }

            if (_dataOverlay != null && _dataOverlay.IsVisible &&
                !_dataOverlay.References(state))
            {
                _dataOverlay.Clear();
            }

            if (_reliefOverlay != null &&
                _reliefOverlay.Mode != TerrainReliefOverlayMode.None &&
                (state?.Relief == null || !state.Relief.IsCurrent(state)))
            {
                _reliefOverlay.Clear();
            }

            if (_hydrologyOverlay != null &&
                _hydrologyOverlay.Mode != TerrainHydrologyOverlayMode.None &&
                (state?.Hydrology == null || !state.Hydrology.IsCurrent(state)))
            {
                _hydrologyOverlay.Clear();
            }

            if (_erosionOverlay != null &&
                _erosionOverlay.Mode != TerrainErosionOverlayMode.None &&
                (state?.Erosion == null || !state.Erosion.IsCurrent(state)))
            {
                _erosionOverlay.Clear();
            }
        }

        private void CreateWindow()
        {
            // WindowCreator clones WorldBox's standard "windows/empty" prefab.
            _window = WindowCreator.CreateEmptyWindow(WindowId, WindowTitleKey);
            ConfigureWindowTitle();

            Transform contentTransform = _window.transform.Find(
                "Background/Scroll View/Viewport/Content");
            if (contentTransform == null)
            {
                throw new MissingReferenceException(
                    "WorldBox empty window does not contain its standard content transform.");
            }

            ConfigureContentLayout(contentTransform.gameObject);
            CreateLocalizedLabel(
                contentTransform,
                "terrain_lab_menu_heading",
                18f,
                FontStyle.Bold,
                26f,
                TextAnchor.MiddleCenter,
                Color.white);

            CreateModuleButton(contentTransform, "project", "terrain_lab_module_project");
            CreateModuleButton(contentTransform, "parameters", "terrain_lab_module_parameters");
            CreateModuleButton(contentTransform, "layers", "terrain_lab_module_layers");
            CreateModuleButton(contentTransform, "settings", "terrain_lab_module_settings");

            _moduleContent = CreateVerticalContainer(
                contentTransform,
                "TerrainLabModuleContent",
                5f);

            _statusText = CreateTextLabel(
                contentTransform,
                "TerrainLabStatus",
                LM.Get("terrain_lab_status_ready"),
                11f,
                FontStyle.Normal,
                42f,
                TextAnchor.MiddleCenter,
                NeutralText);

            UpdateModuleSelection();
            RebuildModuleContent();
            _window.gameObject.SetActive(false);
        }

        private void ConfigureWindowTitle()
        {
            Transform titleTransform = _window.transform.Find("Background/Title");
            if (titleTransform == null)
            {
                return;
            }

            LocalizedText title = titleTransform.GetComponent<LocalizedText>();
            if (title != null)
            {
                title.setKeyAndUpdate(WindowTitleKey);
                title.autoField = false;
            }
        }

        private static void ConfigureContentLayout(GameObject content)
        {
            VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
            if (layout == null)
            {
                layout = content.AddComponent<VerticalLayoutGroup>();
            }

            layout.padding = new RectOffset(12, 12, 8, 12);
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
            if (fitter == null)
            {
                fitter = content.AddComponent<ContentSizeFitter>();
            }

            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private void CreateTopToolbar()
        {
            Transform canvas = CanvasMain.instance.canvas_ui.transform;
            _topToolbar = new GameObject(
                "TerrainLabTopToolbar",
                typeof(RectTransform));
            _topToolbar.transform.SetParent(canvas, false);

            RectTransform toolbarRect = _topToolbar.GetComponent<RectTransform>();
            toolbarRect.anchorMin = new Vector2(0.5f, 1f);
            toolbarRect.anchorMax = new Vector2(0.5f, 1f);
            toolbarRect.pivot = new Vector2(0.5f, 1f);
            toolbarRect.anchoredPosition = Vector2.zero;
            toolbarRect.sizeDelta = new Vector2(574f, 56f);

            GameObject backgroundObject = new GameObject(
                "TerrainLabToolbarBackground",
                typeof(RectTransform),
                typeof(Image));
            backgroundObject.transform.SetParent(_topToolbar.transform, false);
            RectTransform backgroundRect =
                backgroundObject.GetComponent<RectTransform>();
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.pivot = new Vector2(0.5f, 0.5f);
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;
            backgroundRect.localScale = new Vector3(1f, -1f, 1f);

            _toolbarBackground = backgroundObject.GetComponent<Image>();
            _toolbarBackground.sprite = SpriteTextureLoader.getSprite(
                "ui/special/darkInputFieldEmpty");
            _toolbarBackground.type = Image.Type.Sliced;
            _toolbarBackground.color = ToolbarBackground;
            _toolbarBackground.raycastTarget = true;

            _toolbarLeftRail = CreateToolbarSideRail(false);
            _toolbarRightRail = CreateToolbarSideRail(true);

            GameObject content = new GameObject(
                "TerrainLabAdaptiveToolbarContent",
                typeof(RectTransform));
            content.transform.SetParent(_topToolbar.transform, false);
            _toolbarContent = content.GetComponent<RectTransform>();
            _toolbarContent.anchorMin = new Vector2(0f, 1f);
            _toolbarContent.anchorMax = new Vector2(1f, 1f);
            _toolbarContent.pivot = new Vector2(0.5f, 1f);
            _toolbarContent.anchoredPosition = Vector2.zero;
            _toolbarContent.sizeDelta = Vector2.zero;

            CreateToolbarPrimaryRow();
            CreateProjectToolbarRow();
            CreateEditingToolbarRow();
            CreateOverlayToolbarRow();

            UpdateToolbarSectionVisibility();
            UpdateAdaptiveToolbarLayout(true);
            TryApplyBottomToolbarStyle();
            _topToolbar.transform.SetAsLastSibling();
            _topToolbar.SetActive(false);
        }

        private Image CreateToolbarSideRail(bool right)
        {
            string side = right ? "Right" : "Left";
            GameObject maskObject = new GameObject(
                "TerrainLabToolbar" + side + "RailMask",
                typeof(RectTransform),
                typeof(RectMask2D));
            maskObject.transform.SetParent(_topToolbar.transform, false);
            RectTransform maskRect = maskObject.GetComponent<RectTransform>();
            float anchorX = right ? 1f : 0f;
            maskRect.anchorMin = new Vector2(anchorX, 0f);
            maskRect.anchorMax = new Vector2(anchorX, 1f);
            maskRect.pivot = new Vector2(anchorX, 0.5f);
            maskRect.anchoredPosition = Vector2.zero;
            maskRect.sizeDelta = new Vector2(16f, 0f);

            GameObject railObject = new GameObject(
                "TerrainLabToolbar" + side + "Rail",
                typeof(RectTransform),
                typeof(Image));
            railObject.transform.SetParent(maskObject.transform, false);
            RectTransform railRect = railObject.GetComponent<RectTransform>();
            railRect.anchorMin = new Vector2(anchorX, 0f);
            railRect.anchorMax = new Vector2(anchorX, 1f);
            railRect.pivot = new Vector2(anchorX, 0.5f);
            railRect.anchoredPosition = Vector2.zero;
            railRect.sizeDelta = new Vector2(574f, 0f);

            Image rail = railObject.GetComponent<Image>();
            rail.sprite = _toolbarBackground.sprite;
            rail.type = _toolbarBackground.type;
            rail.color = _toolbarBackground.color;
            rail.raycastTarget = false;
            return rail;
        }

        private void CreateToolbarPrimaryRow()
        {
            Transform row = _toolbarContent;
            _toolbarCreationSection = ToolbarSection.Primary;
            _toolbarCreationRole = ToolbarButtonRole.Critical;
            _toolbarCommandButtons["menu"] = CreateToolbarButton(
                row,
                "menu",
                "module_manager",
                null,
                ShowWindow,
                "terrain_lab_toolbar_menu",
                "terrain_lab_toolbar_menu_description");
            _toolbarCommandButtons["save"] = CreateToolbarButton(
                row,
                "save",
                "project_save",
                null,
                SaveProject,
                "terrain_lab_action_save",
                "terrain_lab_action_save_description");

            CreateToolbarSeparator(row);
            _toolbarCreationRole = ToolbarButtonRole.Section;
            CreateToolbarSectionButton(
                row,
                ToolbarSection.Project,
                "project_open",
                null,
                "terrain_lab_module_project");
            CreateToolbarSectionButton(
                row,
                ToolbarSection.Terrain,
                "elevation_raise",
                "Z",
                "terrain_lab_section_terrain");
            CreateToolbarSectionButton(
                row,
                ToolbarSection.Digitizing,
                "layer_add_vector",
                "D",
                "terrain_lab_section_digitizing");
            CreateToolbarSectionButton(
                row,
                ToolbarSection.Analysis,
                "ui/Icons/iconPlay",
                "A",
                "terrain_lab_section_analysis");
            CreateToolbarSectionButton(
                row,
                ToolbarSection.Layers,
                "layer_stack",
                "L",
                "terrain_lab_module_layers");
        }

        private void CreateToolbarSectionButton(
            Transform parent,
            ToolbarSection section,
            string iconId,
            string badge,
            string tooltipNameKey)
        {
            SimpleButton button = CreateToolbarButton(
                parent,
                "section_" + section.ToString().ToLowerInvariant(),
                iconId,
                badge,
                delegate { SelectToolbarSection(section); },
                tooltipNameKey,
                GetToolbarSectionDescriptionLocalizationKey(section));
            _toolbarSectionButtons[section] = button;
        }

        private void CreateProjectToolbarRow()
        {
            Transform row = _toolbarContent;
            _toolbarCreationSection = ToolbarSection.Project;
            _toolbarCreationRole = ToolbarButtonRole.Functional;
            _toolbarLayoutGroup++;
            _toolbarCommandButtons["export"] = CreateToolbarButton(
                row,
                "export",
                "export_wbxgeo",
                null,
                ExportProject,
                "terrain_lab_action_export",
                "terrain_lab_action_export_description");
            _toolbarCommandButtons["validate"] = CreateToolbarButton(
                row,
                "validate",
                "project_validate",
                null,
                ValidateProject,
                "terrain_lab_action_validate",
                "terrain_lab_action_validate_description");
            _toolbarCommandButtons["export_gis"] = CreateToolbarButton(
                row,
                "export_gis",
                "export_geotiff",
                null,
                ExportGisLayers,
                "terrain_lab_action_export_gis",
                "terrain_lab_action_export_gis_description");
            CreateToolbarSeparator(row);
            _toolbarCommandButtons["sync_prepare"] = CreateToolbarButton(
                row,
                "sync_prepare",
                "sync_qgis",
                null,
                PrepareFileSync,
                "terrain_lab_action_prepare_sync",
                "terrain_lab_action_prepare_sync_description");
            _toolbarCommandButtons["sync_pull"] = CreateToolbarButton(
                row,
                "sync_pull",
                "sync_qgis",
                "P",
                delegate { PullFileSync(TerrainSyncConflictPolicy.Reject); },
                "terrain_lab_action_pull_sync",
                "terrain_lab_action_pull_sync_description");
            _toolbarCommandButtons["sync_branch"] = CreateToolbarButton(
                row,
                "sync_branch",
                "sync_qgis",
                "B",
                delegate
                {
                    PullFileSync(TerrainSyncConflictPolicy.BranchAndApplyIncoming);
                },
                "terrain_lab_action_pull_branch",
                "terrain_lab_action_pull_branch_description");

        }

        private void CreateEditingToolbarRow()
        {
            Transform row = _toolbarContent;
            _toolbarCreationSection = ToolbarSection.Terrain;
            _toolbarCreationRole = ToolbarButtonRole.Functional;
            _toolbarLayoutGroup++;
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.Inspect,
                "identify",
                null,
                "terrain_lab_tool_inspect");
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.SetElevation,
                null,
                "Z",
                "terrain_lab_tool_set");
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.RaiseElevation,
                "elevation_raise",
                null,
                "terrain_lab_tool_raise");
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.LowerElevation,
                "elevation_raise",
                null,
                "terrain_lab_tool_lower",
                180f);
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.SmoothElevation,
                null,
                "~",
                "terrain_lab_tool_smooth");
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.RampElevation,
                "layer_add_vector",
                "R",
                "terrain_lab_tool_ramp");
            CreateToolbarSeparator(row);
            _toolbarCommandButtons["undo"] = CreateToolbarButton(
                row,
                "undo",
                "undo",
                null,
                UndoElevationEdit,
                "terrain_lab_action_undo",
                "terrain_lab_action_undo_description");
            _toolbarCommandButtons["redo"] = CreateToolbarButton(
                row,
                "redo",
                "redo",
                null,
                RedoElevationEdit,
                "terrain_lab_action_redo",
                "terrain_lab_action_redo_description");

            _toolbarCreationSection = ToolbarSection.Digitizing;
            _toolbarLayoutGroup++;
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.SampleSurface,
                "identify",
                "P",
                "terrain_lab_tool_surface_sample");
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.FloodFillSurface,
                "ui/Icons/iconBucket",
                null,
                "terrain_lab_tool_surface_fill");
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.DrawLineSurface,
                "layer_add_vector",
                "L",
                "terrain_lab_tool_surface_line");
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.DrawPolygonSurface,
                "layer_add_vector",
                "P",
                "terrain_lab_tool_surface_polygon");
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.DrawRectangleSurface,
                "layer_add_raster",
                "R",
                "terrain_lab_tool_surface_rectangle");
            CreateToolbarEditorButton(
                row,
                TerrainEditorTool.PolygonizeSurface,
                "layer_group",
                "P",
                "terrain_lab_tool_polygonize");
            _toolbarCommandButtons["selection_apply"] = CreateToolbarButton(
                row,
                "selection_apply",
                "project_validate",
                "S",
                ApplyPolygonizedSelection,
                "terrain_lab_action_apply_selection",
                "terrain_lab_action_apply_selection_description");
            _toolbarCommandButtons["digitizing_cancel"] = CreateToolbarButton(
                row,
                "digitizing_cancel",
                "visibility_off",
                "X",
                CancelDigitizing,
                "terrain_lab_action_cancel_digitizing",
                "terrain_lab_action_cancel_digitizing_description");

            _toolbarCreationSection = ToolbarSection.Analysis;
            _toolbarLayoutGroup++;
            _reliefJobButton = CreateToolbarButton(
                row,
                "relief_job",
                "ui/Icons/iconPlay",
                "R",
                ToggleReliefAnalysis,
                "terrain_lab_toolbar_relief_job",
                "terrain_lab_toolbar_relief_job_description");
            _hydrologyJobButton = CreateToolbarButton(
                row,
                "hydrology_job",
                "ui/Icons/iconPlay",
                "H",
                ToggleHydrologyAnalysis,
                "terrain_lab_toolbar_hydrology_job",
                "terrain_lab_toolbar_hydrology_job_description");
            _waterDynamicsButton = CreateToolbarButton(
                row,
                "water_dynamics",
                "ui/Icons/iconRain",
                "W",
                ToggleWaterDynamics,
                "terrain_lab_toolbar_water_dynamics",
                "terrain_lab_toolbar_water_dynamics_description");
            _erosionJobButton = CreateToolbarButton(
                row,
                "erosion_job",
                "ui/Icons/iconPlay",
                "E",
                ToggleErosionAnalysis,
                "terrain_lab_toolbar_erosion_job",
                "terrain_lab_toolbar_erosion_job_description");
            _applyErosionButton = CreateToolbarButton(
                row,
                "erosion_apply",
                "project_validate",
                "E",
                ApplyErosion,
                "terrain_lab_erosion_apply",
                "terrain_lab_erosion_apply_description");
        }

        private void CreateOverlayToolbarRow()
        {
            Transform row = _toolbarContent;
            _toolbarCreationSection = ToolbarSection.Layers;
            _toolbarCreationRole = ToolbarButtonRole.Functional;
            _toolbarLayoutGroup++;
            CreateToolbarOverlayButton(
                row,
                "dem_elevation",
                "layer_add_raster",
                "Z",
                ShowElevationOverlay,
                "terrain_lab_elevation_overlay");
            CreateToolbarOverlayButton(
                row,
                "dem_contours",
                "layer_add_vector",
                "C",
                delegate { ShowDataOverlay(TerrainDataOverlayMode.Contours); },
                "terrain_lab_data_overlay_contours");
            CreateToolbarOverlayButton(
                row,
                "core_landform",
                "layer_group",
                "L",
                delegate { ShowDataOverlay(TerrainDataOverlayMode.Landform); },
                "terrain_lab_data_overlay_landform");
            CreateToolbarOverlayButton(
                row,
                "core_material",
                "layer_style",
                "M",
                delegate { ShowDataOverlay(TerrainDataOverlayMode.Material); },
                "terrain_lab_data_overlay_material");
            CreateToolbarSeparator(row);
            CreateToolbarOverlayButton(
                row,
                "relief_hypsometry",
                "layer_style",
                "Z",
                delegate { ShowReliefOverlay(TerrainReliefOverlayMode.Hypsometry); },
                "terrain_lab_relief_overlay_hypsometry");
            CreateToolbarOverlayButton(
                row,
                "relief_slope",
                "layer_filter",
                "S",
                delegate { ShowReliefOverlay(TerrainReliefOverlayMode.Slope); },
                "terrain_lab_relief_overlay_slope");
            CreateToolbarOverlayButton(
                row,
                "relief_aspect",
                "visibility_on",
                "A",
                delegate { ShowReliefOverlay(TerrainReliefOverlayMode.Aspect); },
                "terrain_lab_relief_overlay_aspect");
            CreateToolbarOverlayButton(
                row,
                "relief_hillshade",
                "visibility_on",
                "L",
                delegate { ShowReliefOverlay(TerrainReliefOverlayMode.Hillshade); },
                "terrain_lab_relief_overlay_hillshade");
            CreateToolbarOverlayButton(
                row,
                "relief_ruggedness",
                "layer_style",
                "R",
                delegate { ShowReliefOverlay(TerrainReliefOverlayMode.Ruggedness); },
                "terrain_lab_relief_overlay_ruggedness");
            CreateToolbarSeparator(row);
            CreateToolbarOverlayButton(
                row,
                "hydrology_filled",
                "layer_add_raster",
                "Z",
                delegate
                {
                    ShowHydrologyOverlay(TerrainHydrologyOverlayMode.FilledElevation);
                },
                "terrain_lab_hydrology_overlay_filled_elevation");
            CreateToolbarOverlayButton(
                row,
                "hydrology_direction",
                "visibility_on",
                "D",
                delegate
                {
                    ShowHydrologyOverlay(TerrainHydrologyOverlayMode.FlowDirection);
                },
                "terrain_lab_hydrology_overlay_flow_direction");
            CreateToolbarOverlayButton(
                row,
                "hydrology_streams",
                "layer_add_vector",
                "R",
                delegate { ShowHydrologyOverlay(TerrainHydrologyOverlayMode.Streams); },
                "terrain_lab_hydrology_overlay_streams");
            CreateToolbarOverlayButton(
                row,
                "hydrology_accumulation",
                "layer_filter",
                "A",
                delegate
                {
                    ShowHydrologyOverlay(TerrainHydrologyOverlayMode.Accumulation);
                },
                "terrain_lab_hydrology_overlay_accumulation");
            CreateToolbarOverlayButton(
                row,
                "hydrology_fill",
                "layer_add_raster",
                "F",
                delegate { ShowHydrologyOverlay(TerrainHydrologyOverlayMode.FillDepth); },
                "terrain_lab_hydrology_overlay_fill");
            CreateToolbarOverlayButton(
                row,
                "hydrology_watersheds",
                "layer_group",
                "W",
                delegate { ShowHydrologyOverlay(TerrainHydrologyOverlayMode.Watersheds); },
                "terrain_lab_hydrology_overlay_watersheds");
            CreateToolbarOverlayButton(
                row,
                "hydrology_order",
                "layer_stack",
                "S",
                delegate
                {
                    ShowHydrologyOverlay(TerrainHydrologyOverlayMode.StreamOrder);
                },
                "terrain_lab_hydrology_overlay_stream_order");
            CreateToolbarSeparator(row);
            CreateToolbarOverlayButton(
                row,
                "water_managed",
                "layer_add_raster",
                "W",
                delegate { ShowDataOverlay(TerrainDataOverlayMode.ManagedWater); },
                "terrain_lab_data_overlay_managed_water");
            CreateToolbarOverlayButton(
                row,
                "water_storage",
                "layer_style",
                "V",
                delegate { ShowDataOverlay(TerrainDataOverlayMode.WaterStorage); },
                "terrain_lab_data_overlay_water_storage");
            CreateToolbarOverlayButton(
                row,
                "water_hydro_feature",
                "layer_add_vector",
                "R",
                delegate { ShowDataOverlay(TerrainDataOverlayMode.HydroFeature); },
                "terrain_lab_data_overlay_hydro_feature");
            CreateToolbarOverlayButton(
                row,
                "water_moisture",
                "layer_add_raster",
                "M",
                delegate { ShowDataOverlay(TerrainDataOverlayMode.Moisture); },
                "terrain_lab_data_overlay_moisture");
            CreateToolbarOverlayButton(
                row,
                "water_erodibility",
                "layer_style",
                "E",
                delegate { ShowDataOverlay(TerrainDataOverlayMode.Erodibility); },
                "terrain_lab_data_overlay_erodibility");
            CreateToolbarOverlayButton(
                row,
                "water_local_slope",
                "layer_filter",
                "S",
                delegate { ShowDataOverlay(TerrainDataOverlayMode.LocalSlope); },
                "terrain_lab_data_overlay_local_slope");
            CreateToolbarOverlayButton(
                row,
                "water_local_aspect",
                "visibility_on",
                "A",
                delegate { ShowDataOverlay(TerrainDataOverlayMode.LocalAspect); },
                "terrain_lab_data_overlay_local_aspect");
            CreateToolbarSeparator(row);
            CreateToolbarOverlayButton(
                row,
                "erosion_net",
                "layer_style",
                "N",
                delegate { ShowErosionOverlay(TerrainErosionOverlayMode.NetChange); },
                "terrain_lab_erosion_overlay_net");
            CreateToolbarOverlayButton(
                row,
                "erosion_cut",
                "elevation_raise",
                "C",
                delegate { ShowErosionOverlay(TerrainErosionOverlayMode.Erosion); },
                "terrain_lab_erosion_overlay_cut",
                180f);
            CreateToolbarOverlayButton(
                row,
                "erosion_fill",
                "elevation_raise",
                "D",
                delegate { ShowErosionOverlay(TerrainErosionOverlayMode.Deposition); },
                "terrain_lab_erosion_overlay_fill");
            CreateToolbarOverlayButton(
                row,
                "erosion_result",
                "layer_properties",
                "Z",
                delegate
                {
                    ShowErosionOverlay(TerrainErosionOverlayMode.ResultElevation);
                },
                "terrain_lab_erosion_overlay_result");
            CreateToolbarSeparator(row);
            CreateToolbarButton(
                row,
                "overlay_hide",
                "visibility_off",
                null,
                HideAllOverlaysWithStatus,
                "terrain_lab_toolbar_hide_overlays",
                "terrain_lab_toolbar_hide_overlays_description");
        }

        private void CreateSideButton(Sprite icon)
        {
            Transform canvas = CanvasMain.instance.canvas_ui.transform;
            _sideButton = SimpleButton.Instantiate(canvas, false, SideButtonKey);
            _sideButton.Setup(
                ToggleWorkspace,
                icon,
                null,
                new Vector2(52f, 52f),
                "tip",
                new TooltipData
                {
                    tip_name = SideButtonKey,
                    tip_description = SideButtonKey + " Description"
                });

            RectTransform buttonRect = _sideButton.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(1f, 0.5f);
            buttonRect.anchorMax = new Vector2(1f, 0.5f);
            buttonRect.pivot = new Vector2(1f, 0.5f);
            buttonRect.anchoredPosition = new Vector2(-10f, 52f);
            buttonRect.sizeDelta = new Vector2(52f, 52f);

            RectTransform iconRect = _sideButton.Icon.GetComponent<RectTransform>();
            iconRect.sizeDelta = new Vector2(52f, 52f);
            _sideButton.Icon.preserveAspect = true;
            _sideButton.Icon.raycastTarget = false;
            _sideButton.Icon.color = InactiveButton;
            _sideButton.Background.color = Color.clear;
            _sideButton.Background.raycastTarget = true;
            _sideButton.Button.transition = Selectable.Transition.None;
            CreateSideStateIndicators();
            _sideButton.transform.SetAsLastSibling();
        }

        private void CreateMapStatusBar()
        {
            Transform canvas = CanvasMain.instance.canvas_ui.transform;
            _mapStatusBar = new GameObject(
                "TerrainLabMapStatusBar",
                typeof(RectTransform),
                typeof(Image));
            _mapStatusBar.transform.SetParent(canvas, false);

            RectTransform barRect = _mapStatusBar.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0.5f, 0f);
            barRect.anchorMax = new Vector2(0.5f, 0f);
            barRect.pivot = new Vector2(0.5f, 0f);
            barRect.anchoredPosition = new Vector2(0f, 5f);
            barRect.sizeDelta = new Vector2(360f, 18f);

            Image background = _mapStatusBar.GetComponent<Image>();
            background.sprite = SpriteTextureLoader.getSprite("ui/special/darkInputFieldEmpty");
            background.type = Image.Type.Sliced;
            background.color = new Color(0.15f, 0.12f, 0.09f, 0.94f);
            background.raycastTarget = false;

            GameObject textObject = new GameObject(
                "TerrainLabMapStatusText",
                typeof(RectTransform),
                typeof(Text));
            textObject.transform.SetParent(_mapStatusBar.transform, false);
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(7f, 2f);
            textRect.offsetMax = new Vector2(-7f, -2f);

            _mapStatusText = textObject.GetComponent<Text>();
            _mapStatusText.font = LocalizedTextManager.current_font;
            _mapStatusText.fontSize = 9;
            _mapStatusText.resizeTextForBestFit = true;
            _mapStatusText.resizeTextMinSize = 6;
            _mapStatusText.resizeTextMaxSize = 9;
            _mapStatusText.alignment = TextAnchor.MiddleCenter;
            _mapStatusText.color = NeutralText;
            _mapStatusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _mapStatusText.verticalOverflow = VerticalWrapMode.Truncate;
            _mapStatusText.supportRichText = false;
            _mapStatusText.raycastTarget = false;

            _mapStatusBar.transform.SetAsLastSibling();
            _mapStatusBar.SetActive(false);
        }

        private void UpdateMapStatusBar()
        {
            if (_mapStatusText == null || _editor == null)
            {
                return;
            }

            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            long cells = state == null
                ? 0
                : TerrainMapLimits.CountCells(state.Width, state.Height);
            double budgetPercent = cells * 100.0 / TerrainMapLimits.MaximumCellCount;
            string tool = LM.Get(GetEditorToolLocalizationKey(_editor.Tool));
            string status;

            WorldTile tile = _editor.HoveredTile;
            if (state != null && tile != null &&
                state.TryGetElevation(tile.x, tile.y, out short elevation))
            {
                string value = elevation == TerrainElevationEncoding.NoData
                    ? LM.Get("terrain_lab_value_nodata")
                    : elevation.ToString();
                TerrainHydrologyResult hydrology = state.Hydrology;
                if (_elevationOverlay != null && _elevationOverlay.IsVisible)
                {
                    status = string.Format(
                        LM.Get("terrain_lab_elevation_map_status_format"),
                        tile.x,
                        tile.y,
                        value,
                        _elevationOverlay.DisplayMinimum,
                        _elevationOverlay.SeaLevel,
                        _elevationOverlay.DisplayMaximum);
                }
                else if (_dataOverlay != null && _dataOverlay.IsVisible &&
                         _dataOverlay.References(state))
                {
                    status = string.Format(
                        LM.Get("terrain_lab_data_map_status_format"),
                        tile.x,
                        tile.y,
                        value,
                        LM.Get(GetDataOverlayLocalizationKey(_dataOverlay.Mode)),
                        GetDataOverlayCellValue(_dataOverlay.Mode, state, tile));
                }
                else if (_hydrologyOverlay != null &&
                    _hydrologyOverlay.Mode != TerrainHydrologyOverlayMode.None &&
                    hydrology != null && hydrology.IsCurrent(state) &&
                    hydrology.TryGetCell(
                        tile.x,
                        tile.y,
                        out short filled,
                        out TerrainFlowDirection direction,
                        out uint accumulation,
                        out bool isStream))
                {
                    int index = tile.y * state.Width + tile.x;
                    uint watershed = hydrology.Watershed[index];
                    byte streamOrder = hydrology.StreamOrder[index];
                    status = string.Format(
                        LM.Get("terrain_lab_hydrology_map_status_format"),
                        tile.x,
                        tile.y,
                        value,
                        accumulation,
                        (int)filled - elevation,
                        GetDirectionToken(direction),
                        isStream ? LM.Get("terrain_lab_hydrology_stream") : string.Empty,
                        watershed,
                        streamOrder == byte.MaxValue ? 0 : streamOrder);
                }
                else if (_reliefOverlay != null &&
                         _reliefOverlay.Mode != TerrainReliefOverlayMode.None &&
                         state.Relief != null && state.Relief.IsCurrent(state))
                {
                    int index = tile.y * state.Width + tile.x;
                    ushort slope = state.Relief.SlopeTenths[index];
                    ushort aspect = state.Relief.AspectTenths[index];
                    ushort ruggedness = state.Relief.Ruggedness[index];
                    status = string.Format(
                        LM.Get("terrain_lab_relief_map_status_format"),
                        tile.x,
                        tile.y,
                        value,
                        slope == ushort.MaxValue ? 0.0 : slope / 10.0,
                        aspect == ushort.MaxValue ? 0.0 : aspect / 10.0,
                        ruggedness == ushort.MaxValue ? 0 : ruggedness);
                }
                else if (_erosionOverlay != null &&
                         _erosionOverlay.Mode != TerrainErosionOverlayMode.None &&
                         state.Erosion != null && state.Erosion.IsCurrent(state))
                {
                    int index = tile.y * state.Width + tile.x;
                    int change = state.Erosion.NetChange[index];
                    status = string.Format(
                        LM.Get("terrain_lab_erosion_map_status_format"),
                        tile.x,
                        tile.y,
                        value,
                        change == int.MinValue ? 0 : change);
                }
                else
                {
                    status = string.Format(
                        LM.Get("terrain_lab_map_status_format"),
                        tile.x,
                        tile.y,
                        value,
                        budgetPercent,
                        tool);
                }
            }
            else
            {
                status = string.Format(
                    LM.Get("terrain_lab_map_status_empty_format"),
                    budgetPercent,
                    tool);
            }

            if (status == _lastMapStatus)
            {
                return;
            }

            _lastMapStatus = status;
            _mapStatusText.text = status;
        }

        private void UpdateAnalysisProgress()
        {
            List<string> progressParts = new List<string>(3);
            TerrainReliefService relief = TerrainLabRuntime.Instance?.Relief;
            if (relief != null && relief.IsRunning)
            {
                _runningJobId = "relief";
                int progress = relief.ProgressPercent;
                string progressText = string.Format(
                    LM.Get("terrain_lab_relief_progress_format"),
                    progress);
                progressParts.Add(progressText);
                if (progress != _lastReliefProgress)
                {
                    _lastReliefProgress = progress;
                    if (_reliefProgressText != null)
                    {
                        _reliefProgressText.text = progressText;
                    }
                }
            }

            TerrainHydrologyModule hydrology = TerrainLabRuntime.Instance?.Hydrology;
            if (hydrology != null && hydrology.IsRunning)
            {
                _runningJobId = "hydrology";
                int progress = hydrology.ProgressPercent;
                string progressText = string.Format(
                    LM.Get("terrain_lab_hydrology_progress_format"),
                    progress);
                progressParts.Add(progressText);
                if (progress != _lastHydrologyProgress)
                {
                    _lastHydrologyProgress = progress;
                    if (_hydrologyProgressText != null)
                    {
                        _hydrologyProgressText.text = progressText;
                    }
                }
            }

            TerrainErosionModule erosion = TerrainLabRuntime.Instance?.Erosion;
            if (erosion != null && erosion.IsRunning)
            {
                _runningJobId = "erosion";
                int progress = erosion.ProgressPercent;
                string progressText = string.Format(
                    LM.Get("terrain_lab_erosion_progress_format"),
                    progress);
                progressParts.Add(progressText);
                if (progress != _lastErosionProgress)
                {
                    _lastErosionProgress = progress;
                    if (_erosionProgressText != null)
                    {
                        _erosionProgressText.text = progressText;
                    }
                }
            }

            bool jobsRunning = progressParts.Count > 0;
            if (jobsRunning)
            {
                SetToolbarStatus(string.Join(" | ", progressParts), WarningText);
            }
            else if (_jobsRunningLastFrame)
            {
                string error = GetLastJobError(_runningJobId);
                if (string.IsNullOrWhiteSpace(error))
                {
                    if (string.IsNullOrEmpty(GetActiveToolbarOverlay()))
                    {
                        SetStatus(LM.Get("terrain_lab_status_ready"), false, true);
                    }
                }
                else
                {
                    SetError(error);
                }

                _runningJobId = null;
            }

            _jobsRunningLastFrame = jobsRunning;
        }

        private string GetLastJobError(string jobId)
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            switch (jobId)
            {
                case "relief":
                    return runtime?.Relief?.LastError;
                case "hydrology":
                    return runtime?.Hydrology?.LastError;
                case "erosion":
                    return runtime?.Erosion?.LastError;
                default:
                    return null;
            }
        }

        private void SelectToolbarSection(ToolbarSection section)
        {
            if (section == ToolbarSection.Primary)
            {
                return;
            }

            if (_activeToolbarSection == section)
            {
                _editor.SetTool(TerrainEditorTool.None);
                _activeToolbarSection = ToolbarSection.Primary;
                UpdateToolbarSectionVisibility();
                UpdateAdaptiveToolbarLayout(true);
                UpdateToolbarState();
                return;
            }

            if (_editor.Tool != TerrainEditorTool.None &&
                GetToolbarSection(_editor.Tool) != section)
            {
                _editor.SetTool(TerrainEditorTool.None);
            }

            _activeToolbarSection = section;
            UpdateToolbarSectionVisibility();
            UpdateAdaptiveToolbarLayout(true);
            UpdateToolbarState();
        }

        private static ToolbarSection GetToolbarSection(TerrainEditorTool tool)
        {
            if (tool == TerrainEditorTool.None)
            {
                return ToolbarSection.Primary;
            }

            return tool >= TerrainEditorTool.SampleSurface
                ? ToolbarSection.Digitizing
                : ToolbarSection.Terrain;
        }

        private void UpdateToolbarSectionVisibility()
        {
            for (int index = 0; index < _toolbarLayoutButtons.Count; index++)
            {
                ToolbarLayoutButton item = _toolbarLayoutButtons[index];
                bool visible = item.Role != ToolbarButtonRole.Functional ||
                               item.Section == _activeToolbarSection;
                item.Rect.gameObject.SetActive(visible);
            }

            for (int index = 0; index < _toolbarLayoutSeparators.Count; index++)
            {
                ToolbarLayoutSeparator separator = _toolbarLayoutSeparators[index];
                bool visible = separator.Section == ToolbarSection.Primary ||
                               separator.Section == _activeToolbarSection;
                separator.Rect.gameObject.SetActive(visible);
            }
        }

        private void UpdateToolbarState()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWorldState state = runtime?.State;
            bool hasState = state != null;

            foreach (KeyValuePair<ToolbarSection, SimpleButton> pair in
                     _toolbarSectionButtons)
            {
                pair.Value.Button.interactable = true;
                pair.Value.Background.color = Color.white;
                SetToolbarActivity(
                    pair.Value,
                    pair.Key == _activeToolbarSection,
                    ActivityGreen);
            }

            SetToolbarCommandEnabled("menu", true);
            SetToolbarCommandEnabled("save", hasState);
            SetToolbarCommandEnabled("export", hasState);
            SetToolbarCommandEnabled("validate", hasState);
            SetToolbarCommandEnabled("export_gis", hasState);
            SetToolbarCommandEnabled("sync_prepare", hasState);
            bool analysisRunning = IsAnalysisRunning();
            SetToolbarCommandEnabled("sync_pull", hasState && !analysisRunning);
            SetToolbarCommandEnabled("sync_branch", hasState && !analysisRunning);
            SetToolbarCommandEnabled(
                "undo",
                hasState && !analysisRunning && _editor.CanUndo);
            SetToolbarCommandEnabled(
                "redo",
                hasState && !analysisRunning && _editor.CanRedo);
            SetToolbarCommandEnabled(
                "selection_apply",
                hasState && !analysisRunning &&
                _editor.HasSurfaceSample && _editor.HasPolygonizedSelection);
            SetToolbarCommandEnabled(
                "digitizing_cancel",
                hasState && !analysisRunning);
            SetToolbarActivity(
                _toolbarCommandButtons["menu"],
                _window != null && _window.gameObject.activeInHierarchy,
                ActivityAmber);
            SetToolbarActivity(
                _toolbarCommandButtons["selection_apply"],
                hasState && !analysisRunning &&
                _editor.HasSurfaceSample && _editor.HasPolygonizedSelection,
                ActivityGreen);

            foreach (KeyValuePair<TerrainEditorTool, SimpleButton> pair in
                     _toolbarEditorButtons)
            {
                pair.Value.Button.interactable = hasState && !analysisRunning;
            }

            UpdateEditorToolSelection();

            bool reliefRunning = runtime?.Relief != null && runtime.Relief.IsRunning;
            bool hydrologyRunning = runtime?.Hydrology != null && runtime.Hydrology.IsRunning;
            bool erosionRunning = runtime?.Erosion != null && runtime.Erosion.IsRunning;
            bool reliefCurrent = hasState && state.Relief != null &&
                                 state.Relief.IsCurrent(state);
            bool hydrologyCurrent = hasState && state.Hydrology != null &&
                                    state.Hydrology.IsCurrent(state);
            bool erosionCurrent = hasState && state.Erosion != null &&
                                  state.Erosion.IsCurrent(state);

            UpdateJobButton(
                _reliefJobButton,
                reliefRunning,
                hasState && (!analysisRunning || reliefRunning),
                reliefCurrent);
            UpdateJobButton(
                _hydrologyJobButton,
                hydrologyRunning,
                hasState && (!analysisRunning || hydrologyRunning),
                hydrologyCurrent);
            if (_waterDynamicsButton != null)
            {
                TerrainWaterDynamicsService waterService = runtime?.WaterDynamics;
                bool waterEnabled = hasState && waterService?.Enabled == true;
                _waterDynamicsButton.Button.interactable = hasState && !analysisRunning;
                _waterDynamicsButton.Background.color = hasState && !analysisRunning
                    ? Color.white
                    : InactiveButton;
                SetToolbarActivity(
                    _waterDynamicsButton,
                    waterEnabled,
                    waterService?.LimitReached == true
                        ? ActivityAmber
                        : ActivityGreen);
            }
            UpdateJobButton(
                _erosionJobButton,
                erosionRunning,
                hasState && (hydrologyCurrent || erosionRunning) &&
                (!analysisRunning || erosionRunning),
                erosionCurrent);

            bool canApplyErosion = erosionCurrent &&
                                   state.Erosion.Statistics.MassBalance == 0 &&
                                   !analysisRunning;
            if (_applyErosionButton != null)
            {
                _applyErosionButton.Button.interactable = canApplyErosion;
                _applyErosionButton.Background.color = canApplyErosion
                    ? Color.white
                    : InactiveButton;
                SetToolbarActivity(
                    _applyErosionButton,
                    canApplyErosion,
                    ActivityGreen);
            }

            string activeOverlay = GetActiveToolbarOverlay();
            foreach (KeyValuePair<string, SimpleButton> pair in _toolbarOverlayButtons)
            {
                bool enabled = hasState;
                pair.Value.Button.interactable = enabled;
                pair.Value.Background.color = enabled ? Color.white : InactiveButton;
                SetToolbarActivity(
                    pair.Value,
                    pair.Key == activeOverlay,
                    ActivityGreen);
            }
        }

        private bool IsAnalysisRunning()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            return (runtime?.Relief != null && runtime.Relief.IsRunning) ||
                   (runtime?.Hydrology != null && runtime.Hydrology.IsRunning) ||
                   (runtime?.Erosion != null && runtime.Erosion.IsRunning);
        }

        private void SetToolbarCommandEnabled(string id, bool enabled)
        {
            if (!_toolbarCommandButtons.TryGetValue(id, out SimpleButton button))
            {
                return;
            }

            button.Button.interactable = enabled;
            button.Background.color = enabled ? Color.white : InactiveButton;
        }

        private void UpdateJobButton(
            SimpleButton button,
            bool running,
            bool enabled,
            bool current)
        {
            if (button == null)
            {
                return;
            }

            button.Button.interactable = enabled || running;
            button.Background.color = running
                ? WarningText
                : enabled ? Color.white : InactiveButton;
            button.Icon.sprite = GetIcon(
                running ? "ui/Icons/iconPause" : "ui/Icons/iconPlay");
            SetToolbarActivity(
                button,
                running || current,
                running ? ActivityAmber : ActivityGreen);
        }

        private string GetActiveToolbarOverlay()
        {
            switch (_pendingOverlayKind)
            {
                case PendingOverlayKind.Relief:
                    return GetReliefOverlayToolbarId(_pendingReliefOverlay);
                case PendingOverlayKind.Hydrology:
                    return GetHydrologyOverlayToolbarId(_pendingHydrologyOverlay);
                case PendingOverlayKind.Erosion:
                    return GetErosionOverlayToolbarId(_pendingErosionOverlay);
            }

            if (_elevationOverlay != null && _elevationOverlay.IsVisible)
            {
                return "dem_elevation";
            }

            if (_dataOverlay != null)
            {
                switch (_dataOverlay.Mode)
                {
                    case TerrainDataOverlayMode.Contours:
                        return "dem_contours";
                    case TerrainDataOverlayMode.Landform:
                        return "core_landform";
                    case TerrainDataOverlayMode.Material:
                        return "core_material";
                    case TerrainDataOverlayMode.ManagedWater:
                        return "water_managed";
                    case TerrainDataOverlayMode.WaterStorage:
                        return "water_storage";
                    case TerrainDataOverlayMode.HydroFeature:
                        return "water_hydro_feature";
                    case TerrainDataOverlayMode.Moisture:
                        return "water_moisture";
                    case TerrainDataOverlayMode.Erodibility:
                        return "water_erodibility";
                    case TerrainDataOverlayMode.LocalSlope:
                        return "water_local_slope";
                    case TerrainDataOverlayMode.LocalAspect:
                        return "water_local_aspect";
                }
            }

            if (_reliefOverlay != null)
            {
                switch (_reliefOverlay.Mode)
                {
                    case TerrainReliefOverlayMode.Hypsometry:
                        return "relief_hypsometry";
                    case TerrainReliefOverlayMode.Slope:
                        return "relief_slope";
                    case TerrainReliefOverlayMode.Aspect:
                        return "relief_aspect";
                    case TerrainReliefOverlayMode.Hillshade:
                        return "relief_hillshade";
                    case TerrainReliefOverlayMode.Ruggedness:
                        return "relief_ruggedness";
                }
            }

            if (_hydrologyOverlay != null)
            {
                switch (_hydrologyOverlay.Mode)
                {
                    case TerrainHydrologyOverlayMode.Streams:
                        return "hydrology_streams";
                    case TerrainHydrologyOverlayMode.Accumulation:
                        return "hydrology_accumulation";
                    case TerrainHydrologyOverlayMode.FillDepth:
                        return "hydrology_fill";
                    case TerrainHydrologyOverlayMode.FilledElevation:
                        return "hydrology_filled";
                    case TerrainHydrologyOverlayMode.FlowDirection:
                        return "hydrology_direction";
                    case TerrainHydrologyOverlayMode.Watersheds:
                        return "hydrology_watersheds";
                    case TerrainHydrologyOverlayMode.StreamOrder:
                        return "hydrology_order";
                }
            }

            if (_erosionOverlay != null)
            {
                switch (_erosionOverlay.Mode)
                {
                    case TerrainErosionOverlayMode.NetChange:
                        return "erosion_net";
                    case TerrainErosionOverlayMode.Erosion:
                        return "erosion_cut";
                    case TerrainErosionOverlayMode.Deposition:
                        return "erosion_fill";
                    case TerrainErosionOverlayMode.ResultElevation:
                        return "erosion_result";
                }
            }

            return null;
        }

        private static string GetReliefOverlayToolbarId(
            TerrainReliefOverlayMode mode)
        {
            switch (mode)
            {
                case TerrainReliefOverlayMode.Hypsometry:
                    return "relief_hypsometry";
                case TerrainReliefOverlayMode.Slope:
                    return "relief_slope";
                case TerrainReliefOverlayMode.Aspect:
                    return "relief_aspect";
                case TerrainReliefOverlayMode.Hillshade:
                    return "relief_hillshade";
                case TerrainReliefOverlayMode.Ruggedness:
                    return "relief_ruggedness";
                default:
                    return null;
            }
        }

        private static string GetHydrologyOverlayToolbarId(
            TerrainHydrologyOverlayMode mode)
        {
            switch (mode)
            {
                case TerrainHydrologyOverlayMode.Streams:
                    return "hydrology_streams";
                case TerrainHydrologyOverlayMode.Accumulation:
                    return "hydrology_accumulation";
                case TerrainHydrologyOverlayMode.FillDepth:
                    return "hydrology_fill";
                case TerrainHydrologyOverlayMode.FilledElevation:
                    return "hydrology_filled";
                case TerrainHydrologyOverlayMode.FlowDirection:
                    return "hydrology_direction";
                case TerrainHydrologyOverlayMode.Watersheds:
                    return "hydrology_watersheds";
                case TerrainHydrologyOverlayMode.StreamOrder:
                    return "hydrology_order";
                default:
                    return null;
            }
        }

        private static string GetErosionOverlayToolbarId(
            TerrainErosionOverlayMode mode)
        {
            switch (mode)
            {
                case TerrainErosionOverlayMode.NetChange:
                    return "erosion_net";
                case TerrainErosionOverlayMode.Erosion:
                    return "erosion_cut";
                case TerrainErosionOverlayMode.Deposition:
                    return "erosion_fill";
                case TerrainErosionOverlayMode.ResultElevation:
                    return "erosion_result";
                default:
                    return null;
            }
        }

        private void CreateModuleButton(Transform parent, string moduleId, string labelKey)
        {
            SimpleButton button = CreateActionButton(
                parent,
                LM.Get(labelKey),
                delegate { SelectModule(moduleId); },
                194f,
                30f,
                GetModuleIconId(moduleId),
                labelKey,
                labelKey + "_description");
            button.name = "TerrainLabModule_" + moduleId;
            _moduleButtons[moduleId] = button;
        }

        private static string GetModuleIconId(string moduleId)
        {
            switch (moduleId)
            {
                case "project":
                    return "project_open";
                case "parameters":
                    return "layer_properties";
                case "layers":
                    return "layer_stack";
                case "settings":
                    return "module_manager";
                default:
                    return "module_manager";
            }
        }

        private void SelectModule(string moduleId)
        {
            _selectedModule = moduleId;
            UpdateModuleSelection();
            RebuildModuleContent();
            SetStatus(LM.Get("terrain_lab_status_ready"), false);
        }

        private void UpdateModuleSelection()
        {
            foreach (KeyValuePair<string, SimpleButton> pair in _moduleButtons)
            {
                pair.Value.Background.color = pair.Key == _selectedModule
                    ? Color.white
                    : InactiveButton;
            }
        }

        private void RebuildModuleContent()
        {
            if (_moduleContent == null)
            {
                return;
            }

            _editorToolButtons.Clear();
            _brushRadiusText = null;
            _reliefProgressText = null;
            _hydrologyProgressText = null;
            _erosionProgressText = null;
            _lastReliefProgress = -1;
            _lastHydrologyProgress = -1;
            _lastErosionProgress = -1;
            ClearChildren(_moduleContent);
            switch (_selectedModule)
            {
                case "project":
                    BuildProjectView();
                    break;
                case "parameters":
                    BuildParametersView();
                    break;
                case "layers":
                    BuildLayerCatalogView();
                    break;
                case "settings":
                    BuildSettingsView();
                    break;
            }

            RefreshModuleContentLayout();
        }

        private void RefreshModuleContentLayout()
        {
            RectTransform moduleRect = _moduleContent as RectTransform;
            if (moduleRect == null)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(moduleRect);
            VerticalLayoutGroup moduleLayout =
                moduleRect.GetComponent<VerticalLayoutGroup>();
            float preferredHeight = moduleLayout == null
                ? 0f
                : moduleLayout.preferredHeight;
            moduleRect.SetSizeWithCurrentAnchors(
                RectTransform.Axis.Vertical,
                preferredHeight);

            LayoutElement element = moduleRect.GetComponent<LayoutElement>();
            if (element != null)
            {
                element.preferredHeight = preferredHeight;
            }

            Canvas.ForceUpdateCanvases();
            RectTransform parentRect = moduleRect.parent as RectTransform;
            if (parentRect != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(parentRect);
            }
        }

        private void BuildProjectView()
        {
            CreateSectionHeading(_moduleContent, "terrain_lab_project_heading");
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            if (runtime == null)
            {
                CreateInfo("terrain_lab_runtime_unavailable", ErrorText, 34f);
                return;
            }

            TerrainWorldState state = runtime.State;
            int width = state?.Width ?? MapBox.width;
            int height = state?.Height ?? MapBox.height;
            long cells = TerrainMapLimits.CountCells(width, height);

            if (width > 0 && height > 0)
            {
                CreateInfoText(
                    string.Format(LM.Get("terrain_lab_dimensions_format"), width, height),
                    NeutralText);
                double budgetPercent = cells * 100.0 / TerrainMapLimits.MaximumCellCount;
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_budget_format"),
                        cells,
                        TerrainMapLimits.MaximumCellCount,
                        budgetPercent),
                    TerrainMapLimits.IsWithinBudget(width, height) ? NeutralText : ErrorText,
                    32f);
            }

            if (state == null)
            {
                CreateInfo("terrain_lab_no_project_state", NeutralText, 34f);
            }
            else
            {
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_project_id_format"),
                        CompactIdentifier(state.ProjectId)),
                    NeutralText);
                CreateInfoText(
                    string.Format(LM.Get("terrain_lab_sea_level_format"), state.SeaLevel),
                    NeutralText);
            }

            bool packageExists = runtime.CurrentPackagePath != null &&
                                 File.Exists(runtime.CurrentPackagePath);
            if (runtime.HasUnsavedChanges)
            {
                CreateInfo("terrain_lab_package_modified", WarningText);
            }
            else
            {
                CreateInfo(
                    packageExists
                        ? "terrain_lab_package_saved"
                        : "terrain_lab_package_missing",
                    packageExists ? SuccessText : NeutralText);
            }

            CreateActionButton(
                _moduleContent,
                LM.Get("terrain_lab_action_open_exchange"),
                OpenExchangeDirectory,
                194f,
                28f,
                "project_open",
                "terrain_lab_action_open_exchange",
                "terrain_lab_action_open_exchange_description");

            CreateSectionHeading(_moduleContent, "terrain_lab_exchange_heading");
            IReadOnlyList<string> packages = runtime.GetExchangePackages(3);
            if (packages.Count == 0)
            {
                CreateInfo("terrain_lab_exchange_empty", NeutralText, 30f);
                return;
            }

            foreach (string packagePath in packages)
            {
                string capturedPath = packagePath;
                CreateActionButton(
                    _moduleContent,
                    string.Format(
                        LM.Get("terrain_lab_action_import_format"),
                        CompactFileName(packagePath, 22)),
                    delegate { ImportProject(capturedPath); },
                    194f,
                    28f,
                    "import_wbxgeo",
                    "terrain_lab_action_import",
                    "terrain_lab_action_import_description");
            }
        }

        private void BuildParametersView()
        {
            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            if (state == null || _editor == null)
            {
                CreateInfo("terrain_lab_no_project_state", ErrorText, 34f);
                return;
            }

            CreateSectionHeading(_moduleContent, "terrain_lab_dem_editor_heading");
            CreateNumericInputRow(
                "TerrainLabElevationTarget",
                "terrain_lab_elevation_value",
                _editor.TargetElevation.ToString(),
                HandleTargetElevationChanged);
            CreateNumericInputRow(
                "TerrainLabElevationStep",
                "terrain_lab_elevation_step",
                _editor.Step.ToString(),
                HandleElevationStepChanged);

            Transform radiusRow = CreateActionRow(
                _moduleContent,
                "TerrainLabElevationRadius");
            CreateTextLabel(
                radiusRow,
                "TerrainLabElevationRadiusLabel",
                LM.Get("terrain_lab_brush_radius"),
                10f,
                FontStyle.Normal,
                28f,
                TextAnchor.MiddleLeft,
                NeutralText,
                78f);
            CreateActionButton(
                radiusRow,
                "-",
                DecreaseBrushRadius,
                34f,
                26f,
                null,
                "terrain_lab_action_radius_decrease",
                "terrain_lab_action_radius_decrease_description");
            _brushRadiusText = CreateTextLabel(
                radiusRow,
                "TerrainLabElevationRadiusValue",
                _editor.BrushRadius.ToString(),
                11f,
                FontStyle.Bold,
                28f,
                TextAnchor.MiddleCenter,
                Color.white,
                40f);
            CreateActionButton(
                radiusRow,
                "+",
                IncreaseBrushRadius,
                34f,
                26f,
                null,
                "terrain_lab_action_radius_increase",
                "terrain_lab_action_radius_increase_description");

            CreateSectionHeading(_moduleContent, "terrain_lab_hydrology_heading");
            EnsureHydrologyThreshold(state);
            CreateNumericInputRow(
                "TerrainLabHydrologyThreshold",
                "terrain_lab_hydrology_threshold",
                _hydrologyThreshold.ToString(),
                HandleHydrologyThresholdChanged,
                8);

            BuildWaterDynamicsParameters(state);

            CreateSectionHeading(_moduleContent, "terrain_lab_erosion_parameters_heading");
            EnsureErosionParameters(state);
            CreateNumericInputRow(
                "TerrainLabErosionIterations",
                "terrain_lab_erosion_iterations",
                _erosionIterations.ToString(),
                HandleErosionIterationsChanged,
                3);
            CreateNumericInputRow(
                "TerrainLabErosionFlowStrength",
                "terrain_lab_erosion_flow_strength",
                _erosionFlowStrength.ToString(),
                HandleErosionFlowStrengthChanged,
                3);
            CreateNumericInputRow(
                "TerrainLabErosionThermalStrength",
                "terrain_lab_erosion_thermal_strength",
                _erosionThermalStrength.ToString(),
                HandleErosionThermalStrengthChanged,
                3);
            CreateNumericInputRow(
                "TerrainLabErosionTalus",
                "terrain_lab_erosion_talus",
                _erosionTalusThreshold.ToString(),
                HandleErosionTalusChanged,
                5);
        }

        private void BuildLayerCatalogView()
        {
            CreateSectionHeading(_moduleContent, "terrain_lab_layer_catalog_heading");
            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            if (state == null)
            {
                CreateInfo("terrain_lab_no_project_state", ErrorText, 34f);
                return;
            }

            foreach (TerrainLayerInfo layer in TerrainLayerCatalog.Build(state))
            {
                Color color = layer.Availability == TerrainLayerAvailability.Ready
                    ? SuccessText
                    : layer.Availability == TerrainLayerAvailability.Stale
                        ? WarningText
                        : NeutralText;
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_layer_catalog_format"),
                        layer.Id,
                        LM.Get(GetLayerAvailabilityLocalizationKey(layer.Availability))),
                    color,
                    30f);
            }

            CreateSectionHeading(_moduleContent, "terrain_lab_analysis_status_heading");
            TerrainReliefResult relief = state.Relief;
            if (relief != null && relief.IsCurrent(state))
            {
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_relief_range_format"),
                        relief.Statistics.MinimumElevation,
                        relief.Statistics.MaximumElevation),
                    NeutralText,
                    28f);
            }

            TerrainHydrologyResult hydrology = state.Hydrology;
            if (hydrology != null && hydrology.IsCurrent(state))
            {
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_hydrology_basin_stats_format"),
                        hydrology.Statistics.WatershedCount,
                        hydrology.Statistics.MaximumStreamOrder),
                    NeutralText,
                    28f);
            }

            TerrainErosionResult erosion = state.Erosion;
            if (erosion != null && erosion.IsCurrent(state))
            {
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_erosion_mass_balance_format"),
                        erosion.Statistics.MassBalance),
                    erosion.Statistics.MassBalance == 0 ? SuccessText : ErrorText,
                    28f);
            }
        }

        private void BuildWaterDynamicsParameters(TerrainWorldState state)
        {
            CreateSectionHeading(_moduleContent, "terrain_lab_water_dynamics_heading");
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWaterDynamicsService service = runtime?.WaterDynamics;
            TerrainWaterState water = state.WaterDynamics;
            TerrainWaterParameters parameters = water?.Parameters ??
                new TerrainWaterParameters();

            int validCells = 0;
            for (int index = 0; index < state.Elevation.Length; index++)
            {
                if (state.Elevation[index] != TerrainElevationEncoding.NoData)
                {
                    validCells++;
                }
            }

            int floodLimit = service?.FloodCellLimit ?? 0;
            if (floodLimit <= 0)
            {
                floodLimit = Math.Max(
                    1,
                    validCells * parameters.MaximumFloodPercent / 100);
            }

            bool enabled = service?.Enabled == true;
            CreateInfoText(
                string.Format(
                    LM.Get("terrain_lab_water_dynamics_status_format"),
                    enabled
                        ? LM.Get("terrain_lab_water_dynamics_enabled")
                        : LM.Get("terrain_lab_water_dynamics_disabled"),
                    service?.ManagedCellCount ?? water?.ManagedCellCount ?? 0,
                    floodLimit,
                    service?.ActiveSourceCount ?? 0,
                    service?.PendingVolume ?? 0L),
                service?.LimitReached == true
                    ? WarningText
                    : enabled ? SuccessText : NeutralText,
                42f);

            if (!string.IsNullOrWhiteSpace(service?.LastError))
            {
                CreateInfoText(service.LastError, ErrorText, 42f);
            }

            CreateActionButton(
                _moduleContent,
                LM.Get(enabled
                    ? "terrain_lab_water_dynamics_disable"
                    : "terrain_lab_water_dynamics_enable"),
                ToggleWaterDynamics,
                194f,
                28f,
                "ui/Icons/iconRain",
                enabled
                    ? "terrain_lab_water_dynamics_disable"
                    : "terrain_lab_water_dynamics_enable",
                "terrain_lab_toolbar_water_dynamics_description");
            CreateSectionHeading(
                _moduleContent,
                "terrain_lab_water_routing_algorithm_heading");
            Transform routingRow = CreateActionRow(
                _moduleContent,
                "TerrainLabWaterRoutingAlgorithm");
            CreateWaterRoutingButton(
                routingRow,
                parameters.RoutingAlgorithm,
                TerrainWaterRoutingAlgorithm.D8,
                "terrain_lab_water_routing_d8");
            CreateWaterRoutingButton(
                routingRow,
                parameters.RoutingAlgorithm,
                TerrainWaterRoutingAlgorithm.DInfinity,
                "terrain_lab_water_routing_dinf");
            CreateWaterRoutingButton(
                routingRow,
                parameters.RoutingAlgorithm,
                TerrainWaterRoutingAlgorithm.MultipleFlowDirection,
                "terrain_lab_water_routing_mfd");
            CreateNumericInputRow(
                "TerrainLabWaterMaximumFlood",
                "terrain_lab_water_maximum_flood_percent",
                parameters.MaximumFloodPercent.ToString(),
                HandleWaterMaximumFloodChanged,
                2);
            CreateNumericInputRow(
                "TerrainLabWaterInitialVolume",
                "terrain_lab_water_initial_source_volume",
                parameters.InitialSourceVolume.ToString(),
                HandleWaterInitialVolumeChanged,
                4);
            CreateNumericInputRow(
                "TerrainLabWaterGeyserVolume",
                "terrain_lab_water_geyser_pulse_volume",
                parameters.GeyserPulseVolume.ToString(),
                HandleWaterGeyserVolumeChanged,
                4);
            CreateNumericInputRow(
                "TerrainLabWaterCellsPerTick",
                "terrain_lab_water_cells_per_tick",
                parameters.CellsPerTick.ToString(),
                HandleWaterCellsPerTickChanged,
                3);
            CreateNumericInputRow(
                "TerrainLabWaterEvaporation",
                "terrain_lab_water_evaporation_per_climate_step",
                parameters.EvaporationPerClimateStep.ToString(),
                HandleWaterEvaporationChanged,
                2);
        }

        private void BuildReliefView()
        {
            CreateSectionHeading(_moduleContent, "terrain_lab_layers_heading");
            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            if (state == null)
            {
                CreateInfo("terrain_lab_no_project_state", NeutralText, 34f);
                return;
            }

            CreateLayerButton("elevation", "terrain_lab_layer_elevation");
            CreateLayerButton("landform", "terrain_lab_layer_landform");
            CreateLayerButton("material", "terrain_lab_layer_material");
            CreateLayerButton("vanilla", "terrain_lab_layer_vanilla");

            CreateSectionHeading(_moduleContent, "terrain_lab_layer_properties_heading");
            switch (_selectedLayer)
            {
                case "elevation":
                    CreateInfo("terrain_lab_layer_elevation_storage", NeutralText, 30f);
                    CreateInfoText(
                        string.Format(
                            LM.Get("terrain_lab_layer_nodata_format"),
                            TerrainElevationEncoding.NoData),
                        NeutralText);
                    CreateInfoText(
                        string.Format(LM.Get("terrain_lab_sea_level_format"), state.SeaLevel),
                        NeutralText);
                    BuildElevationEditor();
                    break;
                case "landform":
                    CreateInfo("terrain_lab_layer_landform_storage", NeutralText, 30f);
                    CreateInfo("terrain_lab_layer_landform_semantics", NeutralText, 42f);
                    break;
                case "material":
                    CreateInfo("terrain_lab_layer_material_storage", NeutralText, 30f);
                    CreateInfo("terrain_lab_layer_material_semantics", NeutralText, 42f);
                    break;
                case "vanilla":
                    CreateInfo("terrain_lab_layer_vanilla_storage", NeutralText, 30f);
                    CreateInfo("terrain_lab_layer_vanilla_semantics", NeutralText, 42f);
                    break;
            }

            CreateInfoText(
                string.Format(
                    LM.Get("terrain_lab_dimensions_format"),
                    state.Width,
                    state.Height),
                NeutralText);

            BuildReliefAnalysis(state);
        }

        private void BuildReliefAnalysis(TerrainWorldState state)
        {
            CreateSectionHeading(_moduleContent, "terrain_lab_relief_analysis_heading");
            TerrainReliefService service = TerrainLabRuntime.Instance?.Relief;
            if (service == null)
            {
                CreateInfo("terrain_lab_runtime_unavailable", ErrorText, 34f);
                return;
            }

            if (service.IsRunning)
            {
                _reliefProgressText = CreateTextLabel(
                    _moduleContent,
                    "TerrainLabReliefProgress",
                    string.Format(
                        LM.Get("terrain_lab_relief_progress_format"),
                        service.ProgressPercent),
                    11f,
                    FontStyle.Bold,
                    30f,
                    TextAnchor.MiddleCenter,
                    WarningText);
                CreateActionButton(
                    _moduleContent,
                    LM.Get("terrain_lab_relief_cancel"),
                    CancelRelief,
                    194f,
                    28f,
                    "ui/Icons/iconPause",
                    "terrain_lab_relief_cancel",
                    "terrain_lab_relief_cancel_description");
                return;
            }

            if (!string.IsNullOrWhiteSpace(service.LastError))
            {
                CreateInfoText(service.LastError, WarningText, 42f);
            }

            TerrainReliefResult result = state.Relief;
            bool resultCurrent = result != null && result.IsCurrent(state);
            if (!resultCurrent)
            {
                CreateInfo(
                    result == null
                        ? "terrain_lab_relief_not_computed"
                        : "terrain_lab_relief_stale",
                    result == null ? NeutralText : WarningText,
                    34f);
            }
            else
            {
                CreateInfo("terrain_lab_relief_algorithm", SuccessText, 30f);
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_relief_range_format"),
                        result.Statistics.MinimumElevation,
                        result.Statistics.MaximumElevation),
                    NeutralText,
                    28f);
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_relief_stats_format"),
                        result.Statistics.MaximumSlopeTenths / 10.0,
                        result.Statistics.MaximumRuggedness),
                    NeutralText,
                    28f);
            }

            CreateActionButton(
                _moduleContent,
                LM.Get(resultCurrent
                    ? "terrain_lab_relief_recompute"
                    : "terrain_lab_relief_run"),
                RunRelief,
                194f,
                28f,
                "ui/Icons/iconPlay",
                resultCurrent
                    ? "terrain_lab_relief_recompute"
                    : "terrain_lab_relief_run",
                "terrain_lab_relief_run_description");
            CreateSectionHeading(_moduleContent, "terrain_lab_relief_overlays_heading");
            Transform firstRow = CreateActionRow(
                _moduleContent,
                "TerrainLabReliefOverlaysPrimary");
            CreateActionButton(
                firstRow,
                LM.Get("terrain_lab_relief_overlay_hypsometry"),
                delegate { ShowReliefOverlay(TerrainReliefOverlayMode.Hypsometry); },
                94f,
                28f,
                "visibility_on",
                "terrain_lab_relief_overlay_hypsometry",
                "terrain_lab_relief_overlay_hypsometry_description");
            CreateActionButton(
                firstRow,
                LM.Get("terrain_lab_relief_overlay_slope"),
                delegate { ShowReliefOverlay(TerrainReliefOverlayMode.Slope); },
                94f,
                28f,
                "layer_filter",
                "terrain_lab_relief_overlay_slope",
                "terrain_lab_relief_overlay_slope_description");

            Transform secondRow = CreateActionRow(
                _moduleContent,
                "TerrainLabReliefOverlaysSecondary");
            CreateActionButton(
                secondRow,
                LM.Get("terrain_lab_relief_overlay_aspect"),
                delegate { ShowReliefOverlay(TerrainReliefOverlayMode.Aspect); },
                94f,
                28f,
                "visibility_on",
                "terrain_lab_relief_overlay_aspect",
                "terrain_lab_relief_overlay_aspect_description");
            CreateActionButton(
                secondRow,
                LM.Get("terrain_lab_relief_overlay_hillshade"),
                delegate { ShowReliefOverlay(TerrainReliefOverlayMode.Hillshade); },
                94f,
                28f,
                "visibility_on",
                "terrain_lab_relief_overlay_hillshade",
                "terrain_lab_relief_overlay_hillshade_description");
            Transform thirdRow = CreateActionRow(
                _moduleContent,
                "TerrainLabReliefOverlaysAdvanced");
            CreateActionButton(
                thirdRow,
                LM.Get("terrain_lab_relief_overlay_ruggedness"),
                delegate { ShowReliefOverlay(TerrainReliefOverlayMode.Ruggedness); },
                94f,
                28f,
                "layer_style",
                "terrain_lab_relief_overlay_ruggedness",
                "terrain_lab_relief_overlay_ruggedness_description");
            CreateActionButton(
                thirdRow,
                LM.Get("terrain_lab_relief_overlay_hide"),
                HideReliefOverlay,
                94f,
                28f,
                "visibility_off",
                "terrain_lab_relief_overlay_hide",
                "terrain_lab_relief_overlay_hide_description");
        }

        private void RunRelief()
        {
            ClearPendingOverlayRequest();
            StartReliefAnalysis();
        }

        private bool StartReliefAnalysis()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            string error = null;
            if (runtime != null && runtime.TryStartReliefAnalysis(out error))
            {
                _runningJobId = "relief";
                _jobsRunningLastFrame = true;
                HideAllOverlays();
                RebuildModuleContent();
                SetStatus(LM.Get("terrain_lab_relief_started"), false, true);
                return true;
            }

            SetError(error ?? LM.Get("terrain_lab_runtime_unavailable"));
            return false;
        }

        private void ToggleReliefAnalysis()
        {
            TerrainReliefService service = TerrainLabRuntime.Instance?.Relief;
            if (service != null && service.IsRunning)
            {
                CancelRelief();
                return;
            }

            RunRelief();
        }

        private void CancelRelief()
        {
            TerrainLabRuntime.Instance?.Relief.Cancel();
            SetStatus(LM.Get("terrain_lab_relief_cancelling"), false);
        }

        private void ShowElevationOverlay()
        {
            ClearPendingOverlayRequest();
            if (_elevationOverlay != null && _elevationOverlay.IsVisible)
            {
                _elevationOverlay.Clear();
                SetStatus(LM.Get("terrain_lab_elevation_overlay_hidden"), false);
                return;
            }

            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            if (_elevationOverlay == null || state == null)
            {
                SetError(LM.Get("terrain_lab_no_project_state"));
                return;
            }

            _reliefOverlay?.Clear();
            _hydrologyOverlay?.Clear();
            _erosionOverlay?.Clear();
            _dataOverlay?.Clear();
            _elevationOverlay.Show(state);
            if (!_elevationOverlay.IsVisible)
            {
                SetError(LM.Get("terrain_lab_elevation_overlay_empty"));
                return;
            }

            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_selected_format"),
                    LM.Get("terrain_lab_elevation_overlay")),
                false,
                true);
        }

        private void ShowDataOverlay(TerrainDataOverlayMode mode)
        {
            ClearPendingOverlayRequest();
            if (_dataOverlay != null && _dataOverlay.Mode == mode)
            {
                _dataOverlay.Clear();
                SetStatus(LM.Get("terrain_lab_data_overlay_hidden"), false);
                return;
            }

            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            if (_dataOverlay == null || state == null)
            {
                SetError(LM.Get("terrain_lab_no_project_state"));
                return;
            }

            _elevationOverlay?.Clear();
            _reliefOverlay?.Clear();
            _hydrologyOverlay?.Clear();
            _erosionOverlay?.Clear();
            _dataOverlay.Show(mode, state);
            if (!_dataOverlay.IsVisible)
            {
                SetError(LM.Get("terrain_lab_data_overlay_empty"));
                return;
            }

            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_selected_format"),
                    LM.Get(GetDataOverlayLocalizationKey(mode))),
                false,
                true);
        }

        private void ShowReliefOverlay(TerrainReliefOverlayMode mode)
        {
            if ((_reliefOverlay != null && _reliefOverlay.Mode == mode) ||
                (_pendingOverlayKind == PendingOverlayKind.Relief &&
                 _pendingReliefOverlay == mode))
            {
                HideReliefOverlay();
                return;
            }

            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            TerrainReliefResult result = state?.Relief;
            if (_reliefOverlay == null || state == null)
            {
                SetError(LM.Get("terrain_lab_no_project_state"));
                return;
            }

            if (result == null || !result.IsCurrent(state))
            {
                QueueReliefOverlay(mode);
                return;
            }

            ClearPendingOverlayRequest();
            DisplayReliefOverlay(mode, state, result);
        }

        private void DisplayReliefOverlay(
            TerrainReliefOverlayMode mode,
            TerrainWorldState state,
            TerrainReliefResult result)
        {
            _elevationOverlay?.Clear();
            _dataOverlay?.Clear();
            _hydrologyOverlay?.Clear();
            _erosionOverlay?.Clear();
            _reliefOverlay.Show(mode, state, result);
            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_selected_format"),
                    LM.Get(GetReliefOverlayLocalizationKey(mode))),
                false,
                true);
        }

        private void HideReliefOverlay()
        {
            if (_pendingOverlayKind == PendingOverlayKind.Relief)
            {
                ClearPendingOverlayRequest();
            }

            _reliefOverlay?.Clear();
            SetStatus(LM.Get("terrain_lab_relief_overlay_hidden"), false);
        }

        private void BuildElevationEditor()
        {
            CreateSectionHeading(_moduleContent, "terrain_lab_dem_editor_heading");
            if (_editor == null)
            {
                CreateInfo("terrain_lab_runtime_unavailable", ErrorText, 34f);
                return;
            }

            Transform firstToolRow = CreateActionRow(
                _moduleContent,
                "TerrainLabElevationToolsPrimary");
            CreateEditorToolButton(
                firstToolRow,
                TerrainEditorTool.Inspect,
                "terrain_lab_tool_inspect",
                94f,
                "identify");
            CreateEditorToolButton(
                firstToolRow,
                TerrainEditorTool.SetElevation,
                "terrain_lab_tool_set",
                94f,
                "layer_style");

            Transform secondToolRow = CreateActionRow(
                _moduleContent,
                "TerrainLabElevationToolsDelta");
            CreateEditorToolButton(
                secondToolRow,
                TerrainEditorTool.RaiseElevation,
                "terrain_lab_tool_raise",
                94f,
                "elevation_raise");
            CreateEditorToolButton(
                secondToolRow,
                TerrainEditorTool.LowerElevation,
                "terrain_lab_tool_lower",
                94f,
                "elevation_raise",
                180f);

            Transform thirdToolRow = CreateActionRow(
                _moduleContent,
                "TerrainLabElevationToolsShape");
            CreateEditorToolButton(
                thirdToolRow,
                TerrainEditorTool.SmoothElevation,
                "terrain_lab_tool_smooth",
                94f,
                "layer_filter");
            CreateEditorToolButton(
                thirdToolRow,
                TerrainEditorTool.RampElevation,
                "terrain_lab_tool_ramp",
                94f,
                "layer_add_vector");

            CreateNumericInputRow(
                "TerrainLabElevationTarget",
                "terrain_lab_elevation_value",
                _editor.TargetElevation.ToString(),
                HandleTargetElevationChanged);
            CreateNumericInputRow(
                "TerrainLabElevationStep",
                "terrain_lab_elevation_step",
                _editor.Step.ToString(),
                HandleElevationStepChanged);

            Transform radiusRow = CreateActionRow(
                _moduleContent,
                "TerrainLabElevationRadius");
            CreateTextLabel(
                radiusRow,
                "TerrainLabElevationRadiusLabel",
                LM.Get("terrain_lab_brush_radius"),
                10f,
                FontStyle.Normal,
                28f,
                TextAnchor.MiddleLeft,
                NeutralText,
                78f);
            CreateActionButton(
                radiusRow,
                "-",
                DecreaseBrushRadius,
                34f,
                26f,
                null,
                "terrain_lab_action_radius_decrease",
                "terrain_lab_action_radius_decrease_description");
            _brushRadiusText = CreateTextLabel(
                radiusRow,
                "TerrainLabElevationRadiusValue",
                _editor.BrushRadius.ToString(),
                11f,
                FontStyle.Bold,
                28f,
                TextAnchor.MiddleCenter,
                Color.white,
                40f);
            CreateActionButton(
                radiusRow,
                "+",
                IncreaseBrushRadius,
                34f,
                26f,
                null,
                "terrain_lab_action_radius_increase",
                "terrain_lab_action_radius_increase_description");

            Transform historyRow = CreateActionRow(
                _moduleContent,
                "TerrainLabElevationHistory");
            CreateActionButton(
                historyRow,
                LM.Get("terrain_lab_action_undo"),
                UndoElevationEdit,
                94f,
                28f,
                "undo",
                "terrain_lab_action_undo",
                "terrain_lab_action_undo_description");
            CreateActionButton(
                historyRow,
                LM.Get("terrain_lab_action_redo"),
                RedoElevationEdit,
                94f,
                28f,
                "redo",
                "terrain_lab_action_redo",
                "terrain_lab_action_redo_description");
        }

        private void BuildHydrologyView()
        {
            CreateSectionHeading(_moduleContent, "terrain_lab_hydrology_heading");
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWorldState state = runtime?.State;
            TerrainHydrologyModule module = runtime?.Hydrology;
            if (state == null || module == null)
            {
                CreateInfo("terrain_lab_no_project_state", ErrorText, 34f);
                return;
            }

            EnsureHydrologyThreshold(state);

            if (module.IsRunning)
            {
                _hydrologyProgressText = CreateTextLabel(
                    _moduleContent,
                    "TerrainLabHydrologyProgress",
                    string.Format(
                        LM.Get("terrain_lab_hydrology_progress_format"),
                        module.ProgressPercent),
                    11f,
                    FontStyle.Bold,
                    30f,
                    TextAnchor.MiddleCenter,
                    WarningText);
                CreateActionButton(
                    _moduleContent,
                    LM.Get("terrain_lab_hydrology_cancel"),
                    CancelHydrology,
                    194f,
                    28f,
                    "ui/Icons/iconPause",
                    "terrain_lab_hydrology_cancel",
                    "terrain_lab_hydrology_cancel_description");
                return;
            }

            if (!string.IsNullOrWhiteSpace(module.LastError))
            {
                CreateInfoText(module.LastError, WarningText, 42f);
            }

            TerrainHydrologyResult result = state.Hydrology;
            bool resultCurrent = result != null && result.IsCurrent(state);
            if (result == null)
            {
                CreateInfo("terrain_lab_hydrology_not_computed", NeutralText, 34f);
            }
            else if (!resultCurrent)
            {
                CreateInfo("terrain_lab_hydrology_stale", WarningText, 42f);
            }
            else
            {
                _hydrologyThreshold = result.StreamThreshold;
                CreateInfo("terrain_lab_hydrology_algorithm", SuccessText, 30f);
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_hydrology_fill_stats_format"),
                        result.Statistics.FilledCellCount,
                        result.Statistics.MaximumFillDepth),
                    NeutralText,
                    30f);
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_hydrology_flow_stats_format"),
                        result.Statistics.OutletCellCount,
                        result.Statistics.MaximumAccumulation),
                    NeutralText,
                    30f);
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_hydrology_stream_stats_format"),
                        result.Statistics.StreamCellCount),
                    NeutralText,
                    26f);
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_hydrology_basin_stats_format"),
                        result.Statistics.WatershedCount,
                        result.Statistics.MaximumStreamOrder),
                    NeutralText,
                    26f);
            }

            CreateNumericInputRow(
                "TerrainLabHydrologyThreshold",
                "terrain_lab_hydrology_threshold",
                _hydrologyThreshold.ToString(),
                HandleHydrologyThresholdChanged,
                8);
            CreateActionButton(
                _moduleContent,
                LM.Get(resultCurrent
                    ? "terrain_lab_hydrology_recompute"
                    : "terrain_lab_hydrology_run"),
                RunHydrology,
                194f,
                28f,
                "ui/Icons/iconPlay",
                resultCurrent
                    ? "terrain_lab_hydrology_recompute"
                    : "terrain_lab_hydrology_run",
                "terrain_lab_hydrology_run_description");

            CreateSectionHeading(_moduleContent, "terrain_lab_hydrology_overlays_heading");
            Transform sourceOverlayRow = CreateActionRow(
                _moduleContent,
                "TerrainLabHydrologyOverlaysSource");
            CreateActionButton(
                sourceOverlayRow,
                LM.Get("terrain_lab_hydrology_overlay_filled_elevation"),
                delegate
                {
                    ShowHydrologyOverlay(TerrainHydrologyOverlayMode.FilledElevation);
                },
                94f,
                28f,
                "layer_add_raster",
                "terrain_lab_hydrology_overlay_filled_elevation",
                "terrain_lab_hydrology_overlay_filled_elevation_description");
            CreateActionButton(
                sourceOverlayRow,
                LM.Get("terrain_lab_hydrology_overlay_flow_direction"),
                delegate
                {
                    ShowHydrologyOverlay(TerrainHydrologyOverlayMode.FlowDirection);
                },
                94f,
                28f,
                "visibility_on",
                "terrain_lab_hydrology_overlay_flow_direction",
                "terrain_lab_hydrology_overlay_flow_direction_description");
            Transform firstOverlayRow = CreateActionRow(
                _moduleContent,
                "TerrainLabHydrologyOverlaysPrimary");
            CreateActionButton(
                firstOverlayRow,
                LM.Get("terrain_lab_hydrology_overlay_streams"),
                delegate { ShowHydrologyOverlay(TerrainHydrologyOverlayMode.Streams); },
                94f,
                28f,
                "visibility_on",
                "terrain_lab_hydrology_overlay_streams",
                "terrain_lab_hydrology_overlay_streams_description");
            CreateActionButton(
                firstOverlayRow,
                LM.Get("terrain_lab_hydrology_overlay_accumulation"),
                delegate { ShowHydrologyOverlay(TerrainHydrologyOverlayMode.Accumulation); },
                94f,
                28f,
                "layer_filter",
                "terrain_lab_hydrology_overlay_accumulation",
                "terrain_lab_hydrology_overlay_accumulation_description");

            Transform secondOverlayRow = CreateActionRow(
                _moduleContent,
                "TerrainLabHydrologyOverlaysSecondary");
            CreateActionButton(
                secondOverlayRow,
                LM.Get("terrain_lab_hydrology_overlay_fill"),
                delegate { ShowHydrologyOverlay(TerrainHydrologyOverlayMode.FillDepth); },
                94f,
                28f,
                "layer_add_raster",
                "terrain_lab_hydrology_overlay_fill",
                "terrain_lab_hydrology_overlay_fill_description");
            CreateActionButton(
                secondOverlayRow,
                LM.Get("terrain_lab_hydrology_overlay_watersheds"),
                delegate { ShowHydrologyOverlay(TerrainHydrologyOverlayMode.Watersheds); },
                94f,
                28f,
                "layer_group",
                "terrain_lab_hydrology_overlay_watersheds",
                "terrain_lab_hydrology_overlay_watersheds_description");

            Transform thirdOverlayRow = CreateActionRow(
                _moduleContent,
                "TerrainLabHydrologyOverlaysAdvanced");
            CreateActionButton(
                thirdOverlayRow,
                LM.Get("terrain_lab_hydrology_overlay_stream_order"),
                delegate { ShowHydrologyOverlay(TerrainHydrologyOverlayMode.StreamOrder); },
                94f,
                28f,
                "layer_stack",
                "terrain_lab_hydrology_overlay_stream_order",
                "terrain_lab_hydrology_overlay_stream_order_description");
            CreateActionButton(
                thirdOverlayRow,
                LM.Get("terrain_lab_hydrology_overlay_hide"),
                HideHydrologyOverlay,
                94f,
                28f,
                "visibility_off",
                "terrain_lab_hydrology_overlay_hide",
                "terrain_lab_hydrology_overlay_hide_description");
        }

        private void RunHydrology()
        {
            ClearPendingOverlayRequest();
            StartHydrologyAnalysis();
        }

        private bool StartHydrologyAnalysis()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWorldState state = runtime?.State;
            if (state == null)
            {
                SetError(LM.Get("terrain_lab_no_project_state"));
                return false;
            }

            EnsureHydrologyThreshold(state);
            string error = null;
            if (runtime != null && runtime.TryStartHydrologyAnalysis(
                _hydrologyThreshold,
                out error))
            {
                _runningJobId = "hydrology";
                _jobsRunningLastFrame = true;
                HideAllOverlays();
                RebuildModuleContent();
                SetStatus(LM.Get("terrain_lab_hydrology_started"), false, true);
                return true;
            }

            SetError(error ?? LM.Get("terrain_lab_runtime_unavailable"));
            return false;
        }

        private void ToggleHydrologyAnalysis()
        {
            TerrainHydrologyModule module = TerrainLabRuntime.Instance?.Hydrology;
            if (module != null && module.IsRunning)
            {
                CancelHydrology();
                return;
            }

            RunHydrology();
        }

        private void ToggleWaterDynamics()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWorldState state = runtime?.State;
            TerrainWaterDynamicsService service = runtime?.WaterDynamics;
            if (state == null || service == null)
            {
                SetError(LM.Get("terrain_lab_no_project_state"));
                return;
            }

            bool enable = !service.Enabled;
            if (!service.TrySetEnabled(state, enable, out string error))
            {
                SetError(error);
                return;
            }

            UpdateToolbarState();
            RebuildModuleContent();
            SetStatus(
                LM.Get(enable
                    ? "terrain_lab_water_dynamics_started"
                    : "terrain_lab_water_dynamics_stopped"),
                false,
                enable);
        }

        private void HandleWaterMaximumFloodChanged(string value)
        {
            TryUpdateWaterParameter(
                value,
                1,
                TerrainWaterParameters.HardMaximumFloodPercent,
                (parameters, parsed) => parameters.MaximumFloodPercent = parsed);
        }

        private void HandleWaterInitialVolumeChanged(string value)
        {
            TryUpdateWaterParameter(
                value,
                1,
                4096,
                (parameters, parsed) => parameters.InitialSourceVolume = parsed);
        }

        private void HandleWaterGeyserVolumeChanged(string value)
        {
            TryUpdateWaterParameter(
                value,
                1,
                1024,
                (parameters, parsed) => parameters.GeyserPulseVolume = parsed);
        }

        private void HandleWaterCellsPerTickChanged(string value)
        {
            TryUpdateWaterParameter(
                value,
                1,
                512,
                (parameters, parsed) => parameters.CellsPerTick = parsed);
        }

        private void HandleWaterEvaporationChanged(string value)
        {
            TryUpdateWaterParameter(
                value,
                0,
                16,
                (parameters, parsed) =>
                    parameters.EvaporationPerClimateStep = parsed);
        }

        private void CreateWaterRoutingButton(
            Transform parent,
            TerrainWaterRoutingAlgorithm selected,
            TerrainWaterRoutingAlgorithm algorithm,
            string localizationKey)
        {
            SimpleButton button = CreateActionButton(
                parent,
                LM.Get(localizationKey),
                delegate { SelectWaterRoutingAlgorithm(algorithm); },
                64f,
                28f,
                null,
                localizationKey,
                localizationKey + "_description");
            button.Background.color = selected == algorithm
                ? Color.white
                : InactiveButton;
        }

        private void SelectWaterRoutingAlgorithm(
            TerrainWaterRoutingAlgorithm algorithm)
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWorldState state = runtime?.State;
            TerrainWaterDynamicsService service = runtime?.WaterDynamics;
            if (state == null || service == null)
            {
                SetError(LM.Get("terrain_lab_no_project_state"));
                return;
            }

            TerrainWaterParameters source = state.WaterDynamics?.Parameters ??
                new TerrainWaterParameters();
            TerrainWaterRoutingAlgorithm normalized =
                TerrainWaterRoutingAlgorithms.Normalize(algorithm);
            if (source.RoutingAlgorithm == normalized)
            {
                return;
            }

            TerrainWaterParameters updated = CopyWaterParameters(source);
            updated.RoutingAlgorithm = normalized;
            if (!service.TryUpdateParameters(state, updated, out string error))
            {
                SetError(error);
                return;
            }

            UpdateToolbarState();
            RebuildModuleContent();
            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_water_routing_algorithm_updated_format"),
                    LM.Get(GetWaterRoutingLocalizationKey(normalized))),
                false,
                true);
        }

        private void TryUpdateWaterParameter(
            string value,
            int minimum,
            int maximum,
            Action<TerrainWaterParameters, int> setter)
        {
            if (!int.TryParse(value, out int parsed) ||
                parsed < minimum || parsed > maximum)
            {
                SetError(string.Format(
                    LM.Get("terrain_lab_water_parameter_error"),
                    minimum,
                    maximum));
                return;
            }

            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWorldState state = runtime?.State;
            TerrainWaterDynamicsService service = runtime?.WaterDynamics;
            if (state == null || service == null)
            {
                SetError(LM.Get("terrain_lab_no_project_state"));
                return;
            }

            TerrainWaterParameters source = state.WaterDynamics?.Parameters ??
                new TerrainWaterParameters();
            TerrainWaterParameters updated = CopyWaterParameters(source);
            setter(updated, parsed);
            if (!service.TryUpdateParameters(state, updated, out string error))
            {
                SetError(error);
                return;
            }

            UpdateToolbarState();
            SetStatus(LM.Get("terrain_lab_water_parameters_updated"), false, true);
        }

        private static TerrainWaterParameters CopyWaterParameters(
            TerrainWaterParameters source)
        {
            source = source ?? new TerrainWaterParameters();
            return new TerrainWaterParameters
            {
                MaximumFloodPercent = source.MaximumFloodPercent,
                InitialSourceVolume = source.InitialSourceVolume,
                GeyserPulseVolume = source.GeyserPulseVolume,
                CellsPerTick = source.CellsPerTick,
                EvaporationPerClimateStep = source.EvaporationPerClimateStep,
                RoutingAlgorithm = source.RoutingAlgorithm
            };
        }

        private static string GetWaterRoutingLocalizationKey(
            TerrainWaterRoutingAlgorithm algorithm)
        {
            switch (TerrainWaterRoutingAlgorithms.Normalize(algorithm))
            {
                case TerrainWaterRoutingAlgorithm.DInfinity:
                    return "terrain_lab_water_routing_dinf";
                case TerrainWaterRoutingAlgorithm.MultipleFlowDirection:
                    return "terrain_lab_water_routing_mfd";
                default:
                    return "terrain_lab_water_routing_d8";
            }
        }

        private void EnsureHydrologyThreshold(TerrainWorldState state)
        {
            if (state == null)
            {
                return;
            }

            if (!string.Equals(
                _hydrologyParameterProjectId,
                state.ProjectId,
                StringComparison.Ordinal))
            {
                TerrainHydrologyResult result = state.Hydrology;
                _hydrologyThreshold = result != null && result.IsCurrent(state)
                    ? result.StreamThreshold
                    : TerrainHydrologyAnalyzer.GetDefaultStreamThreshold(state.CellCount);
                _hydrologyParameterProjectId = state.ProjectId;
            }
            else if (_hydrologyThreshold < 1 || _hydrologyThreshold > state.CellCount)
            {
                _hydrologyThreshold = TerrainHydrologyAnalyzer.GetDefaultStreamThreshold(
                    state.CellCount);
            }
        }

        private void CancelHydrology()
        {
            TerrainLabRuntime.Instance?.Hydrology.Cancel();
            SetStatus(LM.Get("terrain_lab_hydrology_cancelling"), false);
        }

        private void HandleHydrologyThresholdChanged(string value)
        {
            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            if (state == null || !int.TryParse(value, out int threshold) ||
                threshold < 1 || threshold > state.CellCount)
            {
                SetError(LM.Get("terrain_lab_hydrology_threshold_error"));
                return;
            }

            _hydrologyThreshold = threshold;
            TerrainHydrologyResult result = state.Hydrology;
            if (result == null || !result.IsCurrent(state))
            {
                SetStatus(LM.Get("terrain_lab_status_ready"), false);
                return;
            }

            TerrainHydrologyModule module = TerrainLabRuntime.Instance.Hydrology;
            if (!module.TrySetStreamThreshold(state, threshold, out string error))
            {
                SetError(error);
                return;
            }

            if (_hydrologyOverlay != null &&
                (_hydrologyOverlay.Mode == TerrainHydrologyOverlayMode.Streams ||
                 _hydrologyOverlay.Mode == TerrainHydrologyOverlayMode.StreamOrder))
            {
                _hydrologyOverlay.Show(
                    _hydrologyOverlay.Mode,
                    state,
                    result);
            }

            SetStatus(LM.Get("terrain_lab_hydrology_threshold_updated"), false, true);
        }

        private void ShowHydrologyOverlay(TerrainHydrologyOverlayMode mode)
        {
            if ((_hydrologyOverlay != null && _hydrologyOverlay.Mode == mode) ||
                (_pendingOverlayKind == PendingOverlayKind.Hydrology &&
                 _pendingHydrologyOverlay == mode))
            {
                HideHydrologyOverlay();
                return;
            }

            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            TerrainHydrologyResult result = state?.Hydrology;
            if (_hydrologyOverlay == null || state == null)
            {
                SetError(LM.Get("terrain_lab_no_project_state"));
                return;
            }

            if (result == null || !result.IsCurrent(state))
            {
                QueueHydrologyOverlay(mode);
                return;
            }

            ClearPendingOverlayRequest();
            DisplayHydrologyOverlay(mode, state, result);
        }

        private void DisplayHydrologyOverlay(
            TerrainHydrologyOverlayMode mode,
            TerrainWorldState state,
            TerrainHydrologyResult result)
        {
            _elevationOverlay?.Clear();
            _dataOverlay?.Clear();
            _reliefOverlay?.Clear();
            _erosionOverlay?.Clear();
            _hydrologyOverlay.Show(mode, state, result);
            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_selected_format"),
                    LM.Get(GetHydrologyOverlayLocalizationKey(mode))),
                false,
                true);
        }

        private void HideHydrologyOverlay()
        {
            if (_pendingOverlayKind == PendingOverlayKind.Hydrology)
            {
                ClearPendingOverlayRequest();
            }

            _hydrologyOverlay?.Clear();
            SetStatus(LM.Get("terrain_lab_hydrology_overlay_hidden"), false);
        }

        private void BuildErosionView()
        {
            CreateSectionHeading(_moduleContent, "terrain_lab_erosion_heading");
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWorldState state = runtime?.State;
            TerrainErosionModule module = runtime?.Erosion;
            if (state == null || module == null)
            {
                CreateInfo("terrain_lab_no_project_state", ErrorText, 34f);
                return;
            }

            TerrainErosionResult result = state.Erosion;
            EnsureErosionParameters(state);

            if (module.IsRunning)
            {
                _erosionProgressText = CreateTextLabel(
                    _moduleContent,
                    "TerrainLabErosionProgress",
                    string.Format(
                        LM.Get("terrain_lab_erosion_progress_format"),
                        module.ProgressPercent),
                    11f,
                    FontStyle.Bold,
                    30f,
                    TextAnchor.MiddleCenter,
                    WarningText);
                CreateActionButton(
                    _moduleContent,
                    LM.Get("terrain_lab_erosion_cancel"),
                    CancelErosion,
                    194f,
                    28f,
                    "ui/Icons/iconPause",
                    "terrain_lab_erosion_cancel",
                    "terrain_lab_erosion_cancel_description");
                return;
            }

            if (!string.IsNullOrWhiteSpace(module.LastError))
            {
                CreateInfoText(module.LastError, WarningText, 42f);
            }

            TerrainHydrologyResult hydrology = state.Hydrology;
            bool hydrologyCurrent = hydrology != null && hydrology.IsCurrent(state);
            if (!hydrologyCurrent)
            {
                CreateInfo("terrain_lab_erosion_requires_hydrology", WarningText, 42f);
            }

            bool resultCurrent = result != null && result.IsCurrent(state);
            if (result == null)
            {
                CreateInfo("terrain_lab_erosion_not_computed", NeutralText, 34f);
            }
            else if (!resultCurrent)
            {
                CreateInfo("terrain_lab_erosion_stale", WarningText, 42f);
            }
            else
            {
                CreateInfo("terrain_lab_erosion_algorithm", SuccessText, 30f);
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_erosion_change_stats_format"),
                        result.Statistics.ChangedCellCount,
                        result.Statistics.MaximumCut,
                        result.Statistics.MaximumFill),
                    NeutralText,
                    30f);
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_erosion_transport_stats_format"),
                        result.Statistics.HydraulicTransport,
                        result.Statistics.ThermalTransport),
                    NeutralText,
                    30f);
                CreateInfoText(
                    string.Format(
                        LM.Get("terrain_lab_erosion_mass_balance_format"),
                        result.Statistics.MassBalance),
                    result.Statistics.MassBalance == 0 ? SuccessText : ErrorText,
                    26f);
            }

            CreateSectionHeading(_moduleContent, "terrain_lab_erosion_parameters_heading");
            CreateNumericInputRow(
                "TerrainLabErosionIterations",
                "terrain_lab_erosion_iterations",
                _erosionIterations.ToString(),
                HandleErosionIterationsChanged,
                3);
            CreateNumericInputRow(
                "TerrainLabErosionFlowStrength",
                "terrain_lab_erosion_flow_strength",
                _erosionFlowStrength.ToString(),
                HandleErosionFlowStrengthChanged,
                3);
            CreateNumericInputRow(
                "TerrainLabErosionThermalStrength",
                "terrain_lab_erosion_thermal_strength",
                _erosionThermalStrength.ToString(),
                HandleErosionThermalStrengthChanged,
                3);
            CreateNumericInputRow(
                "TerrainLabErosionTalus",
                "terrain_lab_erosion_talus",
                _erosionTalusThreshold.ToString(),
                HandleErosionTalusChanged,
                5);
            CreateActionButton(
                _moduleContent,
                LM.Get(resultCurrent
                    ? "terrain_lab_erosion_recompute"
                    : "terrain_lab_erosion_run"),
                RunErosion,
                194f,
                28f,
                "ui/Icons/iconPlay",
                resultCurrent
                    ? "terrain_lab_erosion_recompute"
                    : "terrain_lab_erosion_run",
                "terrain_lab_erosion_run_description");

            CreateSectionHeading(_moduleContent, "terrain_lab_erosion_overlays_heading");
            Transform firstRow = CreateActionRow(
                _moduleContent,
                "TerrainLabErosionOverlaysPrimary");
            CreateActionButton(
                firstRow,
                LM.Get("terrain_lab_erosion_overlay_net"),
                delegate { ShowErosionOverlay(TerrainErosionOverlayMode.NetChange); },
                94f,
                28f,
                "visibility_on",
                "terrain_lab_erosion_overlay_net",
                "terrain_lab_erosion_overlay_net_description");
            CreateActionButton(
                firstRow,
                LM.Get("terrain_lab_erosion_overlay_cut"),
                delegate { ShowErosionOverlay(TerrainErosionOverlayMode.Erosion); },
                94f,
                28f,
                "elevation_raise",
                "terrain_lab_erosion_overlay_cut",
                "terrain_lab_erosion_overlay_cut_description",
                180f);
            Transform secondRow = CreateActionRow(
                _moduleContent,
                "TerrainLabErosionOverlaysSecondary");
            CreateActionButton(
                secondRow,
                LM.Get("terrain_lab_erosion_overlay_fill"),
                delegate { ShowErosionOverlay(TerrainErosionOverlayMode.Deposition); },
                94f,
                28f,
                "elevation_raise",
                "terrain_lab_erosion_overlay_fill",
                "terrain_lab_erosion_overlay_fill_description");
            CreateActionButton(
                secondRow,
                LM.Get("terrain_lab_erosion_overlay_result"),
                delegate { ShowErosionOverlay(TerrainErosionOverlayMode.ResultElevation); },
                94f,
                28f,
                "layer_properties",
                "terrain_lab_erosion_overlay_result",
                "terrain_lab_erosion_overlay_result_description");
            Transform actionRow = CreateActionRow(
                _moduleContent,
                "TerrainLabErosionResultActions");
            SimpleButton applyButton = CreateActionButton(
                actionRow,
                LM.Get("terrain_lab_erosion_apply"),
                ApplyErosion,
                94f,
                28f,
                "project_validate",
                "terrain_lab_erosion_apply",
                "terrain_lab_erosion_apply_description");
            bool canApply = resultCurrent && result.Statistics.MassBalance == 0;
            applyButton.Button.interactable = canApply;
            applyButton.Background.color = canApply ? Color.white : InactiveButton;
            CreateActionButton(
                actionRow,
                LM.Get("terrain_lab_erosion_overlay_hide"),
                HideErosionOverlay,
                94f,
                28f,
                "visibility_off",
                "terrain_lab_erosion_overlay_hide",
                "terrain_lab_erosion_overlay_hide_description");
        }

        private void RunErosion()
        {
            ClearPendingOverlayRequest();
            StartErosionAnalysis();
        }

        private bool StartErosionAnalysis()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWorldState state = runtime?.State;
            if (state == null)
            {
                SetError(LM.Get("terrain_lab_no_project_state"));
                return false;
            }

            EnsureErosionParameters(state);
            TerrainErosionParameters parameters = new TerrainErosionParameters
            {
                Iterations = _erosionIterations,
                FlowStrengthPercent = _erosionFlowStrength,
                ThermalStrengthPercent = _erosionThermalStrength,
                TalusThreshold = _erosionTalusThreshold
            };
            string error = null;
            if (runtime != null && runtime.TryStartErosionAnalysis(parameters, out error))
            {
                _runningJobId = "erosion";
                _jobsRunningLastFrame = true;
                HideAllOverlays();
                RebuildModuleContent();
                SetStatus(LM.Get("terrain_lab_erosion_started"), false, true);
                return true;
            }

            SetError(error ?? LM.Get("terrain_lab_runtime_unavailable"));
            return false;
        }

        private void ToggleErosionAnalysis()
        {
            TerrainErosionModule module = TerrainLabRuntime.Instance?.Erosion;
            if (module != null && module.IsRunning)
            {
                CancelErosion();
                return;
            }

            RunErosion();
        }

        private void EnsureErosionParameters(TerrainWorldState state)
        {
            if (state == null || string.Equals(
                _erosionParameterProjectId,
                state.ProjectId,
                StringComparison.Ordinal))
            {
                return;
            }

            TerrainErosionParameters source = state.Erosion?.Parameters ??
                new TerrainErosionParameters();
            _erosionIterations = source.Iterations;
            _erosionFlowStrength = source.FlowStrengthPercent;
            _erosionThermalStrength = source.ThermalStrengthPercent;
            _erosionTalusThreshold = source.TalusThreshold;
            _erosionParameterProjectId = state.ProjectId;
        }

        private void CancelErosion()
        {
            TerrainLabRuntime.Instance?.Erosion.Cancel();
            SetStatus(LM.Get("terrain_lab_erosion_cancelling"), false);
        }

        private void ApplyErosion()
        {
            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            TerrainErosionResult result = state?.Erosion;
            if (result == null || !result.IsCurrent(state))
            {
                SetError(LM.Get("terrain_lab_erosion_stale"));
                return;
            }

            if (result.Statistics.MassBalance != 0)
            {
                SetError(LM.Get("terrain_lab_erosion_mass_balance_error"));
                return;
            }

            TerrainElevationEdit edit = state.ApplyElevationGrid(result.ResultElevation);
            HideAllOverlays();
            _editor.RecordAppliedEdit(edit);
            RebuildModuleContent();
            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_erosion_applied_format"),
                    edit.ChangedCellCount),
                false,
                true);
        }

        private void ShowErosionOverlay(TerrainErosionOverlayMode mode)
        {
            if ((_erosionOverlay != null && _erosionOverlay.Mode == mode) ||
                (_pendingOverlayKind == PendingOverlayKind.Erosion &&
                 _pendingErosionOverlay == mode))
            {
                HideErosionOverlay();
                return;
            }

            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            TerrainErosionResult result = state?.Erosion;
            if (_erosionOverlay == null || state == null)
            {
                SetError(LM.Get("terrain_lab_no_project_state"));
                return;
            }

            if (result == null || !result.IsCurrent(state))
            {
                QueueErosionOverlay(mode);
                return;
            }

            ClearPendingOverlayRequest();
            DisplayErosionOverlay(mode, state, result);
        }

        private void DisplayErosionOverlay(
            TerrainErosionOverlayMode mode,
            TerrainWorldState state,
            TerrainErosionResult result)
        {
            _elevationOverlay?.Clear();
            _dataOverlay?.Clear();
            _reliefOverlay?.Clear();
            _hydrologyOverlay?.Clear();
            _erosionOverlay.Show(mode, state, result);
            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_selected_format"),
                    LM.Get(GetErosionOverlayLocalizationKey(mode))),
                false,
                true);
        }

        private void HideErosionOverlay()
        {
            if (_pendingOverlayKind == PendingOverlayKind.Erosion)
            {
                ClearPendingOverlayRequest();
            }

            _erosionOverlay?.Clear();
            SetStatus(LM.Get("terrain_lab_erosion_overlay_hidden"), false);
        }

        private void HandleErosionIterationsChanged(string value)
        {
            TrySetBoundedInt(
                value,
                1,
                100,
                parsed => _erosionIterations = parsed,
                "terrain_lab_erosion_iterations_error");
        }

        private void HandleErosionFlowStrengthChanged(string value)
        {
            TrySetBoundedInt(
                value,
                0,
                100,
                parsed => _erosionFlowStrength = parsed,
                "terrain_lab_erosion_percent_error");
        }

        private void HandleErosionThermalStrengthChanged(string value)
        {
            TrySetBoundedInt(
                value,
                0,
                100,
                parsed => _erosionThermalStrength = parsed,
                "terrain_lab_erosion_percent_error");
        }

        private void HandleErosionTalusChanged(string value)
        {
            TrySetBoundedInt(
                value,
                0,
                32767,
                parsed => _erosionTalusThreshold = parsed,
                "terrain_lab_erosion_talus_error");
        }

        private void TrySetBoundedInt(
            string value,
            int minimum,
            int maximum,
            Action<int> setter,
            string errorKey)
        {
            if (!int.TryParse(value, out int parsed) ||
                parsed < minimum || parsed > maximum)
            {
                SetError(LM.Get(errorKey));
                return;
            }

            setter(parsed);
            SetStatus(LM.Get("terrain_lab_status_ready"), false);
        }

        private void CreateEditorToolButton(
            Transform parent,
            TerrainEditorTool tool,
            string localizationKey,
            float width,
            string iconId = null,
            float iconRotation = 0f)
        {
            SimpleButton button = CreateActionButton(
                parent,
                LM.Get(localizationKey),
                delegate { SelectEditorTool(tool); },
                width,
                28f,
                iconId,
                localizationKey,
                GetEditorToolDescriptionLocalizationKey(tool),
                iconRotation);
            button.Background.color = _editor.Tool == tool
                ? Color.white
                : InactiveButton;
            _editorToolButtons[tool] = button;
        }

        private void CreateNumericInputRow(
            string objectName,
            string labelKey,
            string value,
            UnityEngine.Events.UnityAction<string> valueChanged,
            int characterLimit = 6)
        {
            Transform row = CreateActionRow(_moduleContent, objectName);
            CreateTextLabel(
                row,
                objectName + "Label",
                LM.Get(labelKey),
                10f,
                FontStyle.Normal,
                28f,
                TextAnchor.MiddleLeft,
                NeutralText,
                78f);

            TextInput input = TextInput.Instantiate(row, false);
            input.Setup(value, valueChanged);
            input.SetSize(new Vector2(120f, 26f));
            input.input.contentType = InputField.ContentType.IntegerNumber;
            input.input.characterLimit = characterLimit;
            input.text.resizeTextMinSize = 8;
            input.text.resizeTextMaxSize = 11;

            LayoutElement element = input.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = input.gameObject.AddComponent<LayoutElement>();
            }

            element.preferredWidth = 120f;
            element.preferredHeight = 26f;
        }

        private void SelectEditorTool(TerrainEditorTool tool)
        {
            if (_editor.Tool == tool)
            {
                _editor.SetTool(TerrainEditorTool.None);
                UpdateToolbarState();
                SetStatus(LM.Get("terrain_lab_status_tool_deselected"), false);
                return;
            }

            ToolbarSection section = GetToolbarSection(tool);
            if (_activeToolbarSection != section)
            {
                _activeToolbarSection = section;
                UpdateToolbarSectionVisibility();
                UpdateAdaptiveToolbarLayout(true);
            }

            _editor.SetTool(tool);
            UpdateToolbarState();
            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_selected_format"),
                    LM.Get(GetEditorToolLocalizationKey(tool))),
                false);
        }

        private void UpdateEditorToolSelection()
        {
            foreach (KeyValuePair<TerrainEditorTool, SimpleButton> pair in
                     _editorToolButtons)
            {
                pair.Value.Background.color = pair.Key == _editor.Tool
                    ? Color.white
                    : InactiveButton;
            }

            foreach (KeyValuePair<TerrainEditorTool, SimpleButton> pair in
                     _toolbarEditorButtons)
            {
                pair.Value.Background.color = pair.Value.Button.interactable
                    ? Color.white
                    : InactiveButton;
                SetToolbarActivity(
                    pair.Value,
                    pair.Key == _editor.Tool && pair.Value.Button.interactable,
                    ActivityGreen);
            }
        }

        private void HandleTargetElevationChanged(string value)
        {
            if (!_editor.TrySetTargetElevation(value, out _))
            {
                SetError(LM.Get("terrain_lab_error_elevation_value"));
                return;
            }

            SetStatus(LM.Get("terrain_lab_status_ready"), false);
        }

        private void HandleElevationStepChanged(string value)
        {
            if (!_editor.TrySetStep(value, out _))
            {
                SetError(LM.Get("terrain_lab_error_elevation_step"));
                return;
            }

            SetStatus(LM.Get("terrain_lab_status_ready"), false);
        }

        private void DecreaseBrushRadius()
        {
            _editor.SetBrushRadius(_editor.BrushRadius - 1);
            UpdateBrushRadiusText();
        }

        private void IncreaseBrushRadius()
        {
            _editor.SetBrushRadius(_editor.BrushRadius + 1);
            UpdateBrushRadiusText();
        }

        private void UpdateBrushRadiusText()
        {
            if (_brushRadiusText != null)
            {
                _brushRadiusText.text = _editor.BrushRadius.ToString();
            }
        }

        private void UndoElevationEdit()
        {
            if (!_editor.Undo(out int changedCells))
            {
                SetStatus(LM.Get("terrain_lab_status_nothing_to_undo"), false);
                return;
            }

            SetStatus(
                string.Format(LM.Get("terrain_lab_status_undo_format"), changedCells),
                false,
                true);
        }

        private void RedoElevationEdit()
        {
            if (!_editor.Redo(out int changedCells))
            {
                SetStatus(LM.Get("terrain_lab_status_nothing_to_redo"), false);
                return;
            }

            SetStatus(
                string.Format(LM.Get("terrain_lab_status_redo_format"), changedCells),
                false,
                true);
        }

        private void ApplyPolygonizedSelection()
        {
            _editor.ApplyPolygonizedSelection();
        }

        private void CancelDigitizing()
        {
            _editor.CancelDigitizing();
            SetStatus(LM.Get("terrain_lab_status_digitizing_cancelled"), false);
        }

        private void HandleElevationEditApplied(int changedCells)
        {
            if (changedCells > 0)
            {
                _reliefOverlay?.Clear();
                _hydrologyOverlay?.Clear();
                _erosionOverlay?.Clear();
                TerrainDataOverlayMode mode = _dataOverlay?.Mode ??
                    TerrainDataOverlayMode.None;
                if (mode == TerrainDataOverlayMode.Landform ||
                    mode == TerrainDataOverlayMode.Material)
                {
                    _dataOverlay.Refresh(TerrainLabRuntime.Instance?.State);
                }
            }

            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_status_cells_changed_format"),
                    changedCells),
                false,
                changedCells > 0);
        }

        private void HandleElevationChanged(TerrainElevationEdit edit)
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWorldState state = runtime?.State;
            _elevationOverlay?.UpdateCells(state, edit);
            _dataOverlay?.UpdateCells(state, edit?.Indices);
            if (runtime?.WaterDynamics != null && state != null && edit != null &&
                !runtime.WaterDynamics.TryReconcileWaterSurface(
                    state,
                    edit.Indices,
                    out string error))
            {
                SetError(error);
            }
        }

        private void HandleSurfaceSampled(TerrainSurfaceStamp stamp)
        {
            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_status_surface_sampled_format"),
                    stamp.DisplayName),
                false,
                true);
        }

        private void HandleEditorOperationFailed(string localizationKey, string detail)
        {
            string message = LM.Get(localizationKey);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                message = string.Format(message, detail);
            }

            SetError(message);
        }

        private void HandleRegionPolygonized(int cellCount)
        {
            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_status_polygonized_format"),
                    cellCount),
                false,
                true);
        }

        private void HandleRampStarted(short startElevation)
        {
            SetStatus(
                string.Format(
                    LM.Get("terrain_lab_status_ramp_started_format"),
                    startElevation,
                    _editor.TargetElevation),
                false,
                true);
        }

        private static string GetEditorToolLocalizationKey(TerrainEditorTool tool)
        {
            switch (tool)
            {
                case TerrainEditorTool.SetElevation:
                    return "terrain_lab_tool_set";
                case TerrainEditorTool.RaiseElevation:
                    return "terrain_lab_tool_raise";
                case TerrainEditorTool.LowerElevation:
                    return "terrain_lab_tool_lower";
                case TerrainEditorTool.SmoothElevation:
                    return "terrain_lab_tool_smooth";
                case TerrainEditorTool.RampElevation:
                    return "terrain_lab_tool_ramp";
                case TerrainEditorTool.SampleSurface:
                    return "terrain_lab_tool_surface_sample";
                case TerrainEditorTool.FloodFillSurface:
                    return "terrain_lab_tool_surface_fill";
                case TerrainEditorTool.DrawLineSurface:
                    return "terrain_lab_tool_surface_line";
                case TerrainEditorTool.DrawPolygonSurface:
                    return "terrain_lab_tool_surface_polygon";
                case TerrainEditorTool.DrawRectangleSurface:
                    return "terrain_lab_tool_surface_rectangle";
                case TerrainEditorTool.PolygonizeSurface:
                    return "terrain_lab_tool_polygonize";
                default:
                    return "terrain_lab_tool_inspect";
            }
        }

        private static string GetEditorToolDescriptionLocalizationKey(
            TerrainEditorTool tool)
        {
            return GetEditorToolLocalizationKey(tool) + "_description";
        }

        private static string GetToolbarSectionDescriptionLocalizationKey(
            ToolbarSection section)
        {
            switch (section)
            {
                case ToolbarSection.Project:
                    return "terrain_lab_section_project_description";
                case ToolbarSection.Terrain:
                    return "terrain_lab_section_terrain_description";
                case ToolbarSection.Digitizing:
                    return "terrain_lab_section_digitizing_description";
                case ToolbarSection.Analysis:
                    return "terrain_lab_section_analysis_description";
                case ToolbarSection.Layers:
                    return "terrain_lab_section_layers_description";
                default:
                    return "terrain_lab_toolbar_section_description";
            }
        }

        private static string GetDirectionToken(TerrainFlowDirection direction)
        {
            switch (direction)
            {
                case TerrainFlowDirection.East:
                    return "E";
                case TerrainFlowDirection.NorthEast:
                    return "NE";
                case TerrainFlowDirection.North:
                    return "N";
                case TerrainFlowDirection.NorthWest:
                    return "NW";
                case TerrainFlowDirection.West:
                    return "W";
                case TerrainFlowDirection.SouthWest:
                    return "SW";
                case TerrainFlowDirection.South:
                    return "S";
                case TerrainFlowDirection.SouthEast:
                    return "SE";
                default:
                    return "OUT";
            }
        }

        private static string GetDataOverlayLocalizationKey(
            TerrainDataOverlayMode mode)
        {
            switch (mode)
            {
                case TerrainDataOverlayMode.Landform:
                    return "terrain_lab_data_overlay_landform";
                case TerrainDataOverlayMode.Material:
                    return "terrain_lab_data_overlay_material";
                case TerrainDataOverlayMode.Contours:
                    return "terrain_lab_data_overlay_contours";
                case TerrainDataOverlayMode.ManagedWater:
                    return "terrain_lab_data_overlay_managed_water";
                case TerrainDataOverlayMode.WaterStorage:
                    return "terrain_lab_data_overlay_water_storage";
                case TerrainDataOverlayMode.HydroFeature:
                    return "terrain_lab_data_overlay_hydro_feature";
                case TerrainDataOverlayMode.Moisture:
                    return "terrain_lab_data_overlay_moisture";
                case TerrainDataOverlayMode.Erodibility:
                    return "terrain_lab_data_overlay_erodibility";
                case TerrainDataOverlayMode.LocalSlope:
                    return "terrain_lab_data_overlay_local_slope";
                case TerrainDataOverlayMode.LocalAspect:
                    return "terrain_lab_data_overlay_local_aspect";
                default:
                    return "terrain_lab_toolbar_hide_overlays";
            }
        }

        private static string GetDataOverlayCellValue(
            TerrainDataOverlayMode mode,
            TerrainWorldState state,
            WorldTile tile)
        {
            int index = tile.y * state.Width + tile.x;
            switch (mode)
            {
                case TerrainDataOverlayMode.Landform:
                    TerrainLandform landform = (TerrainLandform)state.Landform[index];
                    string landformToken = Enum.IsDefined(typeof(TerrainLandform), landform)
                        ? landform.ToString().ToLowerInvariant()
                        : "unknown";
                    return LM.Get("terrain_lab_landform_" + landformToken);
                case TerrainDataOverlayMode.Material:
                    TerrainMaterial material = (TerrainMaterial)state.Material[index];
                    string materialToken = Enum.IsDefined(typeof(TerrainMaterial), material)
                        ? material.ToString().ToLowerInvariant()
                        : "unknown";
                    return LM.Get("terrain_lab_material_" + materialToken);
                case TerrainDataOverlayMode.Contours:
                    return string.Format(
                        LM.Get("terrain_lab_contour_value_format"),
                        state.Elevation[index],
                        TerrainDataOverlay.ContourIntervalMetres);
                case TerrainDataOverlayMode.ManagedWater:
                    return state.WaterDynamics.ManagedMask[index] != 0
                        ? LM.Get("terrain_lab_value_yes")
                        : LM.Get("terrain_lab_value_no");
                case TerrainDataOverlayMode.WaterStorage:
                    return string.Format(
                        LM.Get("terrain_lab_water_storage_value_format"),
                        state.WaterDynamics.WaterStorage[index]);
                case TerrainDataOverlayMode.HydroFeature:
                    TerrainHydroFeature feature =
                        TerrainRiverValleyModel.NormalizeFeature(
                            state.WaterDynamics.HydroFeature[index]);
                    return LM.Get("terrain_lab_hydro_feature_" +
                                  feature.ToString().ToLowerInvariant());
                case TerrainDataOverlayMode.Moisture:
                    return string.Format(
                        LM.Get("terrain_lab_moisture_value_format"),
                        state.WaterDynamics.Moisture[index]);
                case TerrainDataOverlayMode.Erodibility:
                    return string.Format(
                        LM.Get("terrain_lab_erodibility_value_format"),
                        state.WaterDynamics.Erodibility[index]);
                case TerrainDataOverlayMode.LocalSlope:
                    return GetLocalAngleValue(
                        state.WaterDynamics.LocalSlope[index],
                        true);
                case TerrainDataOverlayMode.LocalAspect:
                    return GetLocalAngleValue(
                        state.WaterDynamics.LocalAspect[index],
                        false);
                default:
                    return string.Empty;
            }
        }

        private static string GetLocalAngleValue(byte value, bool slope)
        {
            double radians = slope
                ? TerrainRiverValleyModel.DecodeSlopeRadians(value)
                : TerrainRiverValleyModel.DecodeAspectRadians(value);
            if (double.IsNaN(radians))
            {
                return LM.Get("terrain_lab_value_nodata");
            }

            return string.Format(
                LM.Get("terrain_lab_local_angle_value_format"),
                radians,
                radians * 180.0 / Math.PI);
        }

        private static string GetHydrologyOverlayLocalizationKey(
            TerrainHydrologyOverlayMode mode)
        {
            switch (mode)
            {
                case TerrainHydrologyOverlayMode.Streams:
                    return "terrain_lab_hydrology_overlay_streams";
                case TerrainHydrologyOverlayMode.Accumulation:
                    return "terrain_lab_hydrology_overlay_accumulation";
                case TerrainHydrologyOverlayMode.FillDepth:
                    return "terrain_lab_hydrology_overlay_fill";
                case TerrainHydrologyOverlayMode.FilledElevation:
                    return "terrain_lab_hydrology_overlay_filled_elevation";
                case TerrainHydrologyOverlayMode.FlowDirection:
                    return "terrain_lab_hydrology_overlay_flow_direction";
                case TerrainHydrologyOverlayMode.Watersheds:
                    return "terrain_lab_hydrology_overlay_watersheds";
                case TerrainHydrologyOverlayMode.StreamOrder:
                    return "terrain_lab_hydrology_overlay_stream_order";
                default:
                    return "terrain_lab_hydrology_overlay_hide";
            }
        }

        private static string GetReliefOverlayLocalizationKey(
            TerrainReliefOverlayMode mode)
        {
            switch (mode)
            {
                case TerrainReliefOverlayMode.Hypsometry:
                    return "terrain_lab_relief_overlay_hypsometry";
                case TerrainReliefOverlayMode.Slope:
                    return "terrain_lab_relief_overlay_slope";
                case TerrainReliefOverlayMode.Aspect:
                    return "terrain_lab_relief_overlay_aspect";
                case TerrainReliefOverlayMode.Hillshade:
                    return "terrain_lab_relief_overlay_hillshade";
                case TerrainReliefOverlayMode.Ruggedness:
                    return "terrain_lab_relief_overlay_ruggedness";
                default:
                    return "terrain_lab_relief_overlay_hide";
            }
        }

        private static string GetErosionOverlayLocalizationKey(
            TerrainErosionOverlayMode mode)
        {
            switch (mode)
            {
                case TerrainErosionOverlayMode.NetChange:
                    return "terrain_lab_erosion_overlay_net";
                case TerrainErosionOverlayMode.Erosion:
                    return "terrain_lab_erosion_overlay_cut";
                case TerrainErosionOverlayMode.Deposition:
                    return "terrain_lab_erosion_overlay_fill";
                case TerrainErosionOverlayMode.ResultElevation:
                    return "terrain_lab_erosion_overlay_result";
                default:
                    return "terrain_lab_erosion_overlay_hide";
            }
        }

        private void QueueReliefOverlay(TerrainReliefOverlayMode mode)
        {
            ClearPendingOverlayRequest();
            _pendingOverlayKind = PendingOverlayKind.Relief;
            _pendingReliefOverlay = mode;
            TryResolvePendingOverlay();
        }

        private void QueueHydrologyOverlay(TerrainHydrologyOverlayMode mode)
        {
            ClearPendingOverlayRequest();
            _pendingOverlayKind = PendingOverlayKind.Hydrology;
            _pendingHydrologyOverlay = mode;
            TryResolvePendingOverlay();
        }

        private void QueueErosionOverlay(TerrainErosionOverlayMode mode)
        {
            ClearPendingOverlayRequest();
            _pendingOverlayKind = PendingOverlayKind.Erosion;
            _pendingErosionOverlay = mode;
            TryResolvePendingOverlay();
        }

        private void TryResolvePendingOverlay()
        {
            if (_pendingOverlayKind == PendingOverlayKind.None)
            {
                return;
            }

            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            TerrainWorldState state = runtime?.State;
            if (runtime == null || state == null)
            {
                ClearPendingOverlayRequest();
                SetError(LM.Get("terrain_lab_no_project_state"));
                return;
            }

            if (!string.IsNullOrEmpty(_pendingOverlayJobId))
            {
                if (IsJobRunning(_pendingOverlayJobId))
                {
                    return;
                }

                if (!HasCurrentJobResult(_pendingOverlayJobId, state))
                {
                    string jobId = _pendingOverlayJobId;
                    string error = GetLastJobError(jobId);
                    ClearPendingOverlayRequest();
                    SetError(string.IsNullOrWhiteSpace(error)
                        ? GetStaleResultMessage(jobId)
                        : error);
                    return;
                }

                _pendingOverlayJobId = null;
            }

            if (IsAnalysisRunning())
            {
                return;
            }

            switch (_pendingOverlayKind)
            {
                case PendingOverlayKind.Relief:
                    if (state.Relief != null && state.Relief.IsCurrent(state))
                    {
                        TerrainReliefOverlayMode mode = _pendingReliefOverlay;
                        ClearPendingOverlayRequest();
                        DisplayReliefOverlay(mode, state, state.Relief);
                    }
                    else if (StartReliefAnalysis())
                    {
                        _pendingOverlayJobId = "relief";
                    }
                    else
                    {
                        ClearPendingOverlayRequest();
                    }

                    break;
                case PendingOverlayKind.Hydrology:
                    if (state.Hydrology != null && state.Hydrology.IsCurrent(state))
                    {
                        TerrainHydrologyOverlayMode mode = _pendingHydrologyOverlay;
                        ClearPendingOverlayRequest();
                        DisplayHydrologyOverlay(mode, state, state.Hydrology);
                    }
                    else if (StartHydrologyAnalysis())
                    {
                        _pendingOverlayJobId = "hydrology";
                    }
                    else
                    {
                        ClearPendingOverlayRequest();
                    }

                    break;
                case PendingOverlayKind.Erosion:
                    if (state.Erosion != null && state.Erosion.IsCurrent(state))
                    {
                        TerrainErosionOverlayMode mode = _pendingErosionOverlay;
                        ClearPendingOverlayRequest();
                        DisplayErosionOverlay(mode, state, state.Erosion);
                    }
                    else if (state.Hydrology == null ||
                             !state.Hydrology.IsCurrent(state))
                    {
                        if (StartHydrologyAnalysis())
                        {
                            _pendingOverlayJobId = "hydrology";
                        }
                        else
                        {
                            ClearPendingOverlayRequest();
                        }
                    }
                    else if (StartErosionAnalysis())
                    {
                        _pendingOverlayJobId = "erosion";
                    }
                    else
                    {
                        ClearPendingOverlayRequest();
                    }

                    break;
            }

            UpdateToolbarState();
        }

        private bool IsJobRunning(string jobId)
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            switch (jobId)
            {
                case "relief":
                    return runtime?.Relief?.IsRunning == true;
                case "hydrology":
                    return runtime?.Hydrology?.IsRunning == true;
                case "erosion":
                    return runtime?.Erosion?.IsRunning == true;
                default:
                    return false;
            }
        }

        private static bool HasCurrentJobResult(
            string jobId,
            TerrainWorldState state)
        {
            switch (jobId)
            {
                case "relief":
                    return state.Relief != null && state.Relief.IsCurrent(state);
                case "hydrology":
                    return state.Hydrology != null && state.Hydrology.IsCurrent(state);
                case "erosion":
                    return state.Erosion != null && state.Erosion.IsCurrent(state);
                default:
                    return false;
            }
        }

        private static string GetStaleResultMessage(string jobId)
        {
            switch (jobId)
            {
                case "relief":
                    return LM.Get("terrain_lab_relief_stale");
                case "hydrology":
                    return LM.Get("terrain_lab_hydrology_stale");
                case "erosion":
                    return LM.Get("terrain_lab_erosion_stale");
                default:
                    return LM.Get("terrain_lab_runtime_unavailable");
            }
        }

        private void ClearPendingOverlayRequest()
        {
            _pendingOverlayKind = PendingOverlayKind.None;
            _pendingReliefOverlay = TerrainReliefOverlayMode.None;
            _pendingHydrologyOverlay = TerrainHydrologyOverlayMode.None;
            _pendingErosionOverlay = TerrainErosionOverlayMode.None;
            _pendingOverlayJobId = null;
        }

        private void HideAllOverlays()
        {
            _elevationOverlay?.Clear();
            _dataOverlay?.Clear();
            _reliefOverlay?.Clear();
            _hydrologyOverlay?.Clear();
            _erosionOverlay?.Clear();
        }

        private void HideAllOverlaysWithStatus()
        {
            ClearPendingOverlayRequest();
            HideAllOverlays();
            SetStatus(LM.Get("terrain_lab_toolbar_overlays_hidden"), false);
        }

        private static string GetLayerAvailabilityLocalizationKey(
            TerrainLayerAvailability availability)
        {
            switch (availability)
            {
                case TerrainLayerAvailability.Ready:
                    return "terrain_lab_layer_status_ready";
                case TerrainLayerAvailability.Stale:
                    return "terrain_lab_layer_status_stale";
                default:
                    return "terrain_lab_layer_status_missing";
            }
        }

        private void BuildSettingsView()
        {
            CreateSectionHeading(_moduleContent, "terrain_lab_settings_project_heading");
            CreateInfoText(
                string.Format(
                    LM.Get("terrain_lab_format_version_format"),
                    WbxGeoFormat.SchemaVersion),
                NeutralText);
            CreateInfoText(
                string.Format(
                    LM.Get("terrain_lab_limit_format"),
                    TerrainMapLimits.MaximumCellCount),
                NeutralText,
                30f);
            CreateInfo("terrain_lab_aspect_unrestricted", NeutralText, 30f);

            CreateSectionHeading(_moduleContent, "terrain_lab_exchange_heading");
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            CreateTextLabel(
                _moduleContent,
                "TerrainLabExchangePath",
                runtime?.ExchangeDirectory ?? LM.Get("terrain_lab_runtime_unavailable"),
                10f,
                FontStyle.Normal,
                48f,
                TextAnchor.MiddleCenter,
                NeutralText);
            CreateActionButton(
                _moduleContent,
                LM.Get("terrain_lab_action_open_exchange"),
                OpenExchangeDirectory,
                194f,
                28f,
                "project_open",
                "terrain_lab_action_open_exchange",
                "terrain_lab_action_open_exchange_description");
            CreateActionButton(
                _moduleContent,
                LM.Get("terrain_lab_action_open_sync"),
                OpenSyncDirectory,
                194f,
                28f,
                "sync_qgis",
                "terrain_lab_action_open_sync",
                "terrain_lab_action_open_sync_description");
        }

        private void CreateLayerButton(string layerId, string labelKey)
        {
            SimpleButton button = CreateActionButton(
                _moduleContent,
                LM.Get(labelKey),
                delegate
                {
                    _selectedLayer = layerId;
                    RebuildModuleContent();
                },
                194f,
                28f,
                GetLayerIconId(layerId),
                labelKey,
                labelKey + "_description");
            button.Background.color = layerId == _selectedLayer
                ? Color.white
                : InactiveButton;
        }

        private static string GetLayerIconId(string layerId)
        {
            switch (layerId)
            {
                case "elevation":
                    return "layer_add_raster";
                case "landform":
                    return "layer_add_vector";
                case "material":
                    return "layer_style";
                case "vanilla":
                    return "project_open";
                default:
                    return "layer_stack";
            }
        }

        private void SaveProject()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            string error = null;
            if (runtime != null &&
                runtime.TrySaveCurrentProject(out string packagePath, out error))
            {
                RebuildModuleContent();
                SetStatus(
                    string.Format(
                        LM.Get("terrain_lab_status_saved_format"),
                        Path.GetFileName(packagePath)),
                    false,
                    true);
                return;
            }

            SetError(error ?? LM.Get("terrain_lab_runtime_unavailable"));
        }

        private void ExportProject()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            string error = null;
            if (runtime != null &&
                runtime.TryExportCurrentProject(out string exportPath, out error))
            {
                RebuildModuleContent();
                SetStatus(
                    string.Format(
                        LM.Get("terrain_lab_status_exported_format"),
                        Path.GetFileName(exportPath)),
                    false,
                    true);
                return;
            }

            SetError(error ?? LM.Get("terrain_lab_runtime_unavailable"));
        }

        private void ExportGisLayers()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            string error = null;
            if (runtime != null &&
                runtime.TryExportGisLayers(out string directory, out error))
            {
                SetStatus(
                    string.Format(
                        LM.Get("terrain_lab_status_gis_exported_format"),
                        Path.GetFileName(directory)),
                    false,
                    true);
                return;
            }

            SetError(error ?? LM.Get("terrain_lab_runtime_unavailable"));
        }

        private void PrepareFileSync()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            string error = null;
            if (runtime != null &&
                runtime.TryPrepareFileSync(out _, out error))
            {
                SetStatus(
                    string.Format(
                        LM.Get("terrain_lab_status_sync_prepared_format"),
                        CompactIdentifier(runtime.State.ProjectId)),
                    false,
                    true);
                return;
            }

            SetError(error ?? LM.Get("terrain_lab_runtime_unavailable"));
        }

        private void PullFileSync(TerrainSyncConflictPolicy policy)
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            string error = null;
            if (runtime == null ||
                !runtime.TryPullFileSync(policy, out TerrainSyncResult result, out error))
            {
                SetError(error ?? LM.Get("terrain_lab_runtime_unavailable"));
                return;
            }

            switch (result.Outcome)
            {
                case TerrainSyncOutcome.NoIncoming:
                    SetStatus(LM.Get("terrain_lab_status_sync_no_incoming"), false);
                    break;
                case TerrainSyncOutcome.NoChanges:
                    SetStatus(LM.Get("terrain_lab_status_sync_no_changes"), false, true);
                    break;
                case TerrainSyncOutcome.Conflict:
                    SetError(LM.Get("terrain_lab_status_sync_conflict"));
                    break;
                case TerrainSyncOutcome.WorldKept:
                    SetStatus(LM.Get("terrain_lab_status_sync_world_kept"), false);
                    break;
                case TerrainSyncOutcome.Applied:
                    HideAllOverlays();
                    _editor.RecordAppliedEdit(result.Edit);
                    RebuildModuleContent();
                    SetStatus(
                        string.Format(
                            LM.Get(result.ConflictDetected
                                ? "terrain_lab_status_sync_branched_format"
                                : "terrain_lab_status_sync_applied_format"),
                            result.ChangedCells),
                        false,
                        true);
                    break;
            }
        }

        private void ValidateProject()
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            string error = null;
            if (runtime != null &&
                runtime.TryValidateCurrentProject(out TerrainWorldState state, out error))
            {
                SetStatus(
                    string.Format(
                        LM.Get("terrain_lab_status_valid_format"),
                        state.Width,
                        state.Height),
                    false,
                    true);
                return;
            }

            SetError(error ?? LM.Get("terrain_lab_runtime_unavailable"));
        }

        private void RefreshProjectView()
        {
            RebuildModuleContent();
            SetStatus(LM.Get("terrain_lab_status_refreshed"), false, true);
        }

        private void ImportProject(string packagePath)
        {
            TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
            string error = null;
            if (runtime != null &&
                runtime.TryImportPackage(
                    packagePath,
                    out int slot,
                    out _,
                    out error))
            {
                SetStatus(
                    string.Format(LM.Get("terrain_lab_status_imported_format"), slot),
                    false,
                    true);
                return;
            }

            SetError(error ?? LM.Get("terrain_lab_runtime_unavailable"));
        }

        private void OpenExchangeDirectory()
        {
            try
            {
                TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
                if (runtime == null)
                {
                    throw new InvalidOperationException(LM.Get("terrain_lab_runtime_unavailable"));
                }

                Directory.CreateDirectory(runtime.ExchangeDirectory);
                OpenDirectory(runtime.ExchangeDirectory);
                SetStatus(LM.Get("terrain_lab_status_exchange_opened"), false, true);
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
            }
        }

        private void OpenSyncDirectory()
        {
            try
            {
                TerrainLabRuntime runtime = TerrainLabRuntime.Instance;
                string directory = runtime?.CurrentSyncDirectory;
                if (string.IsNullOrWhiteSpace(directory) ||
                    !File.Exists(Path.Combine(directory, "baseline.json")))
                {
                    throw new InvalidOperationException(
                        LM.Get("terrain_lab_status_sync_not_prepared"));
                }

                OpenDirectory(directory);
                SetStatus(LM.Get("terrain_lab_status_sync_opened"), false, true);
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
            }
        }

        private static void OpenDirectory(string path)
        {
            string directory = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            Application.OpenURL(new Uri(directory).AbsoluteUri);
        }

        private void HandleRuntimeStateChanged()
        {
            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            if (_elevationOverlay != null && _elevationOverlay.IsVisible &&
                !_elevationOverlay.References(state))
            {
                _elevationOverlay.Clear();
            }

            if (_dataOverlay != null && _dataOverlay.IsVisible &&
                !_dataOverlay.References(state))
            {
                _dataOverlay.Clear();
            }

            if (_reliefOverlay != null &&
                (state?.Relief == null || !state.Relief.IsCurrent(state)))
            {
                _reliefOverlay.Clear();
            }

            if (_hydrologyOverlay != null &&
                (state?.Hydrology == null || !state.Hydrology.IsCurrent(state)))
            {
                _hydrologyOverlay.Clear();
            }

            if (_erosionOverlay != null &&
                (state?.Erosion == null || !state.Erosion.IsCurrent(state)))
            {
                _erosionOverlay.Clear();
            }

            TryResolvePendingOverlay();

            if (_initialized && _window != null && _window.gameObject.activeInHierarchy)
            {
                RebuildModuleContent();
            }
        }

        private void HandleWaterCellsChanged(IReadOnlyList<int> indices)
        {
            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            _dataOverlay?.UpdateCells(state, indices);
        }

        private void HandleWaterElevationChanged(TerrainElevationEdit edit)
        {
            TerrainWorldState state = TerrainLabRuntime.Instance?.State;
            if (state == null || edit == null || edit.ChangedCellCount == 0)
            {
                return;
            }

            _elevationOverlay?.UpdateCells(state, edit);
            _dataOverlay?.UpdateCells(state, edit.Indices);
            _reliefOverlay?.Clear();
            _hydrologyOverlay?.Clear();
            _erosionOverlay?.Clear();
        }

        private void ShowWindow()
        {
            if (_window == null)
            {
                return;
            }

            SetWorkspaceVisible(true);
            if (_window.gameObject.activeInHierarchy)
            {
                _suppressWindowHideAdvance = true;
                _window.clickHide();
                UpdateSideButtonState();
                return;
            }

            RebuildModuleContent();
            _window.clickShow();
            UpdateSideButtonState();
        }

        private void HandleWindowHidden(string windowId)
        {
            if (!string.Equals(windowId, WindowId, StringComparison.Ordinal))
            {
                return;
            }

            if (_suppressWindowHideAdvance)
            {
                _suppressWindowHideAdvance = false;
                return;
            }

            if (_workspaceVisible)
            {
                _advanceWorkspaceAfterWindowClose = true;
            }
        }

        private void ToggleWorkspace()
        {
            if (!_workspaceVisible)
            {
                SetWorkspaceVisible(true);
                return;
            }

            if (_window != null && !_window.gameObject.activeInHierarchy)
            {
                ShowWindow();
                return;
            }

            SetWorkspaceVisible(false);
        }

        private void SetWorkspaceVisible(bool visible)
        {
            _workspaceVisible = visible;
            if (_topToolbar != null)
            {
                _topToolbar.SetActive(visible);
                if (visible)
                {
                    _topToolbar.transform.SetAsLastSibling();
                }
            }

            if (_mapStatusBar != null)
            {
                _mapStatusBar.SetActive(visible);
                if (visible)
                {
                    _mapStatusBar.transform.SetAsLastSibling();
                }
            }

            if (!visible && _window != null &&
                _window.gameObject.activeInHierarchy)
            {
                if (ScrollWindow.isCurrentWindow(WindowId))
                {
                    _suppressWindowHideAdvance = true;
                    _window.clickHide();
                }
                else
                {
                    _window.gameObject.SetActive(false);
                }
            }

            if (_sideButton != null)
            {
                _sideButton.transform.SetAsLastSibling();
            }

            UpdateSideButtonState();

            if (visible)
            {
                UpdateToolbarState();
                UpdateMapStatusBar();
            }
        }

        private WorkspaceLayer GetWorkspaceLayer()
        {
            if (!_workspaceVisible)
            {
                return WorkspaceLayer.Off;
            }

            return _window != null && _window.gameObject.activeInHierarchy
                ? WorkspaceLayer.Settings
                : WorkspaceLayer.Toolbar;
        }

        private void CreateSideStateIndicators()
        {
            Sprite indicator = GetIndicatorSprite();
            float spacing = Mathf.Max(6f, _indicatorDisplaySize.x + 1f);
            for (int index = 0; index < 3; index++)
            {
                GameObject indicatorObject = new GameObject(
                    "TerrainLabSideState" + index,
                    typeof(RectTransform),
                    typeof(Image));
                indicatorObject.transform.SetParent(_sideButton.transform, false);
                RectTransform rect = indicatorObject.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0f);
                rect.anchorMax = new Vector2(0.5f, 0f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = new Vector2((index - 1) * spacing, -1f);
                rect.sizeDelta = _indicatorDisplaySize;
                Image image = indicatorObject.GetComponent<Image>();
                image.sprite = indicator;
                image.preserveAspect = true;
                image.raycastTarget = false;
                _sideStateIndicators.Add(image);
            }

            UpdateSideButtonState();
        }

        private void UpdateSideButtonState()
        {
            if (_sideButton == null)
            {
                return;
            }

            WorkspaceLayer layer = GetWorkspaceLayer();
            _sideButton.Icon.color = layer == WorkspaceLayer.Off
                ? InactiveButton
                : Color.white;
            for (int index = 0; index < _sideStateIndicators.Count; index++)
            {
                bool active = index == (int)layer;
                Color activeColor = index == (int)WorkspaceLayer.Settings
                    ? ActivityAmber
                    : index == (int)WorkspaceLayer.Toolbar
                        ? ActivityGreen
                        : NeutralText;
                SetIndicatorVisual(
                    _sideStateIndicators[index],
                    active,
                    activeColor);
            }
        }

        private void SetError(string message)
        {
            SetStatus(
                string.Format(LM.Get("terrain_lab_status_error_format"), message),
                true);
        }

        private void SetStatus(string message, bool isError, bool isSuccess = false)
        {
            Color color = isError
                ? ErrorText
                : isSuccess ? SuccessText : NeutralText;
            if (_statusText != null)
            {
                _statusText.text = message;
                _statusText.color = color;
            }

            SetToolbarStatus(message, color);
        }

        private void SetToolbarStatus(string message, Color color)
        {
            // Detailed status remains in the internal window and bottom map strip.
        }

        private void CreateSectionHeading(Transform parent, string localizationKey)
        {
            CreateLocalizedLabel(
                parent,
                localizationKey,
                13f,
                FontStyle.Bold,
                24f,
                TextAnchor.MiddleCenter,
                Color.white);
        }

        private void CreateInfo(string localizationKey, Color color, float height = 24f)
        {
            CreateInfoText(LM.Get(localizationKey), color, height);
        }

        private void CreateInfoText(string text, Color color, float height = 24f)
        {
            CreateTextLabel(
                _moduleContent,
                "TerrainLabInfo",
                text,
                11f,
                FontStyle.Normal,
                height,
                TextAnchor.MiddleCenter,
                color);
        }

        private static Transform CreateVerticalContainer(
            Transform parent,
            string objectName,
            float spacing)
        {
            GameObject container = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(VerticalLayoutGroup),
                typeof(LayoutElement));
            container.transform.SetParent(parent, false);

            RectTransform rect = container.GetComponent<RectTransform>();
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(204f, 0f);

            VerticalLayoutGroup layout = container.GetComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            LayoutElement element = container.GetComponent<LayoutElement>();
            element.preferredWidth = 204f;
            element.preferredHeight = 0f;
            return container.transform;
        }

        private void UpdateAdaptiveToolbarLayout(bool force)
        {
            if (_topToolbar == null || _toolbarContent == null ||
                _toolbarLayoutButtons.Count == 0 || CanvasMain.instance == null)
            {
                return;
            }

            RectTransform canvasRect =
                CanvasMain.instance.canvas_ui.GetComponent<RectTransform>();
            float canvasWidth = canvasRect == null ? 0f : canvasRect.rect.width;
            if (canvasWidth <= 1f)
            {
                return;
            }

            if (!force && Mathf.Abs(canvasWidth - _lastToolbarCanvasWidth) < 0.5f)
            {
                return;
            }

            _lastToolbarCanvasWidth = canvasWidth;
            const float horizontalPadding = 6f;
            const float topPadding = 4f;
            const float rowHeight = 26f;
            const float minimumCellWidth = 28f;
            const float bottomPadding = 4f;

            float panelWidth = canvasWidth;
            float innerWidth = Mathf.Max(24f, panelWidth - horizontalPadding * 2f);
            int capacity = Mathf.Max(1, Mathf.FloorToInt(innerWidth / minimumCellWidth));
            List<ToolbarLayoutButton> primaryItems =
                new List<ToolbarLayoutButton>();
            List<ToolbarLayoutButton> functionalItems =
                new List<ToolbarLayoutButton>();
            for (int index = 0; index < _toolbarLayoutButtons.Count; index++)
            {
                ToolbarLayoutButton item = _toolbarLayoutButtons[index];
                bool functional = item.Role == ToolbarButtonRole.Functional;
                bool visible = !functional || item.Section == _activeToolbarSection;
                item.Rect.gameObject.SetActive(visible);
                if (!visible)
                {
                    continue;
                }

                if (functional)
                {
                    functionalItems.Add(item);
                }
                else
                {
                    primaryItems.Add(item);
                }
            }

            List<ToolbarLayoutButton> visibleItems =
                new List<ToolbarLayoutButton>(
                    primaryItems.Count + functionalItems.Count);
            visibleItems.AddRange(primaryItems);
            visibleItems.AddRange(functionalItems);

            List<int> rowCounts = CalculateToolbarRowCounts(
                primaryItems,
                capacity);
            rowCounts.AddRange(CalculateToolbarRowCounts(
                functionalItems,
                capacity));
            int rowCount = rowCounts.Count;

            RectTransform toolbarRect = _topToolbar.GetComponent<RectTransform>();
            float panelHeight = topPadding + rowCount * rowHeight + bottomPadding;
            toolbarRect.sizeDelta = new Vector2(panelWidth, panelHeight);
            _toolbarContent.anchoredPosition = new Vector2(0f, -topPadding);
            _toolbarContent.sizeDelta = new Vector2(0f, rowCount * rowHeight);
            if (_toolbarLeftRail != null)
            {
                _toolbarLeftRail.rectTransform.sizeDelta =
                    new Vector2(panelWidth, 0f);
            }

            if (_toolbarRightRail != null)
            {
                _toolbarRightRail.rectTransform.sizeDelta =
                    new Vector2(panelWidth, 0f);
            }

            int itemOffset = 0;
            for (int row = 0; row < rowCount; row++)
            {
                int buttonsInRow = rowCounts[row];
                float cellWidth = innerWidth / buttonsInRow;
                for (int column = 0; column < buttonsInRow; column++)
                {
                    ToolbarLayoutButton item = visibleItems[itemOffset++];
                    float centerX = horizontalPadding + cellWidth * (column + 0.5f);
                    item.Row = row;
                    item.CenterX = centerX;
                    item.Rect.anchoredPosition = new Vector2(
                        centerX,
                        -(row * rowHeight + rowHeight * 0.5f));
                    item.Rect.sizeDelta = new Vector2(24f, 24f);
                }
            }

            for (int separatorIndex = 0;
                 separatorIndex < _toolbarLayoutSeparators.Count;
                 separatorIndex++)
            {
                ToolbarLayoutSeparator separator =
                    _toolbarLayoutSeparators[separatorIndex];
                ToolbarLayoutButton previous = null;
                ToolbarLayoutButton next = null;
                for (int itemIndex = 0;
                     itemIndex < visibleItems.Count;
                     itemIndex++)
                {
                    ToolbarLayoutButton item = visibleItems[itemIndex];
                    if (item.Section != separator.Section)
                    {
                        continue;
                    }

                    if (item.Group < separator.StartsGroup)
                    {
                        previous = item;
                    }
                    else
                    {
                        next = item;
                        break;
                    }
                }

                bool sectionVisible =
                    separator.Section == ToolbarSection.Primary ||
                    separator.Section == _activeToolbarSection;
                bool visible = sectionVisible &&
                               previous != null && next != null &&
                               previous.Row == next.Row;
                separator.Rect.gameObject.SetActive(visible);
                if (visible)
                {
                    separator.Rect.anchoredPosition = new Vector2(
                        (previous.CenterX + next.CenterX) * 0.5f,
                        -(previous.Row * rowHeight + rowHeight * 0.5f));
                }
            }
        }

        private static List<int> CalculateToolbarRowCounts(
            IList<ToolbarLayoutButton> items,
            int capacity)
        {
            int itemCount = items.Count;
            if (itemCount == 0)
            {
                return new List<int>();
            }

            int rowCount = Mathf.Max(1, Mathf.CeilToInt(itemCount / (float)capacity));
            List<int> groupBoundaries = new List<int>();
            for (int index = 1; index < itemCount; index++)
            {
                if (items[index - 1].Group != items[index].Group)
                {
                    groupBoundaries.Add(index);
                }
            }

            groupBoundaries.Add(itemCount);
            List<int> result = new List<int>(rowCount);
            int start = 0;
            for (int row = 0; row < rowCount; row++)
            {
                int rowsRemaining = rowCount - row;
                if (rowsRemaining == 1)
                {
                    result.Add(itemCount - start);
                    break;
                }

                int remaining = itemCount - start;
                int targetSize = Mathf.RoundToInt(remaining / (float)rowsRemaining);
                int minimumEnd = Math.Max(
                    start + 1,
                    itemCount - capacity * (rowsRemaining - 1));
                int maximumEnd = Math.Min(
                    start + capacity,
                    itemCount - (rowsRemaining - 1));
                int bestEnd = -1;
                int bestDistance = int.MaxValue;
                for (int boundaryIndex = 0;
                     boundaryIndex < groupBoundaries.Count;
                     boundaryIndex++)
                {
                    int boundary = groupBoundaries[boundaryIndex];
                    if (boundary < minimumEnd || boundary > maximumEnd)
                    {
                        continue;
                    }

                    int distance = Math.Abs((boundary - start) - targetSize);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestEnd = boundary;
                    }
                }

                if (bestEnd < 0)
                {
                    bestEnd = Mathf.Clamp(
                        start + targetSize,
                        minimumEnd,
                        maximumEnd);
                }

                result.Add(bestEnd - start);
                start = bestEnd;
            }

            return result;
        }

        private void TryApplyBottomToolbarStyle()
        {
            if (_bottomToolbarStyleApplied || _toolbarBackground == null ||
                ToolbarButtons.instance == null)
            {
                return;
            }

            Image stockBackground = ToolbarButtons.instance.main_background;
            Sprite stockButton = ToolbarButtons.getSpriteButtonNormal();
            if (stockBackground == null || stockBackground.sprite == null ||
                stockButton == null)
            {
                return;
            }

            ApplyToolbarBackgroundStyle(_toolbarBackground, stockBackground);
            ApplyToolbarBackgroundStyle(_toolbarLeftRail, stockBackground);
            ApplyToolbarBackgroundStyle(_toolbarRightRail, stockBackground);

            for (int index = 0; index < _toolbarLayoutButtons.Count; index++)
            {
                ToolbarLayoutButton item = _toolbarLayoutButtons[index];
                Image buttonBackground = item.Button.Background;
                buttonBackground.sprite =
                    item.Role == ToolbarButtonRole.Critical &&
                    _criticalButtonSprite != null
                        ? _criticalButtonSprite
                        : stockButton;
                buttonBackground.type = Image.Type.Sliced;
            }

            _bottomToolbarStyleApplied = true;
        }

        private static void ApplyToolbarBackgroundStyle(
            Image target,
            Image source)
        {
            if (target == null || source == null)
            {
                return;
            }

            target.sprite = source.sprite;
            target.type = source.type;
            target.material = source.material;
            target.color = source.color;
            target.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
        }

        private void CreateToolbarSeparator(Transform parent)
        {
            _toolbarLayoutGroup++;
            Color groupColor = GetToolbarGroupColor(_toolbarLayoutGroup);
            GameObject separator = new GameObject(
                "TerrainLabToolbarSeparator",
                typeof(RectTransform),
                typeof(Image));
            separator.transform.SetParent(parent, false);
            RectTransform rect = separator.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(2f, 20f);
            Image image = separator.GetComponent<Image>();
            image.color = groupColor;
            image.raycastTarget = false;

            GameObject markerObject = new GameObject(
                "TerrainLabToolbarGroupFlag",
                typeof(RectTransform),
                typeof(Image));
            markerObject.transform.SetParent(separator.transform, false);
            RectTransform markerRect = markerObject.GetComponent<RectTransform>();
            markerRect.anchorMin = new Vector2(0.5f, 1f);
            markerRect.anchorMax = new Vector2(0.5f, 1f);
            markerRect.pivot = new Vector2(0f, 1f);
            markerRect.anchoredPosition = new Vector2(-1f, 0f);
            markerRect.sizeDelta = new Vector2(7f, 3f);
            Image marker = markerObject.GetComponent<Image>();
            marker.color = groupColor;
            marker.raycastTarget = false;
            _toolbarLayoutSeparators.Add(new ToolbarLayoutSeparator
            {
                Rect = rect,
                Marker = marker,
                Section = _toolbarCreationSection,
                StartsGroup = _toolbarLayoutGroup
            });
        }

        private SimpleButton CreateToolbarButton(
            Transform parent,
            string commandId,
            string iconId,
            string badge,
            UnityEngine.Events.UnityAction action,
            string tooltipNameKey,
            string tooltipDescriptionKey,
            float iconRotation = 0f)
        {
            Sprite icon = GetIcon(iconId);
            string fallbackText = icon == null ? badge ?? "?" : null;
            SimpleButton button = SimpleButton.Instantiate(parent, false, commandId);
            button.Setup(
                action,
                icon,
                fallbackText,
                new Vector2(24f, 24f),
                "tip",
                new TooltipData
                {
                    tip_name = tooltipNameKey,
                    tip_description = tooltipDescriptionKey
                });
            button.name = "TerrainLabToolbar_" + commandId;
            if (_criticalButtonSprite == null &&
                _toolbarCreationRole == ToolbarButtonRole.Critical)
            {
                _criticalButtonSprite = button.Background.sprite;
            }

            button.Background.color = InactiveButton;

            UnityEngine.UI.Outline groupOutline =
                button.Background.gameObject.AddComponent<UnityEngine.UI.Outline>();
            groupOutline.effectColor = _toolbarCreationRole == ToolbarButtonRole.Critical
                ? CriticalOutline
                : _toolbarCreationRole == ToolbarButtonRole.Section
                    ? SectionOutline
                    : GetToolbarGroupColor(_toolbarLayoutGroup);
            groupOutline.effectDistance = new Vector2(1f, -1f);
            groupOutline.useGraphicAlpha = true;

            RectTransform buttonRect = button.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0f, 1f);
            buttonRect.anchorMax = new Vector2(0f, 1f);
            buttonRect.pivot = new Vector2(0.5f, 0.5f);
            buttonRect.sizeDelta = new Vector2(24f, 24f);

            LayoutElement element = button.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = button.gameObject.AddComponent<LayoutElement>();
            }

            element.preferredWidth = 24f;
            element.preferredHeight = 24f;

            if (icon != null)
            {
                button.Text.gameObject.SetActive(false);
                button.Icon.gameObject.SetActive(true);
                button.Icon.preserveAspect = true;
                button.Icon.raycastTarget = false;
                RectTransform iconRect = button.Icon.GetComponent<RectTransform>();
                iconRect.anchorMin = new Vector2(0.5f, 0.5f);
                iconRect.anchorMax = new Vector2(0.5f, 0.5f);
                iconRect.pivot = new Vector2(0.5f, 0.5f);
                iconRect.anchoredPosition = Vector2.zero;
                iconRect.sizeDelta = new Vector2(18f, 18f);
                iconRect.localEulerAngles = new Vector3(0f, 0f, iconRotation);
                if (!string.IsNullOrWhiteSpace(badge))
                {
                    CreateToolbarBadge(button.transform, badge);
                }
            }
            else
            {
                button.Icon.gameObject.SetActive(false);
                button.Text.gameObject.SetActive(true);
                button.Text.text = fallbackText;
                button.Text.fontSize = 12;
                button.Text.fontStyle = FontStyle.Bold;
                button.Text.alignment = TextAnchor.MiddleCenter;
                button.Text.resizeTextForBestFit = true;
                button.Text.resizeTextMinSize = 8;
                button.Text.resizeTextMaxSize = 12;
                RectTransform textRect = button.Text.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(2f, 2f);
                textRect.offsetMax = new Vector2(-2f, -2f);
            }

            Image activityLamp = CreateToolbarActivityLamp(button.transform);

            _toolbarButtons.Add(button);
            _toolbarActivityLamps[button] = activityLamp;
            _toolbarLayoutButtons.Add(new ToolbarLayoutButton
            {
                Button = button,
                Rect = buttonRect,
                ActivityLamp = activityLamp,
                Section = _toolbarCreationSection,
                Role = _toolbarCreationRole,
                Group = _toolbarLayoutGroup
            });

            return button;
        }

        private static Color GetToolbarGroupColor(int group)
        {
            int index = Math.Abs(group) % ToolbarGroupColors.Length;
            return ToolbarGroupColors[index];
        }

        private Image CreateToolbarActivityLamp(Transform parent)
        {
            Sprite indicator = GetIndicatorSprite();
            GameObject lampObject = new GameObject(
                "TerrainLabToolbarActivityLamp",
                typeof(RectTransform),
                typeof(Image),
                typeof(UnityEngine.UI.Outline));
            lampObject.transform.SetParent(parent, false);
            RectTransform rect = lampObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, 1f);
            rect.sizeDelta = _indicatorDisplaySize;

            Image lamp = lampObject.GetComponent<Image>();
            lamp.sprite = indicator;
            lamp.preserveAspect = true;
            lamp.raycastTarget = false;
            UnityEngine.UI.Outline outline =
                lampObject.GetComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.95f);
            outline.effectDistance = new Vector2(0.6f, -0.6f);
            outline.useGraphicAlpha = true;
            outline.enabled = !_usesGameIndicatorSprites;
            SetIndicatorVisual(lamp, true, ActivityGreen);
            lampObject.SetActive(false);
            return lamp;
        }

        private Sprite GetIndicatorSprite()
        {
            if (_indicatorSprite != null)
            {
                return _indicatorSprite;
            }

            if (TryResolveGameIndicatorSprites())
            {
                return _indicatorSprite;
            }

            const int size = 5;
            _indicatorTexture = new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false)
            {
                name = "TerrainLabActivityLamp",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int dx = x - 2;
                    int dy = y - 2;
                    pixels[y * size + x] = dx * dx + dy * dy <= 5
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(0, 0, 0, 0);
                }
            }

            _indicatorTexture.SetPixels32(pixels);
            _indicatorTexture.Apply(false, true);
            _indicatorSprite = Sprite.Create(
                _indicatorTexture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                size);
            _indicatorSprite.name = "TerrainLabActivityLampSprite";
            _indicatorSprite.hideFlags = HideFlags.HideAndDontSave;
            return _indicatorSprite;
        }

        private bool TryResolveGameIndicatorSprites()
        {
            ToggleIcon[] candidates =
                UnityEngine.Resources.FindObjectsOfTypeAll<ToggleIcon>();
            ToggleIcon best = null;
            int bestScore = int.MinValue;
            for (int index = 0; index < candidates.Length; index++)
            {
                ToggleIcon candidate = candidates[index];
                if (candidate == null || candidate.spriteON == null ||
                    candidate.spriteOFF == null)
                {
                    continue;
                }

                int score = candidate.gameObject.activeInHierarchy ? 8 : 0;
                if (candidate.GetComponentInParent<PowerButton>() != null)
                {
                    score += 8;
                }

                if (candidate.name.IndexOf(
                        "toggle",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 2;
                }

                RectTransform sourceRect = candidate.GetComponent<RectTransform>();
                if (sourceRect != null &&
                    Mathf.Abs(sourceRect.rect.width) <= 12f &&
                    Mathf.Abs(sourceRect.rect.height) <= 12f)
                {
                    score += 4;
                }

                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (best == null)
            {
                return false;
            }

            _indicatorSprite = best.spriteON;
            _indicatorOffSprite = best.spriteOFF;
            _usesGameIndicatorSprites = true;
            RectTransform bestRect = best.GetComponent<RectTransform>();
            if (bestRect != null)
            {
                float width = Mathf.Abs(bestRect.rect.width);
                float height = Mathf.Abs(bestRect.rect.height);
                if (width >= 3f && height >= 3f)
                {
                    _indicatorDisplaySize = new Vector2(
                        Mathf.Clamp(width, 4f, 8f),
                        Mathf.Clamp(height, 4f, 8f));
                }
            }

            Debug.Log(
                "[TerrainLab] WorldBox ToggleIcon sprites: " +
                _indicatorSprite.name + " / " +
                (_indicatorOffSprite == null ? "none" : _indicatorOffSprite.name));
            return true;
        }

        private void SetIndicatorVisual(Image image, bool active, Color color)
        {
            if (image == null)
            {
                return;
            }

            if (_usesGameIndicatorSprites)
            {
                bool nativeGreen = active && IsIndicatorGreen(color);
                image.sprite = nativeGreen
                    ? _indicatorSprite
                    : _indicatorOffSprite ?? _indicatorSprite;
                image.color = nativeGreen
                    ? Color.white
                    : active ? color : Color.white;
                return;
            }

            image.sprite = _indicatorSprite;
            image.color = active
                ? color
                : new Color(0.12f, 0.12f, 0.11f, 0.9f);
        }

        private static bool IsIndicatorGreen(Color color)
        {
            return Mathf.Abs(color.r - ActivityGreen.r) < 0.01f &&
                   Mathf.Abs(color.g - ActivityGreen.g) < 0.01f &&
                   Mathf.Abs(color.b - ActivityGreen.b) < 0.01f;
        }

        private void SetToolbarActivity(
            SimpleButton button,
            bool active,
            Color color)
        {
            if (button == null ||
                !_toolbarActivityLamps.TryGetValue(button, out Image lamp))
            {
                return;
            }

            SetIndicatorVisual(lamp, active, color);
            lamp.gameObject.SetActive(active);
        }

        private void CreateToolbarEditorButton(
            Transform parent,
            TerrainEditorTool tool,
            string iconId,
            string badge,
            string tooltipNameKey,
            float iconRotation = 0f)
        {
            SimpleButton button = CreateToolbarButton(
                parent,
                "editor_" + tool,
                iconId,
                badge,
                delegate { SelectEditorTool(tool); },
                tooltipNameKey,
                GetEditorToolDescriptionLocalizationKey(tool),
                iconRotation);
            _toolbarEditorButtons[tool] = button;
        }

        private void CreateToolbarOverlayButton(
            Transform parent,
            string overlayId,
            string iconId,
            string badge,
            UnityEngine.Events.UnityAction action,
            string tooltipNameKey,
            float iconRotation = 0f)
        {
            SimpleButton button = CreateToolbarButton(
                parent,
                overlayId,
                iconId,
                badge,
                action,
                tooltipNameKey,
                tooltipNameKey + "_description",
                iconRotation);
            _toolbarOverlayButtons[overlayId] = button;
        }

        private static void CreateToolbarBadge(Transform parent, string badge)
        {
            GameObject badgeObject = new GameObject(
                "TerrainLabToolbarBadge",
                typeof(RectTransform),
                typeof(Text),
                typeof(UnityEngine.UI.Outline));
            badgeObject.transform.SetParent(parent, false);
            RectTransform rect = badgeObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-2f, 2f);
            rect.sizeDelta = new Vector2(10f, 9f);

            Text text = badgeObject.GetComponent<Text>();
            text.font = LocalizedTextManager.current_font;
            text.text = badge;
            text.fontSize = 7;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            UnityEngine.UI.Outline outline =
                badgeObject.GetComponent<UnityEngine.UI.Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.95f);
            outline.effectDistance = new Vector2(0.7f, -0.7f);
            outline.useGraphicAlpha = true;
        }

        private static Transform CreateActionRow(Transform parent, string objectName)
        {
            GameObject row = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(HorizontalLayoutGroup),
                typeof(LayoutElement));
            row.transform.SetParent(parent, false);

            RectTransform rect = row.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(204f, 28f);

            HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            LayoutElement element = row.GetComponent<LayoutElement>();
            element.preferredWidth = 204f;
            element.preferredHeight = 28f;
            return row.transform;
        }

        private SimpleButton CreateActionButton(
            Transform parent,
            string text,
            UnityEngine.Events.UnityAction action,
            float width,
            float height,
            string iconId,
            string tooltipNameKey,
            string tooltipDescriptionKey,
            float iconRotation = 0f)
        {
            Sprite icon = GetIcon(iconId);
            SimpleButton button = SimpleButton.Instantiate(parent, false);
            button.Setup(
                action,
                icon,
                text,
                new Vector2(width, height),
                "tip",
                new TooltipData
                {
                    tip_name = tooltipNameKey,
                    tip_description = tooltipDescriptionKey
                });
            button.Text.resizeTextMinSize = 7;
            button.Text.resizeTextMaxSize = 11;
            if (icon != null)
            {
                ConfigureActionButtonIcon(button, width, height, iconRotation);
            }

            LayoutElement element = button.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = button.gameObject.AddComponent<LayoutElement>();
            }

            element.preferredWidth = width;
            element.preferredHeight = height;
            return button;
        }

        private Sprite GetIcon(string iconId)
        {
            if (string.IsNullOrWhiteSpace(iconId))
            {
                return null;
            }

            if (_iconCache.TryGetValue(iconId, out Sprite cached))
            {
                return cached;
            }

            string resourcePath = iconId.IndexOf('/') >= 0
                ? iconId
                : IconResourceRoot + iconId;
            Sprite icon = SpriteTextureLoader.getSprite(resourcePath);
            _iconCache[iconId] = icon;
            if (icon == null && _missingIconIds.Add(iconId))
            {
                Debug.LogWarning(
                    "[TerrainLab] UI sprite not found: " + resourcePath +
                    ". A text fallback will be used.");
            }

            return icon;
        }

        private static void ConfigureActionButtonIcon(
            SimpleButton button,
            float width,
            float height,
            float iconRotation)
        {
            float iconSize = Mathf.Max(14f, height - 8f);
            button.Icon.gameObject.SetActive(true);
            button.Icon.preserveAspect = true;
            button.Icon.raycastTarget = false;

            RectTransform iconRect = button.Icon.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.sizeDelta = new Vector2(iconSize, iconSize);
            iconRect.localEulerAngles = new Vector3(0f, 0f, iconRotation);
            iconRect.anchoredPosition = new Vector2(
                -width * 0.5f + 4f + iconSize * 0.5f,
                0f);

            RectTransform textRect = button.Text.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.5f, 0.5f);
            textRect.anchorMax = new Vector2(0.5f, 0.5f);
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.sizeDelta = new Vector2(
                Mathf.Max(20f, width - iconSize - 12f),
                height * 0.875f);
            textRect.anchoredPosition = new Vector2((iconSize + 4f) * 0.5f, 0f);
        }

        private static Text CreateLocalizedLabel(
            Transform parent,
            string localizationKey,
            float fontSize,
            FontStyle fontStyle,
            float height,
            TextAnchor alignment,
            Color color)
        {
            return CreateTextLabel(
                parent,
                "TerrainLabLabel_" + localizationKey,
                LM.Get(localizationKey),
                fontSize,
                fontStyle,
                height,
                alignment,
                color);
        }

        private static Text CreateTextLabel(
            Transform parent,
            string objectName,
            string text,
            float fontSize,
            FontStyle fontStyle,
            float height,
            TextAnchor alignment,
            Color color,
            float width = 204f)
        {
            GameObject labelObject = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(Text),
                typeof(LayoutElement));
            labelObject.transform.SetParent(parent, false);

            Text label = labelObject.GetComponent<Text>();
            label.font = LocalizedTextManager.current_font;
            label.text = text;
            label.fontSize = (int)fontSize;
            label.fontStyle = fontStyle;
            label.color = color;
            label.alignment = alignment;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.supportRichText = false;

            RectTransform rect = labelObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);

            LayoutElement layoutElement = labelObject.GetComponent<LayoutElement>();
            layoutElement.preferredWidth = width;
            layoutElement.preferredHeight = height;
            return label;
        }

        private static void ClearChildren(Transform parent)
        {
            for (int index = parent.childCount - 1; index >= 0; index--)
            {
                GameObject child = parent.GetChild(index).gameObject;
                child.SetActive(false);
                Destroy(child);
            }
        }

        private static string CompactIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "-";
            }

            return value.Length <= 8 ? value : value.Substring(0, 8);
        }

        private static string CompactFileName(string path, int maximumLength)
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            if (fileName.Length <= maximumLength)
            {
                return fileName;
            }

            int sideLength = (maximumLength - 3) / 2;
            return fileName.Substring(0, sideLength) + "..." +
                   fileName.Substring(fileName.Length - sideLength);
        }

        private void OnDestroy()
        {
            ScrollWindow.removeCallbackHide(HandleWindowHidden);

            if (_editor != null)
            {
                _editor.EditApplied -= HandleElevationEditApplied;
                _editor.ElevationChanged -= HandleElevationChanged;
                _editor.SurfaceSampled -= HandleSurfaceSampled;
                _editor.OperationFailed -= HandleEditorOperationFailed;
                _editor.RegionPolygonized -= HandleRegionPolygonized;
                _editor.RampStarted -= HandleRampStarted;
                _editor.SetInterfaceState(false, false);
            }

            if (TerrainLabRuntime.Instance != null)
            {
                TerrainLabRuntime.Instance.StateChanged -= HandleRuntimeStateChanged;
                TerrainLabRuntime.Instance.WaterDynamics.CellsChanged -=
                    HandleWaterCellsChanged;
                TerrainLabRuntime.Instance.WaterDynamics.ElevationChanged -=
                    HandleWaterElevationChanged;
            }

            if (_sideButton != null)
            {
                Destroy(_sideButton.gameObject);
            }

            if (_topToolbar != null)
            {
                Destroy(_topToolbar);
            }

            if (_mapStatusBar != null)
            {
                Destroy(_mapStatusBar);
            }

            if (_window != null)
            {
                Destroy(_window.gameObject);
            }

            if (_indicatorTexture != null && _indicatorSprite != null)
            {
                Destroy(_indicatorSprite);
            }

            if (_indicatorTexture != null)
            {
                Destroy(_indicatorTexture);
            }
        }
    }
}
