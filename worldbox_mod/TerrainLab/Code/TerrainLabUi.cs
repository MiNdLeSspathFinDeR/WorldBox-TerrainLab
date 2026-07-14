using System.Collections.Generic;
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

        private readonly Dictionary<string, SimpleButton> _moduleButtons =
            new Dictionary<string, SimpleButton>();

        private ScrollWindow _window;
        private SimpleButton _sideButton;
        private Text _selectionText;
        private bool _initialized;

        public void Initialize(ModDeclare declaration)
        {
            if (_initialized)
            {
                return;
            }

            CreateWindow();
            CreateSideButton(declaration.GetIcon());
            _initialized = true;
        }

        private void CreateWindow()
        {
            // WindowCreator clones WorldBox's own "windows/empty" prefab.
            _window = WindowCreator.CreateEmptyWindow(WindowId, WindowTitleKey);
            ConfigureWindowTitle();

            Transform contentTransform = _window.transform.Find(
                "Background/Scroll View/Viewport/Content");
            if (contentTransform == null)
            {
                throw new MissingReferenceException(
                    "WorldBox empty window does not contain its standard content transform.");
            }

            GameObject content = contentTransform.gameObject;
            ConfigureContentLayout(content);

            CreateLabel(content.transform, "terrain_lab_menu_heading", 22f, FontStyle.Bold, 28f);
            CreateModuleButton(content.transform, "map", "terrain_lab_module_map");
            CreateModuleButton(content.transform, "relief", "terrain_lab_module_relief");
            CreateModuleButton(content.transform, "hydrology", "terrain_lab_module_hydrology");
            CreateModuleButton(content.transform, "erosion", "terrain_lab_module_erosion");
            CreateModuleButton(content.transform, "settings", "terrain_lab_module_settings");

            _selectionText = CreateLabel(
                content.transform,
                "terrain_lab_status_ready",
                12f,
                FontStyle.Normal,
                34f);
            _selectionText.color = new Color(0.83f, 0.79f, 0.66f, 1f);

            SelectModule("map", "terrain_lab_module_map");
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
            layout.spacing = 7f;
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
            _sideButton.Setup(ShowWindow, icon, null, new Vector2(52f, 52f));

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
            SimpleButton button = SimpleButton.Instantiate(
                parent,
                false,
                "TerrainLabModule_" + moduleId);

            button.Setup(
                delegate { SelectModule(moduleId, labelKey); },
                null,
                LM.Get(labelKey),
                new Vector2(194f, 32f));

            LayoutElement element = button.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = button.gameObject.AddComponent<LayoutElement>();
            }

            element.preferredWidth = 194f;
            element.preferredHeight = 32f;
            _moduleButtons[moduleId] = button;
        }

        private static Text CreateLabel(
            Transform parent,
            string localizationKey,
            float fontSize,
            FontStyle fontStyle,
            float height)
        {
            GameObject labelObject = new GameObject(
                "TerrainLabLabel_" + localizationKey,
                typeof(RectTransform),
                typeof(Text),
                typeof(LayoutElement));
            labelObject.transform.SetParent(parent, false);

            Text label = labelObject.GetComponent<Text>();
            label.font = LocalizedTextManager.current_font;
            label.text = LM.Get(localizationKey);
            label.fontSize = (int)fontSize;
            label.fontStyle = fontStyle;
            label.color = Color.white;
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;

            RectTransform rect = labelObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(204f, height);

            LayoutElement layoutElement = labelObject.GetComponent<LayoutElement>();
            layoutElement.preferredWidth = 204f;
            layoutElement.preferredHeight = height;
            return label;
        }

        private void SelectModule(string moduleId, string labelKey)
        {
            foreach (KeyValuePair<string, SimpleButton> pair in _moduleButtons)
            {
                pair.Value.Background.color = pair.Key == moduleId
                    ? Color.white
                    : new Color(0.68f, 0.68f, 0.68f, 1f);
            }

            if (_selectionText != null)
            {
                _selectionText.text = string.Format(
                    LM.Get("terrain_lab_selected_format"),
                    LM.Get(labelKey));
            }
        }

        private void ShowWindow()
        {
            if (_window != null)
            {
                _window.clickShow();
            }
        }

        private void OnDestroy()
        {
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
