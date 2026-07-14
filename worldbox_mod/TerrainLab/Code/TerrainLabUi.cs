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
        private const string WindowId = "terrain_lab_window";
        private const string WindowTitleKey = "terrain_lab_window_title";
        private const string SideButtonKey = "terrain_lab_side_button";

        private static readonly Color NeutralText = new Color(0.83f, 0.79f, 0.66f, 1f);
        private static readonly Color SuccessText = new Color(0.55f, 0.82f, 0.55f, 1f);
        private static readonly Color ErrorText = new Color(0.95f, 0.52f, 0.46f, 1f);
        private static readonly Color InactiveButton = new Color(0.68f, 0.68f, 0.68f, 1f);

        private readonly Dictionary<string, SimpleButton> _moduleButtons =
            new Dictionary<string, SimpleButton>();

        private ScrollWindow _window;
        private SimpleButton _sideButton;
        private Transform _moduleContent;
        private Text _statusText;
        private string _selectedModule = "map";
        private string _selectedLayer = "elevation";
        private bool _initialized;

        public void Initialize(ModDeclare declaration)
        {
            if (_initialized)
            {
                return;
            }

            CreateWindow();
            CreateSideButton(declaration.GetIcon());

            if (TerrainLabRuntime.Instance != null)
            {
                TerrainLabRuntime.Instance.StateChanged += HandleRuntimeStateChanged;
            }

            _initialized = true;
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

            CreateModuleButton(contentTransform, "map", "terrain_lab_module_map");
            CreateModuleButton(contentTransform, "relief", "terrain_lab_module_relief");
            CreateModuleButton(contentTransform, "hydrology", "terrain_lab_module_hydrology");
            CreateModuleButton(contentTransform, "erosion", "terrain_lab_module_erosion");
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

        private void CreateSideButton(Sprite icon)
        {
            Transform canvas = CanvasMain.instance.canvas_ui.transform;
            _sideButton = SimpleButton.Instantiate(canvas, false, SideButtonKey);
            _sideButton.Setup(
                ShowWindow,
                icon,
                null,
                new Vector2(52f, 52f),
                "tip",
                new TooltipData
                {
                    tip_name = LM.Get(SideButtonKey),
                    tip_description = LM.Get(SideButtonKey + " Description")
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
            _sideButton.Background.color = Color.clear;
            _sideButton.Background.raycastTarget = true;
            _sideButton.Button.transition = Selectable.Transition.None;
            _sideButton.transform.SetAsLastSibling();
        }

        private void CreateModuleButton(Transform parent, string moduleId, string labelKey)
        {
            SimpleButton button = CreateActionButton(
                parent,
                LM.Get(labelKey),
                delegate { SelectModule(moduleId); },
                194f,
                30f);
            button.name = "TerrainLabModule_" + moduleId;
            _moduleButtons[moduleId] = button;
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

            ClearChildren(_moduleContent);
            switch (_selectedModule)
            {
                case "map":
                    BuildProjectView();
                    break;
                case "relief":
                    BuildReliefView();
                    break;
                case "hydrology":
                    BuildPlannedModuleView("terrain_lab_module_hydrology");
                    break;
                case "erosion":
                    BuildPlannedModuleView("terrain_lab_module_erosion");
                    break;
                case "settings":
                    BuildSettingsView();
                    break;
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
            CreateInfo(
                packageExists
                    ? "terrain_lab_package_saved"
                    : "terrain_lab_package_missing",
                packageExists ? SuccessText : NeutralText);

            Transform actionRow = CreateActionRow(_moduleContent, "TerrainLabProjectActions");
            CreateActionButton(
                actionRow,
                LM.Get("terrain_lab_action_save"),
                SaveProject,
                94f,
                28f);
            CreateActionButton(
                actionRow,
                LM.Get("terrain_lab_action_export"),
                ExportProject,
                94f,
                28f);

            Transform validationRow = CreateActionRow(_moduleContent, "TerrainLabValidationActions");
            CreateActionButton(
                validationRow,
                LM.Get("terrain_lab_action_validate"),
                ValidateProject,
                94f,
                28f);
            CreateActionButton(
                validationRow,
                LM.Get("terrain_lab_action_refresh"),
                RefreshProjectView,
                94f,
                28f);

            CreateActionButton(
                _moduleContent,
                LM.Get("terrain_lab_action_open_exchange"),
                OpenExchangeDirectory,
                194f,
                28f);

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
                    28f);
            }
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
        }

        private void BuildPlannedModuleView(string moduleLabelKey)
        {
            CreateTextLabel(
                _moduleContent,
                "TerrainLabPlannedHeading",
                LM.Get(moduleLabelKey),
                15f,
                FontStyle.Bold,
                28f,
                TextAnchor.MiddleCenter,
                Color.white);
            CreateInfo("terrain_lab_module_not_installed", NeutralText, 42f);
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
                28f);
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
                28f);
            button.Background.color = layerId == _selectedLayer
                ? Color.white
                : InactiveButton;
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
                string directory = Path.GetFullPath(runtime.ExchangeDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                Application.OpenURL(new Uri(directory).AbsoluteUri);
                SetStatus(LM.Get("terrain_lab_status_exchange_opened"), false, true);
            }
            catch (Exception exception)
            {
                SetError(exception.Message);
            }
        }

        private void HandleRuntimeStateChanged()
        {
            if (_initialized && _window != null && _window.gameObject.activeInHierarchy)
            {
                RebuildModuleContent();
            }
        }

        private void ShowWindow()
        {
            if (_window == null)
            {
                return;
            }

            RebuildModuleContent();
            _window.clickShow();
        }

        private void SetError(string message)
        {
            SetStatus(
                string.Format(LM.Get("terrain_lab_status_error_format"), message),
                true);
        }

        private void SetStatus(string message, bool isError, bool isSuccess = false)
        {
            if (_statusText == null)
            {
                return;
            }

            _statusText.text = message;
            _statusText.color = isError
                ? ErrorText
                : isSuccess ? SuccessText : NeutralText;
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
                typeof(ContentSizeFitter));
            container.transform.SetParent(parent, false);

            RectTransform rect = container.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(204f, 0f);

            VerticalLayoutGroup layout = container.GetComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            ContentSizeFitter fitter = container.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return container.transform;
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

        private static SimpleButton CreateActionButton(
            Transform parent,
            string text,
            UnityEngine.Events.UnityAction action,
            float width,
            float height)
        {
            SimpleButton button = SimpleButton.Instantiate(parent, false);
            button.Setup(action, null, text, new Vector2(width, height));
            button.Text.resizeTextMinSize = 7;
            button.Text.resizeTextMaxSize = 11;

            LayoutElement element = button.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = button.gameObject.AddComponent<LayoutElement>();
            }

            element.preferredWidth = width;
            element.preferredHeight = height;
            return button;
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
            Color color)
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
            rect.sizeDelta = new Vector2(204f, height);

            LayoutElement layoutElement = labelObject.GetComponent<LayoutElement>();
            layoutElement.preferredWidth = 204f;
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
            if (TerrainLabRuntime.Instance != null)
            {
                TerrainLabRuntime.Instance.StateChanged -= HandleRuntimeStateChanged;
            }

            if (_sideButton != null)
            {
                Destroy(_sideButton.gameObject);
            }

            if (_window != null)
            {
                Destroy(_window.gameObject);
            }
        }
    }
}
