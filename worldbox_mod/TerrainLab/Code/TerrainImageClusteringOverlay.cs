using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NeoModLoader.General;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityColor = UnityEngine.Color;

namespace TerrainLab
{
    internal sealed class TerrainImageClusteringCanvasInput : MonoBehaviour,
        IPointerClickHandler,
        IScrollHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IPointerMoveHandler,
        IPointerExitHandler
    {
        private TerrainImageClusteringOverlay _owner;
        private bool _dragging;
        private bool _moved;

        public void Initialize(TerrainImageClusteringOverlay owner)
        {
            _owner = owner;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_owner == null || _moved)
            {
                _moved = false;
                return;
            }

            if (eventData.button == PointerEventData.InputButton.Left)
            {
                _owner.HandleImageClick(
                    eventData.position,
                    false,
                    eventData.clickCount >= 2);
            }
            else if (eventData.button == PointerEventData.InputButton.Right)
            {
                _owner.HandleImageClick(eventData.position, true, false);
            }
        }

        public void OnScroll(PointerEventData eventData)
        {
            _owner?.HandleZoom(eventData.position, eventData.scrollDelta.y);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _dragging =
                eventData.button == PointerEventData.InputButton.Middle ||
                eventData.button == PointerEventData.InputButton.Right;
            _moved = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragging || _owner == null)
            {
                return;
            }
            if (eventData.delta.sqrMagnitude > 0.25f)
            {
                _moved = true;
            }
            _owner.HandlePan(eventData.delta);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _dragging = false;
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            _owner?.HandlePointerMove(eventData.position);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _owner?.HandlePointerExit();
        }
    }

    public sealed class TerrainImageClusteringOverlay : MonoBehaviour
    {
        private const float MinimumZoom = 1f;
        private const float MaximumZoom = 12f;

        private readonly DefaultControls.Resources _defaultResources =
            new DefaultControls.Resources();
        private readonly List<Selectable> _editorControls =
            new List<Selectable>();
        private readonly Dictionary<string, InputField> _parameterInputs =
            new Dictionary<string, InputField>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _parameterLocalizationKeys =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Toggle> _surfaceToggles =
            new Dictionary<string, Toggle>(StringComparer.Ordinal);
        private readonly Dictionary<string, Toggle> _biotopeToggles =
            new Dictionary<string, Toggle>(StringComparer.Ordinal);
        private readonly List<TerrainImageClassificationVertex> _draftVertices =
            new List<TerrainImageClassificationVertex>();

        private TerrainImageWorkspaceService _workspace;
        private Transform _toolbarTransform;
        private GameObject _root;
        private RectTransform _viewport;
        private RectTransform _imageRect;
        private RawImage _image;
        private RectTransform _graphicRoot;
        private Text _fileLabel;
        private Text _summaryLabel;
        private Text _coordinateLabel;
        private Text _mapSizeLabel;
        private Text _clusterBudgetLabel;
        private Text _panelStatus;
        private InputField _longSideInput;
        private Button _previousImageButton;
        private Button _nextImageButton;
        private Button _openSelectedButton;
        private Button _drawBoundaryButton;
        private Button _finishBoundaryButton;
        private Button _cancelBoundaryButton;
        private Button _clearBoundaryButton;
        private Button _legacyAlgorithmButton;
        private Button _semanticAlgorithmButton;
        private Button _expertButton;
        private GameObject _expertPanel;
        private Button _compositionButton;
        private GameObject _compositionPanel;
        private Button _surfaceCompositionButton;
        private Button _biotopeCompositionButton;
        private GameObject _surfaceCompositionPanel;
        private GameObject _biotopeCompositionPanel;
        private TerrainImagePolygonGraphic _savedBoundaryGraphic;
        private TerrainImagePolygonGraphic _draftBoundaryGraphic;
        private TerrainImageClusteringProfile _profile;
        private Texture2D _texture;
        private List<string> _images = new List<string>();
        private string _imagePath;
        private int _imageIndex = -1;
        private int _pendingImageIndex = -1;
        private int _sourceWidth;
        private int _sourceHeight;
        private float _zoom = 1f;
        private Vector2 _lastViewportSize;
        private TerrainImageClassificationVertex _hoverVertex;
        private bool _hasHoverVertex;
        private bool _drawingBoundary;
        private bool _expertVisible;
        private bool _compositionVisible;
        private bool _surfaceCompositionVisible = true;
        private bool _initialized;

        public event Action<string, bool> StatusChanged;

        public event Action VisibilityChanged;

        public bool IsVisible => _root != null && _root.activeSelf;

        public string CurrentImageName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_imagePath))
                {
                    return Path.GetFileName(_imagePath);
                }
                return _pendingImageIndex >= 0 &&
                       _pendingImageIndex < _images.Count
                    ? Path.GetFileName(_images[_pendingImageIndex])
                    : null;
            }
        }

        public int ClusterCount => _profile?.Settings?.Clusters ?? 0;

        public bool HasBoundary =>
            (_profile?.MapBoundary?.Vertices?.Count ?? 0) >= 3;

        public void Initialize(
            TerrainImageWorkspaceService workspace,
            Transform toolbarTransform)
        {
            _workspace = workspace ??
                         throw new ArgumentNullException(nameof(workspace));
            _toolbarTransform = toolbarTransform;
            _initialized = true;
        }

        public void ToggleLatest()
        {
            if (IsVisible)
            {
                Hide();
                return;
            }
            ShowImagePicker();
        }

        public void ShowImagePicker()
        {
            EnsureInitialized();
            try
            {
                string previous = _imagePath;
                SaveCurrentProfile(false);
                _images = _workspace.GetRecentImages(64).ToList();
                EnsureUi();
                ResetLoadedImage();
                InitializePendingImage(previous);
                _root.SetActive(true);
                BringToFront();

                if (_images.Count == 0)
                {
                    _fileLabel.text =
                        LM.Get("terrain_lab_cluster_no_images");
                    _summaryLabel.text = string.Empty;
                    SetPanelStatus(
                        LM.Get("terrain_lab_cluster_no_images"),
                        true);
                    SetStatus(
                        LM.Get("terrain_lab_cluster_no_images"),
                        true);
                }
                else
                {
                    SetPanelStatus(
                        LM.Get("terrain_lab_cluster_select_and_confirm"),
                        false);
                    SetStatus(
                        LM.Get("terrain_lab_status_cluster_selector_opened"),
                        false);
                }
                VisibilityChanged?.Invoke();
            }
            catch (Exception exception)
            {
                SetStatus(exception.Message, true);
            }
        }

        public void Hide()
        {
            if (_root == null)
            {
                return;
            }

            SaveCurrentProfile(false);
            CancelBoundary(false);
            _root.SetActive(false);
            if (_image != null)
            {
                _image.texture = null;
            }
            ReleaseTexture();
            VisibilityChanged?.Invoke();
        }

        public void HandleImageClick(
            Vector2 screenPosition,
            bool secondary,
            bool doubleClick)
        {
            if (!IsVisible || _profile == null ||
                !_drawingBoundary || _imageRect == null)
            {
                return;
            }
            if (!TryScreenToSource(
                    screenPosition,
                    out int sourceX,
                    out int sourceY))
            {
                return;
            }

            UpdateCoordinateLabel(sourceX, sourceY);
            if (secondary)
            {
                FinishBoundary();
                return;
            }
            if (_draftVertices.Count >=
                TerrainImageClusteringProfile.MaximumBoundaryVertices)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_boundary_vertex_limit"),
                    true);
                return;
            }
            TerrainImageClassificationVertex previous =
                _draftVertices.Count == 0
                    ? null
                    : _draftVertices[_draftVertices.Count - 1];
            if (previous == null ||
                previous.X != sourceX || previous.Y != sourceY)
            {
                _draftVertices.Add(
                    new TerrainImageClassificationVertex(sourceX, sourceY));
                RefreshBoundaryGraphics();
                UpdateSummary();
            }
            if (doubleClick && _draftVertices.Count >= 3)
            {
                FinishBoundary();
            }
        }

        public void HandlePointerMove(Vector2 screenPosition)
        {
            if (!IsVisible ||
                !TryScreenToSource(
                    screenPosition,
                    out int sourceX,
                    out int sourceY))
            {
                HandlePointerExit();
                return;
            }

            UpdateCoordinateLabel(sourceX, sourceY);
            if (!_drawingBoundary || _draftVertices.Count == 0)
            {
                return;
            }
            if (_hasHoverVertex &&
                _hoverVertex.X == sourceX &&
                _hoverVertex.Y == sourceY)
            {
                return;
            }
            _hoverVertex =
                new TerrainImageClassificationVertex(sourceX, sourceY);
            _hasHoverVertex = true;
            RefreshBoundaryGraphics();
        }

        public void HandlePointerExit()
        {
            if (!_hasHoverVertex)
            {
                return;
            }
            _hoverVertex = null;
            _hasHoverVertex = false;
            RefreshBoundaryGraphics();
        }

        public void HandleZoom(Vector2 screenPosition, float delta)
        {
            if (!IsVisible || Mathf.Abs(delta) < 0.01f)
            {
                return;
            }

            float previous = _zoom;
            _zoom = Mathf.Clamp(
                _zoom * (delta > 0f ? 1.22f : 1f / 1.22f),
                MinimumZoom,
                MaximumZoom);
            if (Mathf.Approximately(previous, _zoom))
            {
                return;
            }

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _viewport,
                    screenPosition,
                    null,
                    out Vector2 cursor))
            {
                float ratio = _zoom / previous;
                _imageRect.anchoredPosition =
                    cursor - (cursor - _imageRect.anchoredPosition) * ratio;
            }
            _imageRect.localScale = new Vector3(_zoom, _zoom, 1f);
            UpdateGraphicScale();
            ClampPan();
        }

        public void HandlePan(Vector2 delta)
        {
            if (!IsVisible || _imageRect == null)
            {
                return;
            }

            Canvas canvas = CanvasMain.instance?.canvas_ui;
            float scale = canvas == null
                ? 1f
                : Mathf.Max(0.01f, canvas.scaleFactor);
            _imageRect.anchoredPosition += delta / scale;
            ClampPan();
        }

        private void Update()
        {
            if (!IsVisible || _viewport == null)
            {
                return;
            }
            Vector2 size = _viewport.rect.size;
            if ((size - _lastViewportSize).sqrMagnitude > 1f)
            {
                FitImageToViewport(false);
            }
        }

        private void EnsureUi()
        {
            if (_root != null)
            {
                return;
            }

            Transform canvas = CanvasMain.instance.canvas_ui.transform;
            _root = new GameObject(
                "TerrainLabAutomaticClustering",
                typeof(RectTransform));
            _root.transform.SetParent(canvas, false);
            RectTransform rootRect = _root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = new Vector2(0f, 112f);
            rootRect.offsetMax = new Vector2(0f, -92f);

            GameObject viewportObject = new GameObject(
                "TerrainLabClusteringViewport",
                typeof(RectTransform),
                typeof(Image),
                typeof(RectMask2D),
                typeof(TerrainImageClusteringCanvasInput));
            viewportObject.transform.SetParent(_root.transform, false);
            _viewport = viewportObject.GetComponent<RectTransform>();
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = Vector2.zero;
            _viewport.offsetMax = new Vector2(-326f, 0f);
            Image viewportBackground = viewportObject.GetComponent<Image>();
            viewportBackground.color =
                new UnityColor(0.01f, 0.01f, 0.01f, 0.9f);
            viewportBackground.raycastTarget = true;
            viewportObject
                .GetComponent<TerrainImageClusteringCanvasInput>()
                .Initialize(this);
            ConfigureTooltip(
                viewportObject,
                "terrain_lab_cluster_canvas");

            GameObject imageObject = new GameObject(
                "TerrainLabClusteringImage",
                typeof(RectTransform),
                typeof(RawImage));
            imageObject.transform.SetParent(_viewport, false);
            _imageRect = imageObject.GetComponent<RectTransform>();
            _imageRect.anchorMin = new Vector2(0.5f, 0.5f);
            _imageRect.anchorMax = new Vector2(0.5f, 0.5f);
            _imageRect.pivot = new Vector2(0.5f, 0.5f);
            _image = imageObject.GetComponent<RawImage>();
            _image.color = UnityColor.white;
            _image.raycastTarget = false;

            GameObject graphicObject = new GameObject(
                "TerrainLabClusteringGraphics",
                typeof(RectTransform));
            graphicObject.transform.SetParent(_imageRect, false);
            _graphicRoot = graphicObject.GetComponent<RectTransform>();
            _graphicRoot.anchorMin = Vector2.zero;
            _graphicRoot.anchorMax = Vector2.one;
            _graphicRoot.offsetMin = Vector2.zero;
            _graphicRoot.offsetMax = Vector2.zero;

            _coordinateLabel = CreateCoordinateOverlay(_viewport);
            CreatePanel(_root.transform);
            _root.SetActive(false);
        }

        private void CreatePanel(Transform parent)
        {
            GameObject panel = new GameObject(
                "TerrainLabClusteringPanel",
                typeof(RectTransform),
                typeof(Image),
                typeof(RectMask2D),
                typeof(ScrollRect));
            panel.transform.SetParent(parent, false);
            RectTransform rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(320f, 0f);
            rect.anchoredPosition = Vector2.zero;
            Image background = panel.GetComponent<Image>();
            background.sprite = SpriteTextureLoader.getSprite(
                "ui/special/darkInputFieldEmpty");
            background.type = Image.Type.Sliced;
            background.color =
                new UnityColor(0.02f, 0.02f, 0.018f, 0.99f);
            background.raycastTarget = true;

            GameObject contentObject = new GameObject(
                "TerrainLabClusteringPanelContent",
                typeof(RectTransform),
                typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter));
            contentObject.transform.SetParent(panel.transform, false);
            RectTransform contentRect =
                contentObject.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            ContentSizeFitter fitter =
                contentObject.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            ScrollRect scroll = panel.GetComponent<ScrollRect>();
            scroll.content = contentRect;
            scroll.viewport = rect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            VerticalLayoutGroup layout =
                contentObject.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(14, 14, 8, 12);
            layout.spacing = 5f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            Transform content = contentObject.transform;

            CreateLocalizedLabel(
                content,
                "terrain_lab_cluster_heading",
                18,
                FontStyle.Bold,
                30f,
                UnityColor.white);
            CreateLocalizedLabel(
                content,
                "terrain_lab_cluster_image_choice",
                11,
                FontStyle.Bold,
                18f,
                NeutralText());
            Transform navigation = CreateRow(content, 36f);
            _previousImageButton = CreateButton(
                navigation,
                "<",
                delegate { MovePendingImage(-1); },
                38f,
                "terrain_lab_cluster_previous_image");
            _fileLabel = CreateFileSelectorField(
                navigation,
                "terrain_lab_cluster_image_choice");
            _nextImageButton = CreateButton(
                navigation,
                ">",
                delegate { MovePendingImage(1); },
                38f,
                "terrain_lab_cluster_next_image");
            _openSelectedButton = CreateButton(
                content,
                LM.Get("terrain_lab_cluster_open_selected"),
                ConfirmImageSelection,
                292f,
                "terrain_lab_cluster_open_selected",
                true);
            TrackEditorControl(
                CreateButton(
                    content,
                    LM.Get("terrain_lab_cluster_fit"),
                    delegate
                    {
                        _zoom = 1f;
                        _imageRect.localScale = Vector3.one;
                        _imageRect.anchoredPosition = Vector2.zero;
                        FitImageToViewport(true);
                    },
                    292f,
                    "terrain_lab_cluster_fit"));

            CreateLocalizedLabel(
                content,
                "terrain_lab_output_size_heading",
                11,
                FontStyle.Bold,
                18f,
                NeutralText());
            Transform mapSizeRow = CreateRow(content, 34f);
            Text mapSizeCaption = CreateLocalizedLabel(
                mapSizeRow,
                "terrain_lab_output_long_side",
                9,
                FontStyle.Normal,
                34f,
                UnityColor.white);
            mapSizeCaption.alignment = TextAnchor.MiddleLeft;
            LayoutElement mapSizeCaptionElement =
                mapSizeCaption.GetComponent<LayoutElement>();
            mapSizeCaptionElement.preferredWidth = 108f;
            mapSizeCaptionElement.flexibleWidth = 1f;
            _longSideInput = TrackEditorControl(
                CreateInputField(
                    mapSizeRow,
                    "20",
                    "terrain_lab_output_long_side"));
            LayoutElement mapSizeInputElement =
                _longSideInput.GetComponent<LayoutElement>();
            mapSizeInputElement.preferredWidth = 48f;
            mapSizeInputElement.flexibleWidth = 0f;
            _longSideInput.characterLimit = 10;
            _longSideInput.onValueChanged.AddListener(
                delegate { UpdateMapSizePreview(); });
            _mapSizeLabel = CreateLabel(
                mapSizeRow,
                string.Empty,
                8,
                FontStyle.Normal,
                34f,
                new UnityColor(0.72f, 0.84f, 0.94f, 1f));
            _mapSizeLabel.alignment = TextAnchor.MiddleLeft;
            _mapSizeLabel.resizeTextForBestFit = true;
            _mapSizeLabel.resizeTextMinSize = 6;
            _mapSizeLabel.resizeTextMaxSize = 8;
            LayoutElement mapSizeLabelElement =
                _mapSizeLabel.GetComponent<LayoutElement>();
            mapSizeLabelElement.preferredWidth = 118f;
            mapSizeLabelElement.flexibleWidth = 1f;
            ConfigureTooltip(
                _mapSizeLabel.gameObject,
                "terrain_lab_output_long_side");

            CreateLocalizedLabel(
                content,
                "terrain_lab_cluster_algorithm_heading",
                11,
                FontStyle.Bold,
                18f,
                NeutralText());
            Transform algorithmModes =
                CreateFlexibleButtonRow(content, 32f);
            _legacyAlgorithmButton = TrackEditorControl(
                CreateFlexibleButton(
                    algorithmModes,
                    LM.Get("terrain_lab_cluster_algorithm_legacy"),
                    delegate
                    {
                        SetClusteringAlgorithm(
                            TerrainImageClusteringAlgorithms.LegacyAdaptive);
                    },
                    "terrain_lab_cluster_algorithm_legacy"));
            _semanticAlgorithmButton = TrackEditorControl(
                CreateFlexibleButton(
                    algorithmModes,
                    LM.Get("terrain_lab_cluster_algorithm_semantic"),
                    delegate
                    {
                        SetClusteringAlgorithm(
                            TerrainImageClusteringAlgorithms.Semantic);
                    },
                    "terrain_lab_cluster_algorithm_semantic"));

            CreateLocalizedLabel(
                content,
                "terrain_lab_cluster_boundary_heading",
                11,
                FontStyle.Bold,
                18f,
                NeutralText());
            Transform boundaryModes =
                CreateFlexibleButtonRow(content, 28f);
            _drawBoundaryButton = TrackEditorControl(
                CreateFlexibleButton(
                    boundaryModes,
                    LM.Get("terrain_lab_cluster_boundary_draw"),
                    StartBoundary,
                    "terrain_lab_cluster_boundary_draw"));
            _finishBoundaryButton = TrackEditorControl(
                CreateFlexibleButton(
                    boundaryModes,
                    LM.Get("terrain_lab_cluster_boundary_finish"),
                    FinishBoundary,
                    "terrain_lab_cluster_boundary_finish"));
            _cancelBoundaryButton = TrackEditorControl(
                CreateFlexibleButton(
                    boundaryModes,
                    LM.Get("terrain_lab_cluster_boundary_cancel"),
                    delegate { CancelBoundary(true); },
                    "terrain_lab_cluster_boundary_cancel"));
            _clearBoundaryButton = TrackEditorControl(
                CreateButton(
                    content,
                    LM.Get("terrain_lab_cluster_boundary_clear"),
                    ClearBoundary,
                    292f,
                    "terrain_lab_cluster_boundary_clear"));

            CreateLocalizedLabel(
                content,
                "terrain_lab_cluster_basic_heading",
                11,
                FontStyle.Bold,
                18f,
                NeutralText());
            CreateParameterRow(
                content,
                "clusters",
                "terrain_lab_cluster_clusters",
                "14");
            CreateParameterRow(
                content,
                "spline_radius",
                "terrain_lab_cluster_spline_radius",
                "0");
            CreateParameterRow(
                content,
                "smooth_passes",
                "terrain_lab_cluster_smooth_passes",
                "1");
            CreateParameterRow(
                content,
                "min_land_region",
                "terrain_lab_cluster_min_land_region",
                "32");
            CreateParameterRow(
                content,
                "water_sensitivity",
                "terrain_lab_cluster_water_sensitivity",
                "100");
            CreateParameterRow(
                content,
                "analysis_max_dimension",
                "terrain_lab_cluster_analysis_max_dimension",
                "2048");

            _clusterBudgetLabel = CreateLabel(
                content,
                string.Empty,
                10,
                FontStyle.Bold,
                26f,
                new UnityColor(1f, 0.82f, 0.22f, 1f));
            _clusterBudgetLabel.alignment = TextAnchor.MiddleCenter;
            _clusterBudgetLabel.resizeTextForBestFit = true;
            _clusterBudgetLabel.resizeTextMinSize = 7;
            _clusterBudgetLabel.resizeTextMaxSize = 10;
            ConfigureTooltip(
                _clusterBudgetLabel.gameObject,
                "terrain_lab_cluster_budget");

            _compositionButton = CreateButton(
                content,
                LM.Get("terrain_lab_cluster_composition_show"),
                ToggleCompositionPanel,
                292f,
                "terrain_lab_cluster_composition_toggle");
            _compositionPanel = CreateVerticalGroup(
                content,
                "TerrainLabClusteringCompositionPanel");
            Transform composition = _compositionPanel.transform;
            Transform compositionCategories =
                CreateFlexibleButtonRow(composition, 32f);
            _surfaceCompositionButton = CreateFlexibleButton(
                compositionCategories,
                LM.Get("terrain_lab_cluster_composition_surfaces"),
                delegate { SetCompositionCategory(true); },
                "terrain_lab_cluster_composition_surfaces");
            _biotopeCompositionButton = CreateFlexibleButton(
                compositionCategories,
                LM.Get("terrain_lab_cluster_composition_biotopes"),
                delegate { SetCompositionCategory(false); },
                "terrain_lab_cluster_composition_biotopes");
            _surfaceCompositionPanel = CreateCompositionPalette(
                composition,
                "TerrainLabClusteringSurfacePalette");
            foreach (TerrainImageClassOption option in
                     TerrainImageClassificationCatalog.Surfaces)
            {
                _surfaceToggles[option.Id] = CreateCompositionToggle(
                    _surfaceCompositionPanel.transform,
                    option.Id,
                    true,
                    option.LocalizationKey);
            }
            _biotopeCompositionPanel = CreateCompositionPalette(
                composition,
                "TerrainLabClusteringBiotopePalette");
            foreach (TerrainImageBiotopeOption option in
                     TerrainImageClassificationCatalog.SelectableBiotopes)
            {
                _biotopeToggles[option.Id] = CreateCompositionToggle(
                    _biotopeCompositionPanel.transform,
                    option.Id,
                    false,
                    option.LocalizationKey);
            }
            _surfaceCompositionPanel.SetActive(true);
            _biotopeCompositionPanel.SetActive(false);
            _compositionPanel.SetActive(false);

            _expertButton = CreateButton(
                content,
                LM.Get("terrain_lab_cluster_expert_show"),
                ToggleExpertPanel,
                292f,
                "terrain_lab_cluster_expert_toggle");
            _expertPanel = CreateVerticalGroup(
                content,
                "TerrainLabClusteringExpertPanel");
            Transform expert = _expertPanel.transform;
            CreateParameterRow(
                expert,
                "color_weight",
                "terrain_lab_cluster_color_weight",
                "100");
            CreateParameterRow(
                expert,
                "luma_weight",
                "terrain_lab_cluster_luma_weight",
                "100");
            CreateParameterRow(
                expert,
                "saturation_weight",
                "terrain_lab_cluster_saturation_weight",
                "100");
            CreateParameterRow(
                expert,
                "texture_weight",
                "terrain_lab_cluster_texture_weight",
                "0");
            CreateParameterRow(
                expert,
                "slope_weight",
                "terrain_lab_cluster_slope_weight",
                "100");
            CreateParameterRow(
                expert,
                "spatial_weight",
                "terrain_lab_cluster_spatial_weight",
                "0");
            CreateParameterRow(
                expert,
                "detail_weight",
                "terrain_lab_cluster_detail_weight",
                "65");
            CreateParameterRow(
                expert,
                "sample_limit",
                "terrain_lab_cluster_sample_limit",
                "60000");
            CreateParameterRow(
                expert,
                "kmeans_iterations",
                "terrain_lab_cluster_iterations",
                "18");
            CreateParameterRow(
                expert,
                "random_seed",
                "terrain_lab_cluster_seed",
                "1729");
            _expertPanel.SetActive(false);

            _summaryLabel = CreateLabel(
                content,
                string.Empty,
                10,
                FontStyle.Normal,
                34f,
                UnityColor.white);
            _panelStatus = CreateLabel(
                content,
                string.Empty,
                10,
                FontStyle.Normal,
                38f,
                NeutralText());

            Transform actions = CreateRow(content, 34f);
            TrackEditorControl(
                CreateButton(
                    actions,
                    LM.Get("terrain_lab_cluster_save_profile"),
                    SaveProfileCommand,
                    142f,
                    "terrain_lab_cluster_save_profile"));
            TrackEditorControl(
                CreateButton(
                    actions,
                    LM.Get("terrain_lab_cluster_build_map"),
                    QueueConversion,
                    142f,
                    "terrain_lab_cluster_build_map",
                    true));
            CreateButton(
                content,
                LM.Get("terrain_lab_cluster_close"),
                Hide,
                292f,
                "terrain_lab_cluster_close");

            SetEditorControlsEnabled(false);
            UpdateImageSelectorControls();
            UpdateBoundaryButtons();
        }

        private void OpenImage(int imageIndex)
        {
            if (imageIndex < 0 || imageIndex >= _images.Count)
            {
                return;
            }

            string path = _images[imageIndex];
            Texture2D nextTexture = TerrainImagePreviewLoader.Load(
                path,
                "TerrainLabAutomaticClusteringPreview",
                out int width,
                out int height);
            try
            {
                TerrainImageClusteringProfile nextProfile =
                    TerrainImageClusteringProfile.LoadOrCreate(
                        path,
                        width,
                        height);
                EnsureUi();
                ReleaseTexture();
                _texture = nextTexture;
                nextTexture = null;
                _image.texture = _texture;
                _imagePath = path;
                _imageIndex = imageIndex;
                _pendingImageIndex = imageIndex;
                _sourceWidth = width;
                _sourceHeight = height;
                _profile = nextProfile;
                _drawingBoundary = false;
                _draftVertices.Clear();
                _hoverVertex = null;
                _hasHoverVertex = false;
                _zoom = 1f;
                _imageRect.localScale = Vector3.one;
                _imageRect.anchoredPosition = Vector2.zero;
                _root.SetActive(true);
                BringToFront();
                PopulateParameterControls();
                PopulateCompositionControls();

                Canvas.ForceUpdateCanvases();
                FitImageToViewport(true);
                SetEditorControlsEnabled(true);
                RefreshBoundaryGraphics();
                UpdateSummary();
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_ready"),
                    false);
                VisibilityChanged?.Invoke();
                SetStatus(
                    LM.Get("terrain_lab_status_cluster_opened"),
                    false);
            }
            finally
            {
                if (nextTexture != null)
                {
                    Destroy(nextTexture);
                }
            }
        }

        private void InitializePendingImage(string previousPath)
        {
            int selected = string.IsNullOrWhiteSpace(previousPath)
                ? -1
                : _images.FindIndex(path => string.Equals(
                    path,
                    previousPath,
                    StringComparison.OrdinalIgnoreCase));
            _pendingImageIndex = selected >= 0
                ? selected
                : (_images.Count > 0 ? 0 : -1);
            UpdatePendingImageLabels();
            UpdateImageSelectorControls();
        }

        private void SelectPendingImage(int index)
        {
            if (index < 0 || index >= _images.Count)
            {
                return;
            }
            if (_draftVertices.Count > 0)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_finish_boundary_first"),
                    true);
                return;
            }

            _pendingImageIndex = index;
            if (_profile != null &&
                string.Equals(
                    _imagePath,
                    _images[index],
                    StringComparison.OrdinalIgnoreCase))
            {
                SetEditorControlsEnabled(true);
                UpdateSummary();
                return;
            }

            if (!SaveCurrentProfile(true))
            {
                return;
            }
            ResetLoadedImage();
            UpdatePendingImageLabels();
            SetPanelStatus(
                LM.Get("terrain_lab_cluster_select_and_confirm"),
                false);
            VisibilityChanged?.Invoke();
        }

        private void MovePendingImage(int direction)
        {
            if (_images.Count == 0)
            {
                return;
            }
            int current = _pendingImageIndex >= 0
                ? _pendingImageIndex
                : 0;
            int next = (current + direction) % _images.Count;
            if (next < 0)
            {
                next += _images.Count;
            }
            SelectPendingImage(next);
        }

        private void ConfirmImageSelection()
        {
            if (_pendingImageIndex < 0 ||
                _pendingImageIndex >= _images.Count)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_no_selection"),
                    true);
                return;
            }
            try
            {
                OpenImage(_pendingImageIndex);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void StartBoundary()
        {
            if (_profile == null)
            {
                return;
            }
            _drawingBoundary = true;
            _draftVertices.Clear();
            _hoverVertex = null;
            _hasHoverVertex = false;
            RefreshBoundaryGraphics();
            UpdateBoundaryButtons();
            SetPanelStatus(
                LM.Get("terrain_lab_cluster_boundary_drawing"),
                false);
        }

        private void FinishBoundary()
        {
            if (!_drawingBoundary)
            {
                return;
            }
            if (_draftVertices.Count < 3)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_boundary_need_vertices"),
                    true);
                return;
            }
            try
            {
                _profile.SetMapBoundary(_draftVertices);
                _profile.Save(_imagePath);
                _drawingBoundary = false;
                _draftVertices.Clear();
                _hoverVertex = null;
                _hasHoverVertex = false;
                RefreshBoundaryGraphics();
                UpdateSummary();
                UpdateBoundaryButtons();
                UpdateMapSizePreview();
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_boundary_saved"),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void CancelBoundary(bool notify)
        {
            _drawingBoundary = false;
            _draftVertices.Clear();
            _hoverVertex = null;
            _hasHoverVertex = false;
            RefreshBoundaryGraphics();
            UpdateSummary();
            UpdateBoundaryButtons();
            if (notify)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_boundary_cancelled"),
                    false);
            }
        }

        private void ClearBoundary()
        {
            if (_profile == null || !_profile.ClearMapBoundary())
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_boundary_missing"),
                    false);
                return;
            }
            try
            {
                _profile.Save(_imagePath);
                CancelBoundary(false);
                RefreshBoundaryGraphics();
                UpdateSummary();
                UpdateMapSizePreview();
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_boundary_cleared"),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void ToggleExpertPanel()
        {
            _expertVisible = !_expertVisible;
            if (_expertPanel != null)
            {
                _expertPanel.SetActive(_expertVisible);
            }
            if (_expertButton != null)
            {
                SetButtonLocalizationKey(
                    _expertButton,
                    _expertVisible
                        ? "terrain_lab_cluster_expert_hide"
                        : "terrain_lab_cluster_expert_show");
            }
            Canvas.ForceUpdateCanvases();
        }

        private void ToggleCompositionPanel()
        {
            _compositionVisible = !_compositionVisible;
            if (_compositionPanel != null)
            {
                _compositionPanel.SetActive(_compositionVisible);
            }
            if (_compositionVisible)
            {
                SetCompositionCategory(_surfaceCompositionVisible);
            }
            if (_compositionButton != null)
            {
                SetButtonLocalizationKey(
                    _compositionButton,
                    _compositionVisible
                        ? "terrain_lab_cluster_composition_hide"
                        : "terrain_lab_cluster_composition_show");
            }
            Canvas.ForceUpdateCanvases();
        }

        private void SetCompositionCategory(bool surfaces)
        {
            _surfaceCompositionVisible = surfaces;
            _surfaceCompositionPanel?.SetActive(surfaces);
            _biotopeCompositionPanel?.SetActive(!surfaces);
            SetModeButtonState(_surfaceCompositionButton, surfaces);
            SetModeButtonState(_biotopeCompositionButton, !surfaces);
            Canvas.ForceUpdateCanvases();
        }

        private void SetClusteringAlgorithm(string algorithmId)
        {
            if (_profile == null)
            {
                return;
            }
            _profile.Algorithm =
                TerrainImageClusteringAlgorithm.Create(algorithmId);
            UpdateAlgorithmButtons();
            UpdateSummary();
            SetPanelStatus(
                LM.Get(
                    string.Equals(
                        algorithmId,
                        TerrainImageClusteringAlgorithms.Semantic,
                        StringComparison.Ordinal)
                        ? "terrain_lab_cluster_algorithm_semantic_selected"
                        : "terrain_lab_cluster_algorithm_legacy_selected"),
                false);
        }

        private void UpdateAlgorithmButtons()
        {
            bool semantic = string.Equals(
                _profile?.Algorithm?.Id,
                TerrainImageClusteringAlgorithms.Semantic,
                StringComparison.Ordinal);
            SetModeButtonState(_legacyAlgorithmButton, !semantic);
            SetModeButtonState(_semanticAlgorithmButton, semantic);
        }

        private void SaveProfileCommand()
        {
            if (SaveCurrentProfile(true))
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_profile_saved"),
                    false);
            }
        }

        private void QueueConversion()
        {
            if (_profile == null || string.IsNullOrWhiteSpace(_imagePath))
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_no_selection"),
                    true);
                return;
            }
            if (_drawingBoundary)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_cluster_finish_boundary_first"),
                    true);
                return;
            }
            if (!SaveCurrentProfile(true))
            {
                return;
            }
            if (!_workspace.TryQueueImageNow(
                    _imagePath,
                    TerrainImageConversionMode.AutomaticClustering,
                    out string error))
            {
                SetPanelStatus(error, true);
                SetStatus(error, true);
                return;
            }
            SetPanelStatus(
                LM.Get("terrain_lab_cluster_queued"),
                false);
            SetStatus(
                LM.Get("terrain_lab_status_cluster_queued"),
                false);
        }

        private bool SaveCurrentProfile(bool reportError)
        {
            if (_profile == null || string.IsNullOrWhiteSpace(_imagePath))
            {
                return true;
            }
            try
            {
                ApplyParameterControls();
                _profile.Save(_imagePath);
                return true;
            }
            catch (Exception exception)
            {
                if (reportError)
                {
                    SetPanelStatus(exception.Message, true);
                    SetStatus(exception.Message, true);
                }
                return false;
            }
        }

        private void ApplyParameterControls()
        {
            ValidateParameterControls();
            TerrainImageClusteringSettings settings = _profile.Settings;
            settings.AnalysisMaximumDimension =
                ReadInteger("analysis_max_dimension", 512, 4096);
            settings.LongSideBlocks = ReadLongSideBlocks();
            settings.Clusters = ReadInteger("clusters", 4, 64);
            settings.SplineRadius = ReadInteger("spline_radius", 0, 12);
            settings.SmoothPasses = ReadInteger("smooth_passes", 0, 8);
            settings.MinimumLandRegion =
                ReadInteger("min_land_region", 0, 4096);
            settings.WaterSensitivity =
                ReadInteger("water_sensitivity", 50, 200) / 100.0;
            settings.ColorWeight =
                ReadInteger("color_weight", 0, 300) / 100.0;
            settings.LumaWeight =
                ReadInteger("luma_weight", 0, 300) / 100.0;
            settings.SaturationWeight =
                ReadInteger("saturation_weight", 0, 300) / 100.0;
            settings.TextureWeight =
                ReadInteger("texture_weight", 0, 300) / 100.0;
            settings.SlopeWeight =
                ReadInteger("slope_weight", 0, 300) / 100.0;
            settings.SpatialWeight =
                ReadInteger("spatial_weight", 0, 300) / 100.0;
            settings.DetailWeight =
                ReadInteger("detail_weight", 0, 100) / 100.0;
            settings.SampleLimit =
                ReadInteger("sample_limit", 1000, 250000);
            settings.KMeansIterations =
                ReadInteger("kmeans_iterations", 1, 100);
            settings.RandomSeed =
                ReadInteger("random_seed", 0, int.MaxValue);
            _profile.Composition.AllowedSurfaces =
                _surfaceToggles
                    .Where(pair => pair.Value != null && pair.Value.isOn)
                    .Select(pair => pair.Key)
                    .ToList();
            _profile.Composition.AllowedBiotopes =
                _biotopeToggles
                    .Where(pair => pair.Value != null && pair.Value.isOn)
                    .Select(pair => pair.Key)
                    .ToList();
            _profile.Validate(_sourceWidth, _sourceHeight);
            UpdateSummary();
        }

        private int ReadInteger(string id, int minimum, int maximum)
        {
            if (!TryReadIntegerInput(
                    id,
                    minimum,
                    maximum,
                    out int value))
            {
                throw CreateInvalidParameterException(
                    id,
                    minimum,
                    maximum);
            }
            return value;
        }

        private void ValidateParameterControls()
        {
            List<string> invalid = new List<string>();
            bool longSideValid = TryReadLongSideBlocks(out _);
            SetInputValidationState(_longSideInput, longSideValid);
            if (!longSideValid)
            {
                invalid.Add(LM.Get("terrain_lab_output_long_side"));
            }

            foreach (KeyValuePair<string, InputField> pair in _parameterInputs)
            {
                if (!TryGetParameterRange(
                        pair.Key,
                        out int minimum,
                        out int maximum))
                {
                    continue;
                }
                bool valid = TryReadIntegerInput(
                    pair.Key,
                    minimum,
                    maximum,
                    out _);
                SetInputValidationState(pair.Value, valid);
                if (valid)
                {
                    continue;
                }

                string localizationKey =
                    _parameterLocalizationKeys.TryGetValue(
                        pair.Key,
                        out string key)
                        ? key
                        : pair.Key;
                invalid.Add(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} ({1}..{2})",
                        LM.Get(localizationKey),
                        minimum,
                        maximum));
            }

            if (invalid.Count > 0)
            {
                throw new InvalidDataException(
                    string.Format(
                        LM.Get(
                            "terrain_lab_cluster_invalid_fields_format"),
                        string.Join(", ", invalid)));
            }
        }

        private bool TryReadIntegerInput(
            string id,
            int minimum,
            int maximum,
            out int value)
        {
            value = 0;
            return _parameterInputs.TryGetValue(
                       id,
                       out InputField input) &&
                   int.TryParse(
                       input.text,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out value) &&
                   value >= minimum &&
                   value <= maximum;
        }

        private InvalidDataException CreateInvalidParameterException(
            string id,
            int minimum,
            int maximum)
        {
            string localizationKey =
                _parameterLocalizationKeys.TryGetValue(
                    id,
                    out string key)
                    ? key
                    : id;
            return new InvalidDataException(
                string.Format(
                    LM.Get("terrain_lab_cluster_invalid_fields_format"),
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} ({1}..{2})",
                        LM.Get(localizationKey),
                        minimum,
                        maximum)));
        }

        private static bool TryGetParameterRange(
            string id,
            out int minimum,
            out int maximum)
        {
            switch (id)
            {
                case "analysis_max_dimension":
                    minimum = 512;
                    maximum = 4096;
                    return true;
                case "clusters":
                    minimum = 4;
                    maximum = 64;
                    return true;
                case "spline_radius":
                    minimum = 0;
                    maximum = 12;
                    return true;
                case "smooth_passes":
                    minimum = 0;
                    maximum = 8;
                    return true;
                case "min_land_region":
                    minimum = 0;
                    maximum = 4096;
                    return true;
                case "water_sensitivity":
                    minimum = 50;
                    maximum = 200;
                    return true;
                case "color_weight":
                case "luma_weight":
                case "saturation_weight":
                case "texture_weight":
                case "slope_weight":
                case "spatial_weight":
                    minimum = 0;
                    maximum = 300;
                    return true;
                case "detail_weight":
                    minimum = 0;
                    maximum = 100;
                    return true;
                case "sample_limit":
                    minimum = 1000;
                    maximum = 250000;
                    return true;
                case "kmeans_iterations":
                    minimum = 1;
                    maximum = 100;
                    return true;
                case "random_seed":
                    minimum = 0;
                    maximum = int.MaxValue;
                    return true;
                default:
                    minimum = 0;
                    maximum = 0;
                    return false;
            }
        }

        private void PopulateParameterControls()
        {
            TerrainImageClusteringSettings settings = _profile.Settings;
            UpdateAlgorithmButtons();
            _longSideInput?.SetTextWithoutNotify(
                settings.LongSideBlocks.ToString(
                    CultureInfo.InvariantCulture));
            UpdateMapSizePreview();
            SetParameter(
                "analysis_max_dimension",
                settings.AnalysisMaximumDimension);
            SetParameter("clusters", settings.Clusters);
            SetParameter("spline_radius", settings.SplineRadius);
            SetParameter("smooth_passes", settings.SmoothPasses);
            SetParameter("min_land_region", settings.MinimumLandRegion);
            SetParameter(
                "water_sensitivity",
                Percent(settings.WaterSensitivity));
            SetParameter("color_weight", Percent(settings.ColorWeight));
            SetParameter("luma_weight", Percent(settings.LumaWeight));
            SetParameter(
                "saturation_weight",
                Percent(settings.SaturationWeight));
            SetParameter("texture_weight", Percent(settings.TextureWeight));
            SetParameter("slope_weight", Percent(settings.SlopeWeight));
            SetParameter("spatial_weight", Percent(settings.SpatialWeight));
            SetParameter("detail_weight", Percent(settings.DetailWeight));
            SetParameter("sample_limit", settings.SampleLimit);
            SetParameter("kmeans_iterations", settings.KMeansIterations);
            SetParameter("random_seed", settings.RandomSeed);
            RefreshParameterValidationVisuals();
        }

        private void PopulateCompositionControls()
        {
            HashSet<string> surfaces = new HashSet<string>(
                _profile.Composition.AllowedSurfaces,
                StringComparer.Ordinal);
            foreach (KeyValuePair<string, Toggle> pair in _surfaceToggles)
            {
                pair.Value?.SetIsOnWithoutNotify(
                    surfaces.Contains(pair.Key));
                UpdateCompositionToggleVisual(pair.Value);
            }

            HashSet<string> biotopes = new HashSet<string>(
                _profile.Composition.AllowedBiotopes,
                StringComparer.Ordinal);
            foreach (KeyValuePair<string, Toggle> pair in _biotopeToggles)
            {
                pair.Value?.SetIsOnWithoutNotify(
                    biotopes.Contains(pair.Key));
                UpdateCompositionToggleVisual(pair.Value);
            }
            UpdateClusterBudgetLabel();
        }

        private static int Percent(double value)
        {
            return (int)Math.Round(
                value * 100.0,
                MidpointRounding.AwayFromZero);
        }

        private void SetParameter(string id, int value)
        {
            if (_parameterInputs.TryGetValue(id, out InputField input))
            {
                input.SetTextWithoutNotify(
                    value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void ResetLoadedImage()
        {
            CancelBoundary(false);
            if (_image != null)
            {
                _image.texture = null;
            }
            ReleaseTexture();
            _profile = null;
            _imagePath = null;
            _imageIndex = -1;
            _sourceWidth = 0;
            _sourceHeight = 0;
            DestroyBoundaryGraphics();
            if (_coordinateLabel != null)
            {
                _coordinateLabel.text = "X -  Y -";
            }
            if (_mapSizeLabel != null)
            {
                _mapSizeLabel.text = string.Empty;
            }
            if (_clusterBudgetLabel != null)
            {
                _clusterBudgetLabel.text = string.Empty;
            }
            SetEditorControlsEnabled(false);
        }

        private int ReadLongSideBlocks()
        {
            if (!TryReadLongSideBlocks(out int value))
            {
                SetInputValidationState(_longSideInput, false);
                throw new InvalidDataException(
                    LM.Get("terrain_lab_output_size_error"));
            }
            SetInputValidationState(_longSideInput, true);
            return value;
        }

        private bool TryReadLongSideBlocks(out int value)
        {
            value = 0;
            GetOutputAspectDimensions(
                out int aspectWidth,
                out int aspectHeight);
            return _longSideInput != null &&
                   int.TryParse(
                       _longSideInput.text,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out value) &&
                   TerrainMapLimits.TryGetBlockDimensions(
                       aspectWidth,
                       aspectHeight,
                       value,
                       out _,
                       out _);
        }

        private void UpdateMapSizePreview()
        {
            if (_mapSizeLabel == null)
            {
                return;
            }
            GetOutputAspectDimensions(
                out int aspectWidth,
                out int aspectHeight);
            if (!TerrainMapLimits.TryGetRecommendedBlockDimensions(
                    aspectWidth,
                    aspectHeight,
                    out int recommendedWidth,
                    out int recommendedHeight))
            {
                _mapSizeLabel.text = string.Empty;
                return;
            }

            int width = 0;
            int height = 0;
            bool valid =
                _longSideInput != null &&
                int.TryParse(
                    _longSideInput.text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int longSide) &&
                TerrainMapLimits.TryGetBlockDimensions(
                    aspectWidth,
                    aspectHeight,
                    longSide,
                    out width,
                    out height);
            bool aboveRecommendation =
                valid &&
                TerrainMapLimits.IsAboveRecommendedBudgetForBlocks(
                    width,
                    height);
            string sizeText = valid
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    LM.Get(
                        aboveRecommendation
                            ? "terrain_lab_output_size_warning_format"
                            : "terrain_lab_output_size_preview_format"),
                    width,
                    height,
                    recommendedWidth,
                    recommendedHeight)
                : string.Format(
                    CultureInfo.InvariantCulture,
                    LM.Get("terrain_lab_output_size_maximum_format"),
                    recommendedWidth,
                    recommendedHeight);
            _mapSizeLabel.text =
                sizeText + "  " +
                string.Format(
                    CultureInfo.InvariantCulture,
                    LM.Get(
                        _profile?.MapBoundary == null
                            ? "terrain_lab_output_size_scope_source_format"
                            : "terrain_lab_output_size_scope_extent_format"),
                    aspectWidth,
                    aspectHeight);
            _mapSizeLabel.color = !valid
                ? new UnityColor(0.95f, 0.52f, 0.46f, 1f)
                : aboveRecommendation
                    ? new UnityColor(1f, 0.72f, 0.24f, 1f)
                    : new UnityColor(0.72f, 0.84f, 0.94f, 1f);
            SetInputValidationState(_longSideInput, valid);
            if (valid && aboveRecommendation)
            {
                Image background = _longSideInput?.GetComponent<Image>();
                if (background != null)
                {
                    background.color =
                        new UnityColor(0.28f, 0.18f, 0.035f, 1f);
                }
            }
        }

        private void GetOutputAspectDimensions(
            out int width,
            out int height)
        {
            if (_profile != null)
            {
                _profile.GetOutputAspectDimensions(out width, out height);
                return;
            }
            width = Math.Max(1, _sourceWidth);
            height = Math.Max(1, _sourceHeight);
        }

        private void UpdatePendingImageLabels()
        {
            if (_pendingImageIndex < 0 ||
                _pendingImageIndex >= _images.Count)
            {
                return;
            }
            _fileLabel.text = Path.GetFileName(_images[_pendingImageIndex]);
            _summaryLabel.text =
                LM.Get("terrain_lab_cluster_select_and_confirm");
        }

        private void UpdateImageSelectorControls()
        {
            if (_previousImageButton != null)
            {
                _previousImageButton.interactable = _images.Count > 1;
            }
            if (_nextImageButton != null)
            {
                _nextImageButton.interactable = _images.Count > 1;
            }
            if (_openSelectedButton != null)
            {
                _openSelectedButton.interactable =
                    _pendingImageIndex >= 0 &&
                    _pendingImageIndex < _images.Count;
            }
        }

        private void SetEditorControlsEnabled(bool enabled)
        {
            foreach (Selectable control in _editorControls)
            {
                if (control != null)
                {
                    control.interactable = enabled;
                }
            }
            UpdateBoundaryButtons();
        }

        private void UpdateBoundaryButtons()
        {
            bool ready = _profile != null;
            if (_drawBoundaryButton != null)
            {
                _drawBoundaryButton.interactable =
                    ready && !_drawingBoundary;
                SetModeButtonState(
                    _drawBoundaryButton,
                    _drawingBoundary);
            }
            if (_finishBoundaryButton != null)
            {
                _finishBoundaryButton.interactable =
                    ready && _drawingBoundary && _draftVertices.Count >= 3;
            }
            if (_cancelBoundaryButton != null)
            {
                _cancelBoundaryButton.interactable =
                    ready && _drawingBoundary;
            }
            if (_clearBoundaryButton != null)
            {
                _clearBoundaryButton.interactable =
                    ready && HasBoundary;
            }
        }

        private void UpdateSummary()
        {
            if (_profile == null)
            {
                return;
            }
            _fileLabel.text = string.Format(
                LM.Get("terrain_lab_cluster_source_format"),
                Path.GetFileName(_imagePath),
                _sourceWidth,
                _sourceHeight);
            _summaryLabel.text = string.Format(
                LM.Get("terrain_lab_cluster_summary_format"),
                _profile.Settings.Clusters,
                _profile.Settings.SplineRadius,
                _profile.MapBoundary?.Vertices?.Count ?? 0,
                _draftVertices.Count,
                LM.Get(
                    string.Equals(
                        _profile.Algorithm?.Id,
                        TerrainImageClusteringAlgorithms.Semantic,
                        StringComparison.Ordinal)
                        ? "terrain_lab_cluster_algorithm_semantic"
                        : "terrain_lab_cluster_algorithm_legacy"));
            UpdateBoundaryButtons();
            UpdateClusterBudgetLabel();
        }

        private void UpdateClusterBudgetLabel()
        {
            if (_clusterBudgetLabel == null)
            {
                return;
            }
            if (_profile == null)
            {
                _clusterBudgetLabel.text = string.Empty;
                return;
            }

            bool budgetValid = TryReadIntegerInput(
                "clusters",
                4,
                64,
                out int budget);
            if (!budgetValid)
            {
                budget = _profile.Settings.Clusters;
            }

            HashSet<string> surfaces = new HashSet<string>(
                _surfaceToggles
                    .Where(pair =>
                        pair.Value != null &&
                        pair.Value.isOn)
                    .Select(pair => pair.Key),
                StringComparer.Ordinal);
            int biotopeCount = _biotopeToggles.Count(pair =>
                pair.Value != null &&
                pair.Value.isOn);
            int candidates = CountCompositionCandidateClasses(
                surfaces,
                biotopeCount);
            int effective = Math.Min(budget, candidates);
            _clusterBudgetLabel.text = string.Format(
                CultureInfo.InvariantCulture,
                LM.Get("terrain_lab_cluster_budget_format"),
                effective,
                budget,
                candidates);
            _clusterBudgetLabel.color =
                budgetValid && candidates > 0
                    ? new UnityColor(1f, 0.82f, 0.22f, 1f)
                    : new UnityColor(1f, 0.42f, 0.36f, 1f);
        }

        private static int CountCompositionCandidateClasses(
            ISet<string> surfaces,
            int biotopeCount)
        {
            int count = 0;
            if (surfaces.Contains("deep_ocean"))
            {
                count++;
            }
            if (surfaces.Contains("shelf"))
            {
                count++;
            }
            if (surfaces.Contains("shallow_water") ||
                surfaces.Contains("river_lake"))
            {
                count++;
            }
            if (surfaces.Contains("sand"))
            {
                count++;
            }
            if (surfaces.Contains("plain") ||
                surfaces.Contains("lowland") ||
                surfaces.Contains("depression"))
            {
                count += biotopeCount;
            }
            if (surfaces.Contains("upland"))
            {
                count += biotopeCount;
            }
            if (surfaces.Contains("hills"))
            {
                count++;
            }
            if (surfaces.Contains("rocks") ||
                surfaces.Contains("summit"))
            {
                count++;
            }
            return count;
        }

        private void RefreshParameterValidationVisuals()
        {
            foreach (string id in _parameterInputs.Keys)
            {
                UpdateParameterValidationVisual(id);
            }
            UpdateMapSizePreview();
        }

        private void UpdateParameterValidationVisual(string id)
        {
            if (!_parameterInputs.TryGetValue(
                    id,
                    out InputField input) ||
                !TryGetParameterRange(
                    id,
                    out int minimum,
                    out int maximum))
            {
                return;
            }
            SetInputValidationState(
                input,
                TryReadIntegerInput(
                    id,
                    minimum,
                    maximum,
                    out _));
            if (string.Equals(
                    id,
                    "clusters",
                    StringComparison.Ordinal))
            {
                UpdateClusterBudgetLabel();
            }
        }

        private static void SetInputValidationState(
            InputField input,
            bool valid)
        {
            Image background = input?.GetComponent<Image>();
            if (background != null)
            {
                background.color = valid
                    ? new UnityColor(0.06f, 0.06f, 0.055f, 1f)
                    : new UnityColor(0.38f, 0.075f, 0.055f, 1f);
            }
        }

        private void UpdateCoordinateLabel(int x, int y)
        {
            if (_coordinateLabel != null)
            {
                _coordinateLabel.text = string.Format(
                    CultureInfo.InvariantCulture,
                    "X {0}  Y {1}",
                    x,
                    y);
            }
        }

        private bool TryScreenToSource(
            Vector2 screenPosition,
            out int sourceX,
            out int sourceY)
        {
            sourceX = 0;
            sourceY = 0;
            if (_imageRect == null ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _imageRect,
                    screenPosition,
                    null,
                    out Vector2 local))
            {
                return false;
            }

            Rect rect = _imageRect.rect;
            float normalizedX = Mathf.InverseLerp(
                rect.xMin,
                rect.xMax,
                local.x);
            float normalizedY = Mathf.InverseLerp(
                rect.yMax,
                rect.yMin,
                local.y);
            if (normalizedX < 0f || normalizedX > 1f ||
                normalizedY < 0f || normalizedY > 1f)
            {
                return false;
            }
            sourceX = Mathf.Clamp(
                Mathf.FloorToInt(normalizedX * _sourceWidth),
                0,
                _sourceWidth - 1);
            sourceY = Mathf.Clamp(
                Mathf.FloorToInt(normalizedY * _sourceHeight),
                0,
                _sourceHeight - 1);
            return true;
        }

        private void FitImageToViewport(bool resetPan)
        {
            if (_viewport == null || _sourceWidth <= 0 || _sourceHeight <= 0)
            {
                return;
            }
            Vector2 viewportSize = _viewport.rect.size;
            if (viewportSize.x <= 1f || viewportSize.y <= 1f)
            {
                return;
            }

            float scale = Mathf.Min(
                viewportSize.x / _sourceWidth,
                viewportSize.y / _sourceHeight);
            _imageRect.sizeDelta = new Vector2(
                Mathf.Max(1f, _sourceWidth * scale),
                Mathf.Max(1f, _sourceHeight * scale));
            if (resetPan)
            {
                _imageRect.anchoredPosition = Vector2.zero;
            }
            _lastViewportSize = viewportSize;
            ClampPan();
            UpdateGraphicScale();
        }

        private void ClampPan()
        {
            if (_imageRect == null || _viewport == null)
            {
                return;
            }
            Vector2 scaledSize = _imageRect.rect.size * _zoom;
            Vector2 viewportSize = _viewport.rect.size;
            float limitX =
                Mathf.Max(0f, (scaledSize.x - viewportSize.x) * 0.5f);
            float limitY =
                Mathf.Max(0f, (scaledSize.y - viewportSize.y) * 0.5f);
            Vector2 position = _imageRect.anchoredPosition;
            position.x = Mathf.Clamp(position.x, -limitX, limitX);
            position.y = Mathf.Clamp(position.y, -limitY, limitY);
            _imageRect.anchoredPosition = position;
        }

        private void RefreshBoundaryGraphics()
        {
            if (_graphicRoot == null)
            {
                return;
            }

            IReadOnlyList<TerrainImageClassificationVertex> saved =
                _profile?.MapBoundary?.Vertices;
            if (saved != null && saved.Count >= 3)
            {
                if (_savedBoundaryGraphic == null)
                {
                    _savedBoundaryGraphic = CreateBoundaryGraphic(
                        "TerrainLabClusteringSavedBoundary");
                }
                ConfigureBoundaryGraphic(
                    _savedBoundaryGraphic,
                    saved,
                    true,
                    false,
                    0.035f);
            }
            else if (_savedBoundaryGraphic != null)
            {
                Destroy(_savedBoundaryGraphic.gameObject);
                _savedBoundaryGraphic = null;
            }

            if (_draftVertices.Count == 0)
            {
                if (_draftBoundaryGraphic != null)
                {
                    Destroy(_draftBoundaryGraphic.gameObject);
                    _draftBoundaryGraphic = null;
                }
                return;
            }

            List<TerrainImageClassificationVertex> display =
                new List<TerrainImageClassificationVertex>(_draftVertices);
            TerrainImageClassificationVertex last =
                display[display.Count - 1];
            if (_hasHoverVertex && _hoverVertex != null &&
                (last.X != _hoverVertex.X || last.Y != _hoverVertex.Y))
            {
                display.Add(_hoverVertex);
            }
            if (_draftBoundaryGraphic == null)
            {
                _draftBoundaryGraphic = CreateBoundaryGraphic(
                    "TerrainLabClusteringDraftBoundary");
            }
            ConfigureBoundaryGraphic(
                _draftBoundaryGraphic,
                display,
                false,
                true,
                0.08f);
        }

        private TerrainImagePolygonGraphic CreateBoundaryGraphic(
            string objectName)
        {
            GameObject polygonObject = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TerrainImagePolygonGraphic));
            polygonObject.transform.SetParent(_graphicRoot, false);
            RectTransform rect = polygonObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return polygonObject.GetComponent<TerrainImagePolygonGraphic>();
        }

        private void ConfigureBoundaryGraphic(
            TerrainImagePolygonGraphic graphic,
            IEnumerable<TerrainImageClassificationVertex> vertices,
            bool closed,
            bool showVertices,
            float fillAlpha)
        {
            UnityColor cyan = new UnityColor(0.12f, 0.95f, 1f, 1f);
            graphic.Configure(
                vertices,
                _sourceWidth,
                _sourceHeight,
                "map_boundary",
                cyan,
                cyan,
                closed,
                showVertices,
                1f / Mathf.Max(_zoom, 0.01f),
                fillAlpha,
                2.4f);
        }

        private void UpdateGraphicScale()
        {
            float inverse = 1f / Mathf.Max(_zoom, 0.01f);
            _savedBoundaryGraphic?.SetInverseZoom(inverse);
            _draftBoundaryGraphic?.SetInverseZoom(inverse);
        }

        private void DestroyBoundaryGraphics()
        {
            if (_savedBoundaryGraphic != null)
            {
                Destroy(_savedBoundaryGraphic.gameObject);
                _savedBoundaryGraphic = null;
            }
            if (_draftBoundaryGraphic != null)
            {
                Destroy(_draftBoundaryGraphic.gameObject);
                _draftBoundaryGraphic = null;
            }
        }

        private void CreateParameterRow(
            Transform parent,
            string id,
            string localizationKey,
            string defaultValue)
        {
            Transform row = CreateRow(parent, 28f);
            ConfigureTooltip(row.gameObject, localizationKey);

            Text label = CreateLocalizedLabel(
                row,
                localizationKey,
                10,
                FontStyle.Normal,
                28f,
                UnityColor.white);
            LayoutElement labelElement =
                label.GetComponent<LayoutElement>();
            labelElement.preferredWidth = 174f;
            label.alignment = TextAnchor.MiddleLeft;

            InputField input = CreateInputField(
                row,
                defaultValue,
                localizationKey);
            LayoutElement inputElement =
                input.GetComponent<LayoutElement>();
            inputElement.preferredWidth = 104f;
            input.characterLimit = id == "random_seed" ? 10 : 7;
            input.onValueChanged.AddListener(
                delegate { UpdateParameterValidationVisual(id); });
            input.onEndEdit.AddListener(
                delegate
                {
                    if (_profile == null)
                    {
                        return;
                    }
                    try
                    {
                        ApplyParameterControls();
                        SetPanelStatus(
                            LM.Get("terrain_lab_cluster_parameter_applied"),
                            false);
                    }
                    catch (Exception exception)
                    {
                        SetPanelStatus(exception.Message, true);
                    }
                });
            _parameterInputs[id] = input;
            _parameterLocalizationKeys[id] = localizationKey;
            TrackEditorControl(input);
        }

        private static GameObject CreateVerticalGroup(
            Transform parent,
            string objectName)
        {
            GameObject group = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter));
            group.transform.SetParent(parent, false);
            VerticalLayoutGroup layout =
                group.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 5f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            ContentSizeFitter fitter =
                group.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return group;
        }

        private static GameObject CreateCompositionPalette(
            Transform parent,
            string objectName)
        {
            GameObject palette = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(Image),
                typeof(GridLayoutGroup),
                typeof(ContentSizeFitter));
            palette.transform.SetParent(parent, false);
            Image background = palette.GetComponent<Image>();
            Image nativePanel = ToolbarButtons.instance?.main_background;
            background.sprite = nativePanel?.sprite ??
                                SpriteTextureLoader.getSprite(
                                    "ui/special/darkInputFieldEmpty");
            background.type = Image.Type.Sliced;
            background.material = nativePanel?.material;
            background.color = nativePanel != null
                ? nativePanel.color
                : new UnityColor(0.20f, 0.22f, 0.18f, 0.98f);

            GridLayoutGroup grid = palette.GetComponent<GridLayoutGroup>();
            grid.padding = new RectOffset(7, 7, 7, 7);
            grid.cellSize = new Vector2(34f, 34f);
            grid.spacing = new Vector2(6f, 6f);
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 7;

            ContentSizeFitter fitter =
                palette.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return palette;
        }

        private Toggle CreateCompositionToggle(
            Transform parent,
            string optionId,
            bool surface,
            string tooltipKey)
        {
            GameObject toggleObject = new GameObject(
                "TerrainLabClusteringComposition_" + optionId,
                typeof(RectTransform),
                typeof(Image),
                typeof(Toggle));
            toggleObject.transform.SetParent(parent, false);
            Image background = toggleObject.GetComponent<Image>();
            background.sprite = ToolbarButtons.getSpriteButtonNormal();
            background.type = Image.Type.Sliced;
            background.color =
                new UnityColor(0.30f, 0.32f, 0.27f, 1f);

            Toggle toggle = toggleObject.GetComponent<Toggle>();
            toggle.isOn = true;
            toggle.targetGraphic = background;
            toggle.transition = Selectable.Transition.ColorTint;

            GameObject iconObject = new GameObject(
                "TerrainLabCompositionIcon",
                typeof(RectTransform),
                typeof(Image));
            iconObject.transform.SetParent(toggleObject.transform, false);
            RectTransform iconRect =
                iconObject.GetComponent<RectTransform>();
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = new Vector2(4f, 4f);
            iconRect.offsetMax = new Vector2(-4f, -4f);
            Image icon = iconObject.GetComponent<Image>();
            icon.sprite = surface
                ? TerrainImageUiVisuals.GetSurfaceSprite(optionId)
                : TerrainImageUiVisuals.GetBiotopeSprite(optionId);
            icon.preserveAspect = false;
            icon.raycastTarget = false;

            GameObject lampObject = new GameObject(
                "TerrainLabCompositionSelectionLamp",
                typeof(RectTransform),
                typeof(Image));
            lampObject.transform.SetParent(toggleObject.transform, false);
            RectTransform lampRect =
                lampObject.GetComponent<RectTransform>();
            lampRect.anchorMin = new Vector2(0.5f, 1f);
            lampRect.anchorMax = new Vector2(0.5f, 1f);
            lampRect.pivot = new Vector2(0.5f, 0.5f);
            lampRect.anchoredPosition = new Vector2(0f, 1f);
            lampRect.sizeDelta = new Vector2(6f, 6f);
            Image lamp = lampObject.GetComponent<Image>();
            lamp.sprite = TerrainImageUiVisuals.GetActivitySprite(true);
            lamp.preserveAspect = true;
            lamp.raycastTarget = false;
            toggle.graphic = lamp;

            toggle.onValueChanged.AddListener(
                delegate(bool _)
                {
                    UpdateCompositionToggleVisual(toggle);
                    UpdateClusterBudgetLabel();
                });
            ConfigureTooltip(toggleObject, tooltipKey);
            TrackEditorControl(toggle);
            UpdateCompositionToggleVisual(toggle);
            return toggle;
        }

        private static void UpdateCompositionToggleVisual(Toggle toggle)
        {
            if (toggle == null)
            {
                return;
            }
            Image background = toggle.targetGraphic as Image;
            if (background != null)
            {
                background.color = toggle.isOn
                    ? new UnityColor(0.32f, 0.58f, 0.29f, 1f)
                    : new UnityColor(0.30f, 0.32f, 0.27f, 1f);
            }
            if (toggle.graphic is Image lamp)
            {
                lamp.sprite =
                    TerrainImageUiVisuals.GetActivitySprite(toggle.isOn);
                lamp.gameObject.SetActive(toggle.isOn);
            }
        }

        private static Text CreateCoordinateOverlay(Transform parent)
        {
            GameObject backgroundObject = new GameObject(
                "TerrainLabClusterImageCoordinates",
                typeof(RectTransform),
                typeof(Image));
            backgroundObject.transform.SetParent(parent, false);
            RectTransform rect =
                backgroundObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(176f, 23f);
            rect.anchoredPosition = new Vector2(0f, 6f);
            Image background = backgroundObject.GetComponent<Image>();
            background.color =
                new UnityColor(0.01f, 0.01f, 0.01f, 0.86f);
            background.raycastTarget = false;

            GameObject textObject = new GameObject(
                "TerrainLabClusterImageCoordinatesText",
                typeof(RectTransform),
                typeof(Text));
            textObject.transform.SetParent(backgroundObject.transform, false);
            RectTransform textRect =
                textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(6f, 1f);
            textRect.offsetMax = new Vector2(-6f, -1f);
            Text text = textObject.GetComponent<Text>();
            text.font = LocalizedTextManager.current_font;
            text.text = "X -  Y -";
            text.fontSize = 11;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new UnityColor(0.72f, 0.84f, 0.94f, 1f);
            text.raycastTarget = false;
            return text;
        }

        private static Transform CreateRow(Transform parent, float height)
        {
            GameObject row = new GameObject(
                "TerrainLabClusteringRow",
                typeof(RectTransform),
                typeof(HorizontalLayoutGroup),
                typeof(LayoutElement));
            row.transform.SetParent(parent, false);
            HorizontalLayoutGroup layout =
                row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            LayoutElement element = row.GetComponent<LayoutElement>();
            element.preferredHeight = height;
            return row.transform;
        }

        private static Transform CreateFlexibleButtonRow(
            Transform parent,
            float height)
        {
            Transform row = CreateRow(parent, height);
            HorizontalLayoutGroup layout =
                row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            return row;
        }

        private static Text CreateLabel(
            Transform parent,
            string value,
            int fontSize,
            FontStyle style,
            float height,
            UnityColor color)
        {
            GameObject labelObject = new GameObject(
                "TerrainLabClusteringLabel",
                typeof(RectTransform),
                typeof(Text),
                typeof(LayoutElement));
            labelObject.transform.SetParent(parent, false);
            Text label = labelObject.GetComponent<Text>();
            label.font = LocalizedTextManager.current_font;
            label.text = value;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = color;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.raycastTarget = false;
            LayoutElement element =
                labelObject.GetComponent<LayoutElement>();
            element.preferredHeight = height;
            return label;
        }

        private static Text CreateLocalizedLabel(
            Transform parent,
            string localizationKey,
            int fontSize,
            FontStyle style,
            float height,
            UnityColor color)
        {
            return TerrainLocalizedUi.Bind(
                CreateLabel(
                    parent,
                    LM.Get(localizationKey),
                    fontSize,
                    style,
                    height,
                    color),
                localizationKey);
        }

        private Text CreateFileSelectorField(
            Transform parent,
            string tooltipKey)
        {
            GameObject fieldObject = new GameObject(
                "TerrainLabClusteringFileSelector",
                typeof(RectTransform),
                typeof(Image),
                typeof(LayoutElement));
            fieldObject.transform.SetParent(parent, false);
            Image background = fieldObject.GetComponent<Image>();
            background.sprite = SpriteTextureLoader.getSprite(
                "ui/special/darkInputFieldEmpty");
            background.type = Image.Type.Sliced;
            background.color =
                new UnityColor(0.01f, 0.01f, 0.01f, 1f);
            background.raycastTarget = true;
            LayoutElement element =
                fieldObject.GetComponent<LayoutElement>();
            element.minWidth = 0f;
            element.preferredWidth = 214f;
            element.flexibleWidth = 1f;
            element.preferredHeight = 36f;

            GameObject textObject = new GameObject(
                "TerrainLabClusteringFileName",
                typeof(RectTransform),
                typeof(Text));
            textObject.transform.SetParent(fieldObject.transform, false);
            RectTransform textRect =
                textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(6f, 2f);
            textRect.offsetMax = new Vector2(-6f, -2f);
            Text label = textObject.GetComponent<Text>();
            label.font = LocalizedTextManager.current_font;
            label.fontSize = 11;
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = UnityColor.white;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 7;
            label.resizeTextMaxSize = 11;
            label.raycastTarget = false;
            ConfigureTooltip(fieldObject, tooltipKey);
            return label;
        }

        private T TrackEditorControl<T>(T control)
            where T : Selectable
        {
            if (control != null)
            {
                _editorControls.Add(control);
            }
            return control;
        }

        private Button CreateButton(
            Transform parent,
            string text,
            UnityEngine.Events.UnityAction action,
            float width,
            string tooltipKey,
            bool critical = false)
        {
            GameObject buttonObject =
                DefaultControls.CreateButton(_defaultResources);
            buttonObject.name = "TerrainLabClusteringButton";
            buttonObject.transform.SetParent(parent, false);
            Button button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(action);
            Image image = buttonObject.GetComponent<Image>();
            Sprite sprite = ToolbarButtons.getSpriteButtonNormal();
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
            }
            image.color = critical
                ? UnityColor.white
                : new UnityColor(0.36f, 0.38f, 0.32f, 1f);
            if (critical)
            {
                ColorBlock colors = button.colors;
                colors.normalColor =
                    new UnityColor(0.95f, 0.28f, 0.22f, 1f);
                colors.highlightedColor =
                    new UnityColor(1f, 0.40f, 0.28f, 1f);
                colors.selectedColor = colors.highlightedColor;
                colors.pressedColor =
                    new UnityColor(0.76f, 0.16f, 0.12f, 1f);
                colors.disabledColor =
                    new UnityColor(0.35f, 0.16f, 0.14f, 0.62f);
                colors.colorMultiplier = 1f;
                button.colors = colors;
            }
            Text label = buttonObject.GetComponentInChildren<Text>();
            label.font = LocalizedTextManager.current_font;
            label.text = text;
            label.fontSize = 11;
            label.fontStyle = FontStyle.Bold;
            label.color = UnityColor.white;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 7;
            label.resizeTextMaxSize = 11;
            if (TerrainLocalizedUi.Matches(text, tooltipKey))
            {
                TerrainLocalizedUi.Bind(label, tooltipKey);
            }
            LayoutElement element =
                buttonObject.AddComponent<LayoutElement>();
            element.minWidth = 0f;
            element.preferredWidth = width;
            element.flexibleWidth = 1f;
            element.preferredHeight = 30f;
            ConfigureTooltip(buttonObject, tooltipKey);
            return button;
        }

        private Button CreateFlexibleButton(
            Transform parent,
            string text,
            UnityEngine.Events.UnityAction action,
            string tooltipKey)
        {
            Button button = CreateButton(
                parent,
                text,
                action,
                0f,
                tooltipKey);
            LayoutElement element = button.GetComponent<LayoutElement>();
            element.minWidth = 0f;
            element.preferredWidth = 0f;
            element.flexibleWidth = 1f;
            return button;
        }

        private InputField CreateInputField(
            Transform parent,
            string value,
            string tooltipKey)
        {
            GameObject inputObject =
                DefaultControls.CreateInputField(_defaultResources);
            inputObject.name = "TerrainLabClusteringParameter";
            inputObject.transform.SetParent(parent, false);
            InputField input = inputObject.GetComponent<InputField>();
            input.text = value;
            input.contentType = InputField.ContentType.IntegerNumber;
            foreach (Text text in
                     inputObject.GetComponentsInChildren<Text>(true))
            {
                text.font = LocalizedTextManager.current_font;
                text.fontSize = 12;
                text.color = new UnityColor(1f, 0.82f, 0.22f, 1f);
            }
            Image image = inputObject.GetComponent<Image>();
            image.sprite = SpriteTextureLoader.getSprite(
                "ui/special/darkInputFieldEmpty");
            image.type = Image.Type.Sliced;
            image.color = new UnityColor(0.06f, 0.06f, 0.055f, 1f);
            LayoutElement element =
                inputObject.AddComponent<LayoutElement>();
            element.minWidth = 0f;
            element.flexibleWidth = 1f;
            element.preferredHeight = 28f;
            ConfigureTooltip(inputObject, tooltipKey);
            return input;
        }

        private static void SetButtonLocalizationKey(
            Button button,
            string localizationKey)
        {
            Text label = button?.GetComponentInChildren<Text>();
            if (label != null)
            {
                TerrainLocalizedUi.Bind(label, localizationKey);
            }
        }

        private static void SetModeButtonState(Button button, bool selected)
        {
            if (button?.targetGraphic == null)
            {
                return;
            }
            button.targetGraphic.color = selected
                ? new UnityColor(0.3f, 0.72f, 0.34f, 1f)
                : new UnityColor(0.36f, 0.38f, 0.32f, 1f);
        }

        private static void ConfigureTooltip(
            GameObject target,
            string key)
        {
            TerrainParameterTooltip tooltip =
                target.GetComponent<TerrainParameterTooltip>();
            if (tooltip == null)
            {
                tooltip = target.AddComponent<TerrainParameterTooltip>();
            }
            tooltip.Configure(key, key + "_description");
        }

        private static UnityColor NeutralText()
        {
            return new UnityColor(0.83f, 0.79f, 0.66f, 1f);
        }

        private void SetPanelStatus(string message, bool error)
        {
            if (_panelStatus == null)
            {
                return;
            }
            _panelStatus.text = message ?? string.Empty;
            _panelStatus.color = error
                ? new UnityColor(0.95f, 0.52f, 0.46f, 1f)
                : NeutralText();
        }

        private void SetStatus(string message, bool error)
        {
            StatusChanged?.Invoke(message, error);
        }

        private void BringToFront()
        {
            _root.transform.SetAsLastSibling();
            if (_toolbarTransform != null)
            {
                _toolbarTransform.SetAsLastSibling();
            }
        }

        private void EnsureInitialized()
        {
            if (!_initialized || _workspace == null)
            {
                throw new InvalidOperationException(
                    "Automatic image clustering is not initialized.");
            }
        }

        private void ReleaseTexture()
        {
            if (_texture != null)
            {
                Destroy(_texture);
                _texture = null;
            }
        }

        private void OnDestroy()
        {
            ReleaseTexture();
            DestroyBoundaryGraphics();
        }
    }
}
