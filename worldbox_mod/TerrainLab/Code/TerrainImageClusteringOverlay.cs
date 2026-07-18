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
        private Text _panelStatus;
        private Button _previousImageButton;
        private Button _nextImageButton;
        private Button _openSelectedButton;
        private Button _drawBoundaryButton;
        private Button _finishBoundaryButton;
        private Button _cancelBoundaryButton;
        private Button _clearBoundaryButton;
        private Button _expertButton;
        private GameObject _expertPanel;
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
            layout.padding = new RectOffset(9, 9, 8, 8);
            layout.spacing = 5f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            Transform content = contentObject.transform;

            CreateLabel(
                content,
                LM.Get("terrain_lab_cluster_heading"),
                18,
                FontStyle.Bold,
                30f,
                UnityColor.white);
            CreateLabel(
                content,
                LM.Get("terrain_lab_cluster_image_choice"),
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

            CreateLabel(
                content,
                LM.Get("terrain_lab_cluster_boundary_heading"),
                11,
                FontStyle.Bold,
                18f,
                NeutralText());
            Transform boundaryModes = CreateRow(content, 28f);
            _drawBoundaryButton = TrackEditorControl(
                CreateButton(
                    boundaryModes,
                    LM.Get("terrain_lab_cluster_boundary_draw"),
                    StartBoundary,
                    92f,
                    "terrain_lab_cluster_boundary_draw"));
            _finishBoundaryButton = TrackEditorControl(
                CreateButton(
                    boundaryModes,
                    LM.Get("terrain_lab_cluster_boundary_finish"),
                    FinishBoundary,
                    92f,
                    "terrain_lab_cluster_boundary_finish"));
            _cancelBoundaryButton = TrackEditorControl(
                CreateButton(
                    boundaryModes,
                    LM.Get("terrain_lab_cluster_boundary_cancel"),
                    delegate { CancelBoundary(true); },
                    92f,
                    "terrain_lab_cluster_boundary_cancel"));
            _clearBoundaryButton = TrackEditorControl(
                CreateButton(
                    content,
                    LM.Get("terrain_lab_cluster_boundary_clear"),
                    ClearBoundary,
                    292f,
                    "terrain_lab_cluster_boundary_clear"));

            CreateLabel(
                content,
                LM.Get("terrain_lab_cluster_basic_heading"),
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
            _coordinateLabel = CreateLabel(
                content,
                "X -  Y -",
                10,
                FontStyle.Normal,
                18f,
                new UnityColor(0.72f, 0.84f, 0.94f, 1f));
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
                SetButtonText(
                    _expertButton,
                    LM.Get(_expertVisible
                        ? "terrain_lab_cluster_expert_hide"
                        : "terrain_lab_cluster_expert_show"));
            }
            Canvas.ForceUpdateCanvases();
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
            TerrainImageClusteringSettings settings = _profile.Settings;
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
            _profile.Validate(_sourceWidth, _sourceHeight);
            UpdateSummary();
        }

        private int ReadInteger(string id, int minimum, int maximum)
        {
            if (!_parameterInputs.TryGetValue(id, out InputField input) ||
                !int.TryParse(
                    input.text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int value) ||
                value < minimum || value > maximum)
            {
                throw new InvalidDataException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: {1}..{2}",
                        LM.Get("terrain_lab_cluster_parameter_error"),
                        minimum,
                        maximum));
            }
            return value;
        }

        private void PopulateParameterControls()
        {
            TerrainImageClusteringSettings settings = _profile.Settings;
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
            SetEditorControlsEnabled(false);
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
                _draftVertices.Count);
            UpdateBoundaryButtons();
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

            Text label = CreateLabel(
                row,
                LM.Get(localizationKey),
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
            layout.childControlWidth = false;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            LayoutElement element = row.GetComponent<LayoutElement>();
            element.preferredHeight = height;
            return row.transform;
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
            element.preferredWidth = 214f;
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
                ? new UnityColor(0.95f, 0.28f, 0.22f, 1f)
                : new UnityColor(0.36f, 0.38f, 0.32f, 1f);
            Text label = buttonObject.GetComponentInChildren<Text>();
            label.font = LocalizedTextManager.current_font;
            label.text = text;
            label.fontSize = 11;
            label.fontStyle = FontStyle.Bold;
            label.color = UnityColor.white;
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 7;
            label.resizeTextMaxSize = 11;
            LayoutElement element =
                buttonObject.AddComponent<LayoutElement>();
            element.preferredWidth = width;
            element.preferredHeight = 30f;
            ConfigureTooltip(buttonObject, tooltipKey);
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
                text.color = UnityColor.white;
            }
            Image image = inputObject.GetComponent<Image>();
            image.sprite = SpriteTextureLoader.getSprite(
                "ui/special/darkInputFieldEmpty");
            image.type = Image.Type.Sliced;
            image.color = new UnityColor(0.06f, 0.06f, 0.055f, 1f);
            LayoutElement element =
                inputObject.AddComponent<LayoutElement>();
            element.preferredHeight = 28f;
            ConfigureTooltip(inputObject, tooltipKey);
            return input;
        }

        private static void SetButtonText(Button button, string value)
        {
            Text label = button?.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.text = value;
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
