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
    internal enum TerrainImageClassificationDrawMode
    {
        None,
        Point,
        Line,
        Polygon,
        MapBoundary,
        DeletePolygon
    }

    internal sealed class TerrainImageCanvasInput : MonoBehaviour,
        IPointerClickHandler,
        IScrollHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IPointerMoveHandler,
        IPointerExitHandler
    {
        private TerrainImageClassificationOverlay _owner;
        private bool _dragging;
        private bool _moved;

        public void Initialize(TerrainImageClassificationOverlay owner)
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
                _owner.HandleImageClick(
                    eventData.position,
                    true,
                    false);
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

    public sealed class TerrainImageClassificationOverlay : MonoBehaviour
    {
        private const float MinimumZoom = 1f;
        private const float MaximumZoom = 12f;
        private const int VertexSnapRadiusSourcePixels = 40;

        private readonly List<GameObject> _markers = new List<GameObject>();
        private readonly List<TerrainImagePolygonGraphic> _regionGraphics =
            new List<TerrainImagePolygonGraphic>();
        private readonly List<TerrainImageClassificationVertex> _draftVertices =
            new List<TerrainImageClassificationVertex>();
        private readonly List<Selectable> _editorControls =
            new List<Selectable>();
        private readonly Dictionary<string, Button> _biotopeButtons =
            new Dictionary<string, Button>(StringComparer.Ordinal);
        private readonly Dictionary<string, Image> _biotopeSelectionLamps =
            new Dictionary<string, Image>(StringComparer.Ordinal);
        private readonly Dictionary<string, Button> _quickSurfaceButtons =
            new Dictionary<string, Button>(StringComparer.Ordinal);
        private readonly Dictionary<string, Image> _quickSurfaceSelectionLamps =
            new Dictionary<string, Image>(StringComparer.Ordinal);
        private readonly DefaultControls.Resources _defaultResources =
            new DefaultControls.Resources();

        private TerrainImageWorkspaceService _workspace;
        private Transform _toolbarTransform;
        private GameObject _root;
        private RectTransform _viewport;
        private RectTransform _imageRect;
        private RawImage _image;
        private RectTransform _markerRoot;
        private Text _fileLabel;
        private Text _sampleLabel;
        private Text _coordinateLabel;
        private Text _mapSizeLabel;
        private Text _panelStatus;
        private Dropdown _surfaceDropdown;
        private InputField _longSideInput;
        private InputField _elevationInput;
        private InputField _lineWidthInput;
        private Dropdown _outsideSurfaceDropdown;
        private Dropdown _outsideBiotopeDropdown;
        private InputField _outsideElevationInput;
        private GameObject _biotopePalette;
        private Button _previousImageButton;
        private Button _nextImageButton;
        private Button _openSelectedButton;
        private Button _pointModeButton;
        private Button _lineModeButton;
        private Button _polygonModeButton;
        private Button _boundaryModeButton;
        private Button _deletePolygonModeButton;
        private Button _deleteAllPolygonsButton;
        private Button _finishPolygonButton;
        private Button _cancelPolygonButton;
        private Button _publishFeatureButton;
        private TerrainImagePolygonGraphic _draftGraphic;
        private Texture2D _texture;
        private TerrainImageClassificationProfile _profile;
        private List<string> _images = new List<string>();
        private string _imagePath;
        private int _imageIndex = -1;
        private int _pendingImageIndex = -1;
        private int _sourceWidth;
        private int _sourceHeight;
        private float _zoom = 1f;
        private Vector2 _lastViewportSize;
        private float _clearArmedUntil;
        private TerrainImageClassificationDrawMode _drawMode =
            TerrainImageClassificationDrawMode.None;
        private TerrainImageClassificationVertex _hoverVertex;
        private bool _hasHoverVertex;
        private bool _draftGeometryComplete;
        private bool _syncingOutsideControls;
        private bool _editorEnabled;
        private bool _initialized;
        private string _selectedBiotopeId;

        public event Action<string, bool> StatusChanged;

        public event Action VisibilityChanged;

        public bool IsVisible =>
            _root != null && _root.activeSelf;

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

        public int SampleCount => _profile?.Samples?.Count ?? 0;

        public int RegionCount => _profile?.Regions?.Count ?? 0;

        public int LineCount => _profile?.Lines?.Count ?? 0;

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

        public void ShowLatest()
        {
            ShowImagePicker();
        }

        public void ShowImagePicker()
        {
            EnsureInitialized();
            try
            {
                string previous = _imagePath;
                _images = _workspace.GetRecentImages(64).ToList();
                EnsureUi();
                RefreshLocalizedDropdowns();
                ResetLoadedImage();
                InitializePendingImage(previous);
                _root.SetActive(true);
                _root.transform.SetAsLastSibling();
                if (_toolbarTransform != null)
                {
                    _toolbarTransform.SetAsLastSibling();
                }

                if (_images.Count == 0)
                {
                    _fileLabel.text =
                        LM.Get("terrain_lab_manual_no_images");
                    _sampleLabel.text = string.Empty;
                    SetStatus(
                        LM.Get("terrain_lab_manual_no_images"),
                        true);
                }
                else
                {
                    SetPanelStatus(
                        LM.Get("terrain_lab_manual_select_and_confirm"),
                        false);
                    SetStatus(
                        LM.Get("terrain_lab_status_manual_selector_opened"),
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
            if (_root != null)
            {
                CancelPolygon(false);
                _root.SetActive(false);
                if (_image != null)
                {
                    _image.texture = null;
                }
                ReleaseTexture();
                VisibilityChanged?.Invoke();
            }
        }

        public void HandleImageClick(
            Vector2 screenPosition,
            bool secondary,
            bool doubleClick)
        {
            if (!IsVisible || _profile == null || _imageRect == null)
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

            _coordinateLabel.text = string.Format(
                CultureInfo.InvariantCulture,
                "X {0}  Y {1}",
                sourceX,
                sourceY);
            try
            {
                if (_drawMode ==
                    TerrainImageClassificationDrawMode.DeletePolygon)
                {
                    if (secondary)
                    {
                        return;
                    }
                    if (!_profile.RemoveRegionAt(sourceX, sourceY))
                    {
                        SetPanelStatus(
                            LM.Get(
                                "terrain_lab_manual_no_polygon_at_point"),
                            false);
                        return;
                    }

                    SaveProfile();
                    RebuildAnnotations();
                    UpdateLabels();
                    SetPanelStatus(
                        LM.Get(
                            "terrain_lab_manual_polygon_removed_at_point"),
                        false);
                    return;
                }

                if (_drawMode ==
                        TerrainImageClassificationDrawMode.Line ||
                    _drawMode ==
                        TerrainImageClassificationDrawMode.Polygon ||
                    _drawMode ==
                        TerrainImageClassificationDrawMode.MapBoundary)
                {
                    if (_draftGeometryComplete)
                    {
                        SetPanelStatus(
                            LM.Get(
                                "terrain_lab_manual_publish_or_cancel_draft"),
                            true);
                        return;
                    }
                    if (secondary)
                    {
                        FinishPolygon();
                        return;
                    }
                    AddPolygonVertex(sourceX, sourceY);
                    int minimumVertices =
                        _drawMode ==
                        TerrainImageClassificationDrawMode.Line
                            ? 2
                            : 3;
                    if (doubleClick &&
                        _draftVertices.Count >= minimumVertices)
                    {
                        FinishPolygon();
                    }
                    return;
                }

                if (_drawMode !=
                    TerrainImageClassificationDrawMode.Point)
                {
                    SetPanelStatus(
                        LM.Get("terrain_lab_manual_choose_geometry"),
                        true);
                    return;
                }

                if (secondary)
                {
                    CancelPolygon(true);
                    return;
                }
                if (_draftVertices.Count > 0)
                {
                    SetPanelStatus(
                        LM.Get(
                            "terrain_lab_manual_publish_or_cancel_draft"),
                        true);
                    return;
                }
                TerrainImageClassificationVertex snappedPoint =
                    ResolveDraftVertex(sourceX, sourceY);
                sourceX = snappedPoint.X;
                sourceY = snappedPoint.Y;
                if (!_profile.IsInsideMapBoundary(sourceX, sourceY))
                {
                    SetPanelStatus(
                        LM.Get("terrain_lab_manual_outside_boundary"),
                        true);
                    return;
                }

                _draftVertices.Add(
                    new TerrainImageClassificationVertex(sourceX, sourceY));
                _draftGeometryComplete = true;
                RefreshDraftGraphic();
                UpdateLabels();
                SetPanelStatus(
                    string.Format(
                        LM.Get("terrain_lab_manual_point_drafted_format"),
                        sourceX,
                        sourceY),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
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

            _coordinateLabel.text = string.Format(
                CultureInfo.InvariantCulture,
                "X {0}  Y {1}",
                sourceX,
                sourceY);
            if (_drawMode == TerrainImageClassificationDrawMode.None ||
                _drawMode == TerrainImageClassificationDrawMode.Point ||
                _drawMode ==
                    TerrainImageClassificationDrawMode.DeletePolygon ||
                _draftGeometryComplete ||
                _draftVertices.Count == 0)
            {
                return;
            }
            TerrainImageClassificationVertex snapped =
                ResolveDraftVertex(sourceX, sourceY);
            sourceX = snapped.X;
            sourceY = snapped.Y;
            if (_hasHoverVertex &&
                _hoverVertex.X == sourceX &&
                _hoverVertex.Y == sourceY)
            {
                return;
            }
            _hoverVertex =
                new TerrainImageClassificationVertex(sourceX, sourceY);
            _hasHoverVertex = true;
            RefreshDraftGraphic();
        }

        public void HandlePointerExit()
        {
            if (!_hasHoverVertex)
            {
                return;
            }
            _hasHoverVertex = false;
            _hoverVertex = null;
            RefreshDraftGraphic();
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
            UpdateMarkerScale();
            ClampPan();
        }

        public void HandlePan(Vector2 delta)
        {
            if (!IsVisible || _imageRect == null)
            {
                return;
            }

            Canvas canvas = CanvasMain.instance?.canvas_ui;
            float scale = canvas == null ? 1f : Mathf.Max(0.01f, canvas.scaleFactor);
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

        private void OpenImage(int imageIndex)
        {
            if (imageIndex < 0 || imageIndex >= _images.Count)
            {
                return;
            }

            string path = _images[imageIndex];
            Texture2D nextTexture = LoadPreview(
                path,
                out int width,
                out int height);
            try
            {
                TerrainImageClassificationProfile nextProfile =
                    TerrainImageClassificationProfile.LoadOrCreate(
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
                _draftVertices.Clear();
                _draftGeometryComplete = false;
                _hoverVertex = null;
                _hasHoverVertex = false;
                _profile.Settings.InterpolateElevationGlobally = true;
                _longSideInput.SetTextWithoutNotify(
                    _profile.Settings.LongSideBlocks.ToString(
                        CultureInfo.InvariantCulture));
                UpdateMapSizePreview();
                _zoom = 1f;
                _imageRect.localScale = Vector3.one;
                _imageRect.anchoredPosition = Vector2.zero;
                _root.SetActive(true);
                _root.transform.SetAsLastSibling();
                if (_toolbarTransform != null)
                {
                    _toolbarTransform.SetAsLastSibling();
                }

                Canvas.ForceUpdateCanvases();
                FitImageToViewport(true);
                SetEditorControlsEnabled(true);
                ResetClassificationSelection();
                _drawMode = TerrainImageClassificationDrawMode.None;
                SyncOutsideControlsFromProfile();
                RebuildAnnotations();
                UpdateLabels();
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_ready"),
                    false);
                VisibilityChanged?.Invoke();
                SetStatus(
                    LM.Get("terrain_lab_status_manual_classifier_opened"),
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
                    LM.Get("terrain_lab_manual_finish_draft"),
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
                UpdateLabels();
                return;
            }

            try
            {
                if (_profile != null &&
                    !string.IsNullOrWhiteSpace(_imagePath))
                {
                    SaveProfile();
                }
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
                return;
            }

            ResetLoadedImage();
            UpdatePendingImageLabels();
            SetPanelStatus(
                LM.Get("terrain_lab_manual_select_and_confirm"),
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
                    LM.Get("terrain_lab_manual_no_selection"),
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

        private void ResetLoadedImage()
        {
            CancelPolygon(false);
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
            _hoverVertex = null;
            _hasHoverVertex = false;
            _draftGeometryComplete = false;
            _drawMode = TerrainImageClassificationDrawMode.None;
            ResetClassificationSelection();
            RebuildAnnotations();
            if (_coordinateLabel != null)
            {
                _coordinateLabel.text = "X -  Y -";
            }
            if (_mapSizeLabel != null)
            {
                _mapSizeLabel.text = string.Empty;
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

            _fileLabel.text =
                Path.GetFileName(_images[_pendingImageIndex]);
            _sampleLabel.text =
                LM.Get("terrain_lab_manual_select_and_confirm");
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
            _editorEnabled = enabled;
            foreach (Selectable control in _editorControls)
            {
                if (control != null)
                {
                    control.interactable = enabled;
                }
            }
            UpdateModeButtons();
        }

        private void EnsureUi()
        {
            if (_root != null)
            {
                return;
            }

            Transform canvas = CanvasMain.instance.canvas_ui.transform;
            _root = new GameObject(
                "TerrainLabManualClassification",
                typeof(RectTransform));
            _root.transform.SetParent(canvas, false);
            RectTransform rootRect = _root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = new Vector2(0f, 112f);
            rootRect.offsetMax = new Vector2(0f, -92f);

            GameObject viewportObject = new GameObject(
                "TerrainLabClassificationViewport",
                typeof(RectTransform),
                typeof(Image),
                typeof(RectMask2D),
                typeof(TerrainImageCanvasInput));
            viewportObject.transform.SetParent(_root.transform, false);
            _viewport = viewportObject.GetComponent<RectTransform>();
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = Vector2.zero;
            _viewport.offsetMax = new Vector2(-286f, 0f);
            Image viewportBackground = viewportObject.GetComponent<Image>();
            viewportBackground.color = new UnityColor(0.02f, 0.02f, 0.02f, 0.78f);
            viewportBackground.raycastTarget = true;
            viewportObject.GetComponent<TerrainImageCanvasInput>().Initialize(this);
            ConfigureTooltip(
                viewportObject,
                "terrain_lab_manual_canvas");

            GameObject imageObject = new GameObject(
                "TerrainLabClassificationImage",
                typeof(RectTransform),
                typeof(RawImage));
            imageObject.transform.SetParent(_viewport, false);
            _imageRect = imageObject.GetComponent<RectTransform>();
            _imageRect.anchorMin = new Vector2(0.5f, 0.5f);
            _imageRect.anchorMax = new Vector2(0.5f, 0.5f);
            _imageRect.pivot = new Vector2(0.5f, 0.5f);
            _image = imageObject.GetComponent<RawImage>();
            _image.color = new UnityColor(1f, 1f, 1f, 0.84f);
            _image.raycastTarget = false;

            GameObject markerObject = new GameObject(
                "TerrainLabClassificationMarkers",
                typeof(RectTransform));
            markerObject.transform.SetParent(_imageRect, false);
            _markerRoot = markerObject.GetComponent<RectTransform>();
            _markerRoot.anchorMin = Vector2.zero;
            _markerRoot.anchorMax = Vector2.one;
            _markerRoot.offsetMin = Vector2.zero;
            _markerRoot.offsetMax = Vector2.zero;

            CreateBiotopePalette(_viewport);
            _coordinateLabel = CreateCoordinateOverlay(_viewport);
            CreatePanel(_root.transform);
            _root.SetActive(false);
        }

        private void CreatePanel(Transform parent)
        {
            GameObject panel = new GameObject(
                "TerrainLabClassificationPanel",
                typeof(RectTransform),
                typeof(Image),
                typeof(RectMask2D),
                typeof(ScrollRect));
            panel.transform.SetParent(parent, false);
            RectTransform rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(280f, 0f);
            rect.anchoredPosition = Vector2.zero;
            Image background = panel.GetComponent<Image>();
            background.sprite = SpriteTextureLoader.getSprite(
                "ui/special/darkInputFieldEmpty");
            background.type = Image.Type.Sliced;
            background.color = new UnityColor(0.20f, 0.22f, 0.18f, 0.98f);
            background.raycastTarget = true;

            GameObject contentObject = new GameObject(
                "TerrainLabClassificationPanelContent",
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
                "terrain_lab_manual_heading",
                18,
                FontStyle.Bold,
                30f,
                UnityColor.white);
            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_image_choice",
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            Transform navigation = CreateRow(content, 36f);
            _previousImageButton = CreateButton(
                navigation,
                "<",
                delegate { MovePendingImage(-1); },
                38f,
                "terrain_lab_manual_previous_image");
            _fileLabel = CreateFileSelectorField(
                navigation,
                "terrain_lab_manual_image_choice");
            _nextImageButton = CreateButton(
                navigation,
                ">",
                delegate { MovePendingImage(1); },
                38f,
                "terrain_lab_manual_next_image");
            _openSelectedButton = CreateButton(
                content,
                LM.Get("terrain_lab_manual_open_selected"),
                ConfirmImageSelection,
                252f,
                "terrain_lab_manual_open_selected",
                true);

            TrackEditorControl(
                CreateButton(
                    content,
                    LM.Get("terrain_lab_manual_fit"),
                    delegate
                    {
                        _zoom = 1f;
                        _imageRect.localScale = Vector3.one;
                        _imageRect.anchoredPosition = Vector2.zero;
                        FitImageToViewport(true);
                    },
                    252f,
                    "terrain_lab_manual_fit"));

            CreateLocalizedLabel(
                content,
                "terrain_lab_output_size_heading",
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
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
            mapSizeCaptionElement.preferredWidth = 92f;
            mapSizeCaptionElement.flexibleWidth = 1f;
            _longSideInput = TrackEditorControl(
                CreateInputField(
                    mapSizeRow,
                    "20",
                    "terrain_lab_output_long_side"));
            LayoutElement mapSizeInputElement =
                _longSideInput.GetComponent<LayoutElement>();
            mapSizeInputElement.preferredWidth = 44f;
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
            mapSizeLabelElement.preferredWidth = 104f;
            mapSizeLabelElement.flexibleWidth = 1f;
            ConfigureTooltip(
                _mapSizeLabel.gameObject,
                "terrain_lab_output_long_side");

            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_geometry_heading",
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            Transform drawModes = CreateRow(content, 28f);
            _pointModeButton = TrackEditorControl(
                CreateButton(
                    drawModes,
                    LM.Get("terrain_lab_manual_mode_point"),
                    delegate
                    {
                        SetDrawMode(
                            TerrainImageClassificationDrawMode.Point);
                    },
                    78f,
                    "terrain_lab_manual_mode_point"));
            _lineModeButton = TrackEditorControl(
                CreateButton(
                    drawModes,
                    LM.Get("terrain_lab_manual_mode_line"),
                    delegate
                    {
                        SetDrawMode(
                            TerrainImageClassificationDrawMode.Line);
                    },
                    78f,
                    "terrain_lab_manual_mode_line"));
            _polygonModeButton = TrackEditorControl(
                CreateButton(
                    drawModes,
                    LM.Get("terrain_lab_manual_mode_polygon"),
                    delegate
                    {
                        SetDrawMode(
                            TerrainImageClassificationDrawMode.Polygon);
                    },
                    78f,
                    "terrain_lab_manual_mode_polygon"));

            Transform draftActions = CreateRow(content, 28f);
            _finishPolygonButton = TrackEditorControl(
                CreateButton(
                    draftActions,
                    LM.Get("terrain_lab_manual_finish_geometry"),
                    FinishPolygon,
                    118f,
                    "terrain_lab_manual_finish_geometry"));
            _cancelPolygonButton = TrackEditorControl(
                CreateButton(
                    draftActions,
                    LM.Get("terrain_lab_manual_cancel_draft"),
                    CancelPolygonOrBoundary,
                    118f,
                    "terrain_lab_manual_cancel_draft"));

            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_attributes_heading",
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_surface",
                10,
                FontStyle.Bold,
                16f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            _surfaceDropdown = TrackEditorControl(
                CreateDropdown(
                    content,
                    new[]
                    {
                        LM.Get("terrain_lab_manual_not_selected")
                    }.Concat(
                        TerrainImageClassificationCatalog.Surfaces
                            .Select(option =>
                                LM.Get(option.LocalizationKey))),
                    "terrain_lab_manual_surface"));
            _surfaceDropdown.onValueChanged.AddListener(
                HandleSurfaceChanged);

            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_elevation",
                10,
                FontStyle.Bold,
                16f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            _elevationInput = TrackEditorControl(
                CreateInputField(
                    content,
                    string.Empty,
                    "terrain_lab_manual_elevation"));
            _elevationInput.onValueChanged.AddListener(
                delegate
                {
                    RefreshDraftGraphic();
                    UpdateModeButtons();
                });

            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_line_width",
                10,
                FontStyle.Bold,
                16f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            _lineWidthInput = TrackEditorControl(
                CreateInputField(
                    content,
                    string.Empty,
                    "terrain_lab_manual_line_width"));
            _lineWidthInput.onValueChanged.AddListener(
                delegate
                {
                    RefreshDraftGraphic();
                    UpdateModeButtons();
                });

            _publishFeatureButton = TrackEditorControl(
                CreateButton(
                    content,
                    LM.Get("terrain_lab_manual_publish_feature"),
                    PublishFeature,
                    252f,
                    "terrain_lab_manual_publish_feature",
                    true));

            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_extent_heading",
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            Transform extentModes = CreateRow(content, 28f);
            _boundaryModeButton = TrackEditorControl(
                CreateButton(
                    extentModes,
                    LM.Get("terrain_lab_manual_mode_boundary"),
                    delegate
                    {
                        SetDrawMode(
                            TerrainImageClassificationDrawMode.MapBoundary);
                    },
                    118f,
                    "terrain_lab_manual_mode_boundary"));

            _deletePolygonModeButton = TrackEditorControl(
                CreateButton(
                    extentModes,
                    LM.Get(
                        "terrain_lab_manual_mode_delete_polygon"),
                    delegate
                    {
                        SetDrawMode(
                            TerrainImageClassificationDrawMode
                                .DeletePolygon);
                    },
                    118f,
                    "terrain_lab_manual_mode_delete_polygon"));

            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_outside_heading",
                10,
                FontStyle.Bold,
                16f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_outside_surface",
                9,
                FontStyle.Normal,
                14f,
                new UnityColor(0.72f, 0.74f, 0.67f, 1f));
            _outsideSurfaceDropdown = TrackEditorControl(
                CreateDropdown(
                    content,
                    TerrainImageClassificationCatalog.OutsideSurfaces
                        .Select(option => LM.Get(option.LocalizationKey)),
                    "terrain_lab_manual_outside_surface"));
            _outsideSurfaceDropdown.onValueChanged.AddListener(
                HandleOutsideSurfaceChanged);
            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_outside_biotope",
                9,
                FontStyle.Normal,
                14f,
                new UnityColor(0.72f, 0.74f, 0.67f, 1f));
            _outsideBiotopeDropdown = TrackEditorControl(
                CreateDropdown(
                    content,
                    TerrainImageClassificationCatalog.OutsideBiotopes
                        .Select(option => LM.Get(option.LocalizationKey)),
                    "terrain_lab_manual_outside_biotope"));
            _outsideBiotopeDropdown.onValueChanged.AddListener(
                delegate { SaveOutsideMapAreaFromControls(); });
            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_outside_elevation",
                9,
                FontStyle.Normal,
                14f,
                new UnityColor(0.72f, 0.74f, 0.67f, 1f));
            _outsideElevationInput = TrackEditorControl(
                CreateInputField(
                    content,
                    "-4000",
                    "terrain_lab_manual_outside_elevation"));
            _outsideElevationInput.onEndEdit.AddListener(
                delegate { SaveOutsideMapAreaFromControls(); });

            CreateLocalizedLabel(
                content,
                "terrain_lab_manual_markup_heading",
                10,
                FontStyle.Bold,
                16f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            Transform polygonDeletion = CreateRow(content, 28f);
            _deleteAllPolygonsButton = TrackEditorControl(
                CreateButton(
                    polygonDeletion,
                    LM.Get(
                        "terrain_lab_manual_delete_all_polygons"),
                    ClearPolygons,
                    118f,
                    "terrain_lab_manual_delete_all_polygons",
                    true));

            _sampleLabel = CreateLabel(
                content,
                string.Empty,
                11,
                FontStyle.Normal,
                36f,
                UnityColor.white);
            _panelStatus = CreateLabel(
                content,
                string.Empty,
                10,
                FontStyle.Normal,
                38f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));

            Transform profileActions = CreateRow(content, 30f);
            TrackEditorControl(
                CreateButton(
                    profileActions,
                    LM.Get("terrain_lab_manual_undo"),
                    UndoSample,
                    118f,
                    "terrain_lab_manual_undo"));
            TrackEditorControl(
                CreateButton(
                    profileActions,
                    LM.Get("terrain_lab_manual_clear"),
                    ClearSamples,
                    118f,
                    "terrain_lab_manual_clear"));

            Transform saveActions = CreateRow(content, 34f);
            TrackEditorControl(
                CreateButton(
                    saveActions,
                    LM.Get("terrain_lab_manual_save_profile"),
                    SaveProfileCommand,
                    118f,
                    "terrain_lab_manual_save_profile"));
            TrackEditorControl(
                CreateButton(
                    saveActions,
                    LM.Get("terrain_lab_manual_build_map"),
                    QueueConversion,
                    118f,
                    "terrain_lab_manual_build_map",
                    true));

            CreateButton(
                content,
                LM.Get("terrain_lab_manual_close"),
                Hide,
                252f,
                "terrain_lab_manual_close");
            SetEditorControlsEnabled(false);
            UpdateImageSelectorControls();
            UpdateModeButtons();
        }

        private void HandleSurfaceChanged(int index)
        {
            if (_elevationInput == null)
            {
                return;
            }

            if (index <= 0 ||
                index >
                TerrainImageClassificationCatalog.Surfaces.Count)
            {
                _elevationInput.SetTextWithoutNotify(string.Empty);
                _selectedBiotopeId = null;
                UpdateBiotopePalette();
                RefreshDraftGraphic();
                UpdateModeButtons();
                return;
            }

            TerrainImageClassOption option =
                TerrainImageClassificationCatalog.Surfaces[index - 1];
            _elevationInput.SetTextWithoutNotify(
                option.DefaultElevation.ToString(
                    CultureInfo.InvariantCulture));
            _selectedBiotopeId = null;
            UpdateBiotopePalette();
            RefreshDraftGraphic();
            UpdateModeButtons();
        }

        private void ResetClassificationSelection()
        {
            _surfaceDropdown?.SetValueWithoutNotify(0);
            _surfaceDropdown?.RefreshShownValue();
            _selectedBiotopeId = null;
            UpdateBiotopePalette();
            _elevationInput?.SetTextWithoutNotify(string.Empty);
            _lineWidthInput?.SetTextWithoutNotify(string.Empty);
            RefreshDraftGraphic();
        }

        private void SyncOutsideControlsFromProfile()
        {
            if (_outsideSurfaceDropdown == null ||
                _outsideBiotopeDropdown == null ||
                _outsideElevationInput == null)
            {
                return;
            }

            _syncingOutsideControls = true;
            try
            {
                string surface =
                    _profile?.MapBoundary?.OutsideSurface ??
                    "deep_ocean";
                string biotope =
                    _profile?.MapBoundary?.OutsideBiotope ?? "none";
                int surfaceIndex = Math.Max(
                    0,
                    TerrainImageClassificationCatalog.OutsideSurfaces
                        .ToList()
                        .FindIndex(option =>
                            string.Equals(
                                option.Id,
                                surface,
                                StringComparison.Ordinal)));
                int biotopeIndex = Math.Max(
                    0,
                    TerrainImageClassificationCatalog.OutsideBiotopes
                        .ToList()
                        .FindIndex(option =>
                            string.Equals(
                                option.Id,
                                biotope,
                                StringComparison.Ordinal)));
                _outsideSurfaceDropdown.SetValueWithoutNotify(
                    surfaceIndex);
                _outsideSurfaceDropdown.RefreshShownValue();
                _outsideBiotopeDropdown.SetValueWithoutNotify(
                    biotopeIndex);
                _outsideBiotopeDropdown.RefreshShownValue();
                _outsideElevationInput.SetTextWithoutNotify(
                    (_profile?.MapBoundary?.OutsideElevation ??
                     (short)-4000).ToString(
                        CultureInfo.InvariantCulture));
            }
            finally
            {
                _syncingOutsideControls = false;
            }
            UpdateModeButtons();
        }

        private void HandleOutsideSurfaceChanged(int index)
        {
            if (_syncingOutsideControls ||
                _outsideElevationInput == null ||
                index < 0 ||
                index >=
                TerrainImageClassificationCatalog.OutsideSurfaces.Count)
            {
                return;
            }

            TerrainImageClassOption option =
                TerrainImageClassificationCatalog.OutsideSurfaces[index];
            _outsideElevationInput.SetTextWithoutNotify(
                option.DefaultElevation.ToString(
                    CultureInfo.InvariantCulture));
            if (!option.SupportsBiotope &&
                _outsideBiotopeDropdown != null)
            {
                int noneIndex =
                    TerrainImageClassificationCatalog.OutsideBiotopes
                        .ToList()
                        .FindIndex(item =>
                            string.Equals(
                                item.Id,
                                "none",
                                StringComparison.Ordinal));
                _outsideBiotopeDropdown.SetValueWithoutNotify(
                    Math.Max(0, noneIndex));
                _outsideBiotopeDropdown.RefreshShownValue();
            }
            SaveOutsideMapAreaFromControls();
        }

        private void SaveOutsideMapAreaFromControls()
        {
            if (_syncingOutsideControls ||
                _profile?.MapBoundary == null ||
                _outsideSurfaceDropdown == null ||
                _outsideBiotopeDropdown == null ||
                _outsideElevationInput == null)
            {
                return;
            }
            if (_outsideSurfaceDropdown.value < 0 ||
                _outsideSurfaceDropdown.value >=
                TerrainImageClassificationCatalog.OutsideSurfaces.Count ||
                _outsideBiotopeDropdown.value < 0 ||
                _outsideBiotopeDropdown.value >=
                TerrainImageClassificationCatalog.OutsideBiotopes.Count ||
                !short.TryParse(
                    _outsideElevationInput.text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out short elevation) ||
                !TerrainElevationEncoding.IsDataValue(elevation) ||
                elevation == TerrainElevationEncoding.NoData)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_elevation_error"),
                    true);
                return;
            }

            try
            {
                TerrainImageClassOption surface =
                    TerrainImageClassificationCatalog.OutsideSurfaces[
                        _outsideSurfaceDropdown.value];
                TerrainImageBiotopeOption biotope =
                    TerrainImageClassificationCatalog.OutsideBiotopes[
                        _outsideBiotopeDropdown.value];
                _profile.SetOutsideMapArea(
                    surface.Id,
                    biotope.Id,
                    elevation);
                SaveProfile();
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_outside_saved"),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void SetDrawMode(TerrainImageClassificationDrawMode mode)
        {
            if (!_editorEnabled)
            {
                return;
            }
            if (_drawMode == mode)
            {
                if (_draftVertices.Count == 0)
                {
                    _drawMode = TerrainImageClassificationDrawMode.None;
                    ResetClassificationSelection();
                }
                UpdateModeButtons();
                return;
            }
            if (_draftVertices.Count > 0)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_finish_draft"),
                    true);
                return;
            }
            HandlePointerExit();
            ResetClassificationSelection();
            _drawMode = mode;
            UpdateModeButtons();
            string statusKey;
            switch (mode)
            {
                case TerrainImageClassificationDrawMode.Line:
                    statusKey =
                        "terrain_lab_manual_mode_line_active";
                    break;
                case TerrainImageClassificationDrawMode.Polygon:
                    statusKey =
                        "terrain_lab_manual_mode_polygon_active";
                    break;
                case TerrainImageClassificationDrawMode.MapBoundary:
                    statusKey =
                        "terrain_lab_manual_mode_boundary_active";
                    break;
                case TerrainImageClassificationDrawMode.DeletePolygon:
                    statusKey =
                        "terrain_lab_manual_mode_delete_polygon_active";
                    break;
                case TerrainImageClassificationDrawMode.None:
                    statusKey =
                        "terrain_lab_manual_choose_geometry";
                    break;
                default:
                    statusKey =
                        "terrain_lab_manual_mode_point_active";
                    break;
            }
            SetPanelStatus(
                LM.Get(statusKey),
                false);
        }

        private void UpdateModeButtons()
        {
            bool hasDraft = _draftVertices.Count > 0;
            SetModeButtonState(
                _pointModeButton,
                _editorEnabled &&
                _drawMode == TerrainImageClassificationDrawMode.Point);
            SetModeButtonState(
                _lineModeButton,
                _editorEnabled &&
                _drawMode == TerrainImageClassificationDrawMode.Line);
            SetModeButtonState(
                _polygonModeButton,
                _editorEnabled &&
                _drawMode == TerrainImageClassificationDrawMode.Polygon);
            SetModeButtonState(
                _boundaryModeButton,
                _editorEnabled &&
                _drawMode ==
                TerrainImageClassificationDrawMode.MapBoundary);
            SetModeButtonState(
                _deletePolygonModeButton,
                _editorEnabled &&
                _drawMode ==
                TerrainImageClassificationDrawMode.DeletePolygon);
            if (_pointModeButton != null)
            {
                _pointModeButton.interactable =
                    _editorEnabled &&
                    (!hasDraft ||
                     _drawMode ==
                     TerrainImageClassificationDrawMode.Point);
            }
            if (_lineModeButton != null)
            {
                _lineModeButton.interactable =
                    _editorEnabled &&
                    (!hasDraft ||
                     _drawMode ==
                     TerrainImageClassificationDrawMode.Line);
            }
            if (_polygonModeButton != null)
            {
                _polygonModeButton.interactable =
                    _editorEnabled &&
                    (!hasDraft ||
                     _drawMode ==
                     TerrainImageClassificationDrawMode.Polygon);
            }
            if (_boundaryModeButton != null)
            {
                _boundaryModeButton.interactable =
                    _editorEnabled &&
                    (!hasDraft ||
                     _drawMode ==
                     TerrainImageClassificationDrawMode.MapBoundary);
            }
            if (_deletePolygonModeButton != null)
            {
                _deletePolygonModeButton.interactable =
                    _editorEnabled && !hasDraft;
            }
            if (_deleteAllPolygonsButton != null)
            {
                _deleteAllPolygonsButton.interactable =
                    _editorEnabled &&
                    ((_profile?.Regions?.Count ?? 0) > 0 ||
                     (_drawMode ==
                          TerrainImageClassificationDrawMode.Polygon &&
                      _draftVertices.Count > 0));
            }

            bool boundaryMode =
                _drawMode ==
                TerrainImageClassificationDrawMode.MapBoundary;
            bool featureMode =
                _drawMode ==
                    TerrainImageClassificationDrawMode.Point ||
                _drawMode ==
                    TerrainImageClassificationDrawMode.Line ||
                _drawMode ==
                    TerrainImageClassificationDrawMode.Polygon;
            bool classificationControls =
                _editorEnabled && featureMode &&
                _draftGeometryComplete;
            if (_surfaceDropdown != null)
            {
                _surfaceDropdown.interactable = classificationControls;
            }
            UpdateBiotopePalette(classificationControls);
            if (_elevationInput != null)
            {
                _elevationInput.interactable = classificationControls;
            }
            if (_lineWidthInput != null)
            {
                _lineWidthInput.interactable =
                    classificationControls &&
                    _drawMode ==
                    TerrainImageClassificationDrawMode.Line;
            }
            if (_publishFeatureButton != null)
            {
                _publishFeatureButton.interactable =
                    classificationControls &&
                    HasValidFeatureSelection();
            }

            bool multiVertexMode =
                _editorEnabled &&
                (_drawMode == TerrainImageClassificationDrawMode.Line ||
                 _drawMode ==
                     TerrainImageClassificationDrawMode.Polygon ||
                 _drawMode ==
                     TerrainImageClassificationDrawMode.MapBoundary);
            int minimumVertices =
                _drawMode ==
                TerrainImageClassificationDrawMode.Line
                    ? 2
                    : 3;
            if (_finishPolygonButton != null)
            {
                _finishPolygonButton.interactable =
                    multiVertexMode &&
                    !_draftGeometryComplete &&
                    _draftVertices.Count >= minimumVertices;
                string finishKey = boundaryMode
                    ? "terrain_lab_manual_finish_boundary"
                    : "terrain_lab_manual_finish_geometry";
                SetButtonLocalizationKey(
                    _finishPolygonButton,
                    finishKey);
                ConfigureTooltip(
                    _finishPolygonButton.gameObject,
                    finishKey);
            }
            if (_cancelPolygonButton != null)
            {
                _cancelPolygonButton.interactable =
                    _editorEnabled &&
                    (_draftVertices.Count > 0 ||
                     boundaryMode && _profile?.MapBoundary != null);
                string cancelKey = boundaryMode
                    ? (_draftVertices.Count > 0
                        ? "terrain_lab_manual_cancel_boundary"
                        : "terrain_lab_manual_remove_boundary")
                    : "terrain_lab_manual_cancel_draft";
                SetButtonLocalizationKey(
                    _cancelPolygonButton,
                    cancelKey);
                ConfigureTooltip(
                    _cancelPolygonButton.gameObject,
                    cancelKey);
            }

            bool outsideEnabled =
                _editorEnabled && _profile?.MapBoundary != null;
            if (_outsideSurfaceDropdown != null)
            {
                _outsideSurfaceDropdown.interactable = outsideEnabled;
            }
            if (_outsideBiotopeDropdown != null)
            {
                bool supportsBiotope =
                    _outsideSurfaceDropdown != null &&
                    _outsideSurfaceDropdown.value >= 0 &&
                    _outsideSurfaceDropdown.value <
                    TerrainImageClassificationCatalog.OutsideSurfaces.Count &&
                    TerrainImageClassificationCatalog.OutsideSurfaces[
                        _outsideSurfaceDropdown.value].SupportsBiotope;
                _outsideBiotopeDropdown.interactable =
                    outsideEnabled && supportsBiotope;
            }
            if (_outsideElevationInput != null)
            {
                bool automatic =
                    _outsideSurfaceDropdown != null &&
                    _outsideSurfaceDropdown.value >= 0 &&
                    _outsideSurfaceDropdown.value <
                    TerrainImageClassificationCatalog.OutsideSurfaces.Count &&
                    string.Equals(
                        TerrainImageClassificationCatalog.OutsideSurfaces[
                            _outsideSurfaceDropdown.value].Id,
                        "auto",
                        StringComparison.Ordinal);
                _outsideElevationInput.interactable =
                    outsideEnabled && !automatic;
            }
        }

        private bool HasValidFeatureSelection()
        {
            if (_surfaceDropdown == null ||
                _elevationInput == null ||
                _surfaceDropdown.value <= 0 ||
                _surfaceDropdown.value >
                TerrainImageClassificationCatalog.Surfaces.Count ||
                !short.TryParse(
                    _elevationInput.text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out short elevation) ||
                !TerrainElevationEncoding.IsDataValue(elevation) ||
                elevation == TerrainElevationEncoding.NoData)
            {
                return false;
            }

            TerrainImageClassOption surface =
                TerrainImageClassificationCatalog.Surfaces[
                    _surfaceDropdown.value - 1];
            if (surface.SupportsBiotope &&
                string.IsNullOrWhiteSpace(_selectedBiotopeId))
            {
                return false;
            }

            return _drawMode !=
                   TerrainImageClassificationDrawMode.Line ||
                   int.TryParse(
                       _lineWidthInput?.text,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out int width) &&
                   width >= 1 &&
                   width <=
                   TerrainImageClassificationProfile.MaximumLineWidthCells;
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
            if (button == null)
            {
                return;
            }
            Image image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = selected
                    ? new UnityColor(0.91f, 0.34f, 0.19f, 1f)
                    : new UnityColor(0.36f, 0.38f, 0.32f, 1f);
            }
        }

        private void AddPolygonVertex(int sourceX, int sourceY)
        {
            TerrainImageClassificationVertex snapped =
                ResolveDraftVertex(sourceX, sourceY);
            sourceX = snapped.X;
            sourceY = snapped.Y;
            if (_drawMode !=
                    TerrainImageClassificationDrawMode.MapBoundary &&
                !_profile.IsInsideMapBoundary(sourceX, sourceY))
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_outside_boundary"),
                    true);
                return;
            }
            if (_draftVertices.Count >=
                TerrainImageClassificationProfile.MaximumRegionVertices)
            {
                string limitKey;
                if (_drawMode ==
                    TerrainImageClassificationDrawMode.MapBoundary)
                {
                    limitKey =
                        "terrain_lab_manual_boundary_vertex_limit";
                }
                else if (_drawMode ==
                         TerrainImageClassificationDrawMode.Line)
                {
                    limitKey =
                        "terrain_lab_manual_line_vertex_limit";
                }
                else
                {
                    limitKey =
                        "terrain_lab_manual_polygon_vertex_limit";
                }
                SetPanelStatus(
                    LM.Get(limitKey),
                    true);
                return;
            }
            if (_draftVertices.Count > 0)
            {
                TerrainImageClassificationVertex previous =
                    _draftVertices[_draftVertices.Count - 1];
                if (previous.X == sourceX && previous.Y == sourceY)
                {
                    return;
                }
            }

            _draftVertices.Add(
                new TerrainImageClassificationVertex(sourceX, sourceY));
            RefreshDraftGraphic();
            UpdateLabels();
            UpdateModeButtons();
            string vertexKey;
            if (_drawMode ==
                TerrainImageClassificationDrawMode.MapBoundary)
            {
                vertexKey =
                    "terrain_lab_manual_boundary_vertex_format";
            }
            else if (_drawMode ==
                     TerrainImageClassificationDrawMode.Line)
            {
                vertexKey =
                    "terrain_lab_manual_line_vertex_format";
            }
            else
            {
                vertexKey =
                    "terrain_lab_manual_polygon_vertex_format";
            }
            SetPanelStatus(
                string.Format(
                    LM.Get(vertexKey),
                    _draftVertices.Count),
                false);
        }

        private TerrainImageClassificationVertex ResolveDraftVertex(
            int sourceX,
            int sourceY)
        {
            return TrySnapToExistingVertex(
                    sourceX,
                    sourceY,
                    out TerrainImageClassificationVertex snapped)
                ? snapped
                : new TerrainImageClassificationVertex(sourceX, sourceY);
        }

        private bool TrySnapToExistingVertex(
            int sourceX,
            int sourceY,
            out TerrainImageClassificationVertex snapped)
        {
            TerrainImageClassificationVertex nearest = null;
            long nearestSquared =
                (long)VertexSnapRadiusSourcePixels *
                VertexSnapRadiusSourcePixels + 1L;

            void Consider(TerrainImageClassificationVertex vertex)
            {
                if (vertex == null)
                {
                    return;
                }
                long dx = vertex.X - sourceX;
                long dy = vertex.Y - sourceY;
                long squared = dx * dx + dy * dy;
                if (squared >= nearestSquared)
                {
                    return;
                }
                nearestSquared = squared;
                nearest = vertex;
            }

            foreach (TerrainImageClassificationRegion region in
                     _profile?.Regions ??
                     Enumerable.Empty<TerrainImageClassificationRegion>())
            {
                foreach (TerrainImageClassificationVertex vertex in
                         region?.Vertices ??
                         Enumerable.Empty<TerrainImageClassificationVertex>())
                {
                    Consider(vertex);
                }
            }
            foreach (TerrainImageClassificationLine line in
                     _profile?.Lines ??
                     Enumerable.Empty<TerrainImageClassificationLine>())
            {
                foreach (TerrainImageClassificationVertex vertex in
                         line?.Vertices ??
                         Enumerable.Empty<TerrainImageClassificationVertex>())
                {
                    Consider(vertex);
                }
            }
            foreach (TerrainImageClassificationVertex vertex in
                     _profile?.MapBoundary?.Vertices ??
                     Enumerable.Empty<TerrainImageClassificationVertex>())
            {
                Consider(vertex);
            }
            foreach (TerrainImageClassificationSample sample in
                     _profile?.Samples ??
                     Enumerable.Empty<TerrainImageClassificationSample>())
            {
                if (sample != null)
                {
                    Consider(
                        new TerrainImageClassificationVertex(
                            sample.X,
                            sample.Y,
                            sample.Elevation));
                }
            }

            if (nearest == null)
            {
                snapped = null;
                return false;
            }
            snapped = new TerrainImageClassificationVertex(
                nearest.X,
                nearest.Y,
                nearest.Elevation);
            return true;
        }

        private void FinishPolygon()
        {
            if (_drawMode ==
                    TerrainImageClassificationDrawMode.None ||
                _drawMode ==
                    TerrainImageClassificationDrawMode.Point ||
                _drawMode ==
                    TerrainImageClassificationDrawMode.DeletePolygon ||
                _draftGeometryComplete)
            {
                return;
            }
            int minimumVertices =
                _drawMode ==
                TerrainImageClassificationDrawMode.Line
                    ? 2
                    : 3;
            if (_draftVertices.Count < minimumVertices)
            {
                string errorKey;
                if (_drawMode ==
                    TerrainImageClassificationDrawMode.MapBoundary)
                {
                    errorKey =
                        "terrain_lab_manual_boundary_need_vertices";
                }
                else if (_drawMode ==
                         TerrainImageClassificationDrawMode.Line)
                {
                    errorKey =
                        "terrain_lab_manual_line_need_vertices";
                }
                else
                {
                    errorKey =
                        "terrain_lab_manual_polygon_need_vertices";
                }
                SetPanelStatus(
                    LM.Get(errorKey),
                    true);
                return;
            }

            try
            {
                bool boundaryMode =
                    _drawMode ==
                    TerrainImageClassificationDrawMode.MapBoundary;
                if (boundaryMode)
                {
                    _profile.SetMapBoundary(_draftVertices);
                    _draftVertices.Clear();
                    _draftGeometryComplete = false;
                    _hoverVertex = null;
                    _hasHoverVertex = false;
                    _drawMode =
                        TerrainImageClassificationDrawMode.None;
                    ResetClassificationSelection();
                    SaveProfile();
                    SyncOutsideControlsFromProfile();
                    RebuildAnnotations();
                    UpdateLabels();
                    UpdateMapSizePreview();
                    SetPanelStatus(
                        LM.Get(
                            "terrain_lab_manual_boundary_saved"),
                        false);
                    return;
                }

                _draftGeometryComplete = true;
                _hoverVertex = null;
                _hasHoverVertex = false;
                RefreshDraftGraphic();
                UpdateLabels();
                UpdateModeButtons();
                SetPanelStatus(
                    LM.Get(
                        "terrain_lab_manual_geometry_completed"),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void PublishFeature()
        {
            if (_profile == null ||
                !_draftGeometryComplete ||
                _draftVertices.Count == 0 ||
                (_drawMode !=
                     TerrainImageClassificationDrawMode.Point &&
                 _drawMode !=
                     TerrainImageClassificationDrawMode.Line &&
                 _drawMode !=
                     TerrainImageClassificationDrawMode.Polygon))
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_complete_geometry_first"),
                    true);
                return;
            }
            if (!TryReadSelection(
                    out TerrainImageClassOption surface,
                    out TerrainImageBiotopeOption biotope,
                    out short elevation))
            {
                return;
            }

            try
            {
                TerrainImageClassificationDrawMode publishedMode =
                    _drawMode;
                switch (_drawMode)
                {
                    case TerrainImageClassificationDrawMode.Point:
                        TerrainImageClassificationVertex point =
                            _draftVertices[0];
                        _profile.AddOrReplaceSample(
                            point.X,
                            point.Y,
                            surface.Id,
                            biotope.Id,
                            elevation);
                        break;
                    case TerrainImageClassificationDrawMode.Line:
                        if (!TryReadLineWidth(out int widthCells))
                        {
                            return;
                        }
                        _profile.AddLine(
                            _draftVertices,
                            surface.Id,
                            biotope.Id,
                            elevation,
                            widthCells);
                        break;
                    case TerrainImageClassificationDrawMode.Polygon:
                        _profile.AddRegion(
                            _draftVertices,
                            surface.Id,
                            biotope.Id,
                            elevation);
                        break;
                }

                SaveProfile();
                ResetFeatureDraft();
                RebuildAnnotations();
                UpdateLabels();
                SetPanelStatus(
                    string.Format(
                        LM.Get(
                            "terrain_lab_manual_feature_published_format"),
                        LM.Get(
                            GetGeometryLocalizationKey(
                                publishedMode))),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void ResetFeatureDraft()
        {
            _draftVertices.Clear();
            _draftGeometryComplete = false;
            _hoverVertex = null;
            _hasHoverVertex = false;
            _drawMode = TerrainImageClassificationDrawMode.None;
            ResetClassificationSelection();
            UpdateModeButtons();
        }

        private static string GetGeometryLocalizationKey(
            TerrainImageClassificationDrawMode mode)
        {
            switch (mode)
            {
                case TerrainImageClassificationDrawMode.Point:
                    return "terrain_lab_manual_mode_point";
                case TerrainImageClassificationDrawMode.Line:
                    return "terrain_lab_manual_mode_line";
                case TerrainImageClassificationDrawMode.Polygon:
                    return "terrain_lab_manual_mode_polygon";
                default:
                    return "terrain_lab_manual_geometry_heading";
            }
        }

        private void CancelPolygon(bool notify)
        {
            _hoverVertex = null;
            _hasHoverVertex = false;
            if (_draftVertices.Count == 0)
            {
                UpdateModeButtons();
                return;
            }
            _draftVertices.Clear();
            _draftGeometryComplete = false;
            TerrainImageClassificationDrawMode cancelledMode =
                _drawMode;
            _drawMode = TerrainImageClassificationDrawMode.None;
            ResetClassificationSelection();
            RefreshDraftGraphic();
            UpdateLabels();
            UpdateModeButtons();
            if (notify)
            {
                SetPanelStatus(
                    LM.Get(
                        cancelledMode ==
                        TerrainImageClassificationDrawMode.MapBoundary
                            ? "terrain_lab_manual_boundary_cancelled"
                            : "terrain_lab_manual_draft_cancelled"),
                    false);
            }
        }

        private void CancelPolygonOrBoundary()
        {
            if (_drawMode !=
                    TerrainImageClassificationDrawMode.MapBoundary ||
                _draftVertices.Count > 0 ||
                _profile?.MapBoundary == null)
            {
                CancelPolygon(true);
                return;
            }

            try
            {
                _profile.ClearMapBoundary();
                _drawMode = TerrainImageClassificationDrawMode.None;
                ResetClassificationSelection();
                SaveProfile();
                SyncOutsideControlsFromProfile();
                RebuildAnnotations();
                UpdateLabels();
                UpdateMapSizePreview();
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_boundary_removed"),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void SaveProfileCommand()
        {
            try
            {
                if (_draftVertices.Count > 0)
                {
                    SetPanelStatus(
                        LM.Get("terrain_lab_manual_finish_draft"),
                        true);
                    return;
                }

                SaveProfile();
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_profile_saved"),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void QueueConversion()
        {
            try
            {
                if (_draftVertices.Count > 0)
                {
                    SetPanelStatus(
                        LM.Get("terrain_lab_manual_finish_draft"),
                        true);
                    return;
                }
                if (_profile == null || !_profile.HasUsableTraining)
                {
                    SetPanelStatus(
                        LM.Get("terrain_lab_manual_need_samples"),
                        true);
                    return;
                }

                SaveProfile();
                if (!_workspace.TryQueueImageNow(
                        _imagePath,
                        out string error))
                {
                    SetPanelStatus(error, true);
                    return;
                }

                SetPanelStatus(
                    LM.Get("terrain_lab_manual_queued"),
                    false);
                SetStatus(
                    LM.Get("terrain_lab_status_manual_queued"),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void UndoSample()
        {
            try
            {
                if (_draftVertices.Count > 0)
                {
                    _draftVertices.RemoveAt(_draftVertices.Count - 1);
                    _draftGeometryComplete = false;
                    if (_draftVertices.Count == 0)
                    {
                        ResetFeatureDraft();
                    }
                    RefreshDraftGraphic();
                    UpdateLabels();
                    SetPanelStatus(
                        LM.Get("terrain_lab_manual_vertex_removed"),
                        false);
                    return;
                }
                if (_profile == null || !_profile.RemoveLastAnnotation())
                {
                    SetPanelStatus(
                        LM.Get("terrain_lab_manual_nothing_to_undo"),
                        false);
                    return;
                }

                SaveProfile();
                RebuildAnnotations();
                UpdateLabels();
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_sample_removed"),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void ClearPolygons()
        {
            bool hasDraft =
                _drawMode ==
                    TerrainImageClassificationDrawMode.Polygon &&
                _draftVertices.Count > 0;
            int savedCount = _profile?.Regions?.Count ?? 0;
            if (_profile == null || (savedCount == 0 && !hasDraft))
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_no_polygons"),
                    false);
                return;
            }

            try
            {
                int removed = _profile.ClearRegions();
                if (hasDraft)
                {
                    _draftVertices.Clear();
                    _draftGeometryComplete = false;
                    _hoverVertex = null;
                    _hasHoverVertex = false;
                    _drawMode =
                        TerrainImageClassificationDrawMode.None;
                    ResetClassificationSelection();
                    RefreshDraftGraphic();
                }
                SaveProfile();
                RebuildAnnotations();
                UpdateLabels();
                SetPanelStatus(
                    string.Format(
                        LM.Get(
                            "terrain_lab_manual_polygons_cleared_format"),
                        removed + (hasDraft ? 1 : 0)),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private void ClearSamples()
        {
            if (_profile == null ||
                ((_profile.Samples?.Count ?? 0) == 0 &&
                 (_profile.Regions?.Count ?? 0) == 0 &&
                 (_profile.Lines?.Count ?? 0) == 0 &&
                 _profile.MapBoundary == null &&
                 _draftVertices.Count == 0))
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_nothing_to_undo"),
                    false);
                return;
            }

            if (Time.unscaledTime > _clearArmedUntil)
            {
                _clearArmedUntil = Time.unscaledTime + 3f;
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_clear_confirm"),
                    true);
                return;
            }

            try
            {
                _profile.Samples.Clear();
                _profile.Regions.Clear();
                _profile.Lines.Clear();
                _profile.ClearMapBoundary();
                _draftVertices.Clear();
                _draftGeometryComplete = false;
                _drawMode = TerrainImageClassificationDrawMode.None;
                ResetClassificationSelection();
                SaveProfile();
                SyncOutsideControlsFromProfile();
                RebuildAnnotations();
                UpdateLabels();
                _clearArmedUntil = 0f;
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_cleared"),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
        }

        private bool RemoveNearestAnnotation(int sourceX, int sourceY)
        {
            if (_profile == null)
            {
                return false;
            }

            double maximumDistance = Math.Max(
                3.0,
                Math.Max(_sourceWidth, _sourceHeight) * 0.012 / _zoom);
            double maximumSquared = maximumDistance * maximumDistance;
            int nearest = -1;
            double nearestSquared = double.MaxValue;
            for (int index = 0;
                 index < (_profile.Samples?.Count ?? 0);
                 index++)
            {
                TerrainImageClassificationSample sample =
                    _profile.Samples[index];
                double dx = sample.X - sourceX;
                double dy = sample.Y - sourceY;
                double distanceSquared = dx * dx + dy * dy;
                if (distanceSquared <= maximumSquared &&
                    distanceSquared < nearestSquared)
                {
                    nearest = index;
                    nearestSquared = distanceSquared;
                }
            }

            if (nearest >= 0)
            {
                _profile.Samples.RemoveAt(nearest);
                return true;
            }

            return _profile.RemoveRegionAt(sourceX, sourceY);
        }

        private void SaveProfile()
        {
            if (_profile == null || string.IsNullOrWhiteSpace(_imagePath))
            {
                throw new InvalidOperationException(
                    "No source image is open.");
            }

            _profile.Settings.InterpolateElevationGlobally = true;
            _profile.Settings.LongSideBlocks = ReadLongSideBlocks();
            _profile.Save(_imagePath);
        }

        private int ReadLongSideBlocks()
        {
            GetOutputAspectDimensions(
                out int aspectWidth,
                out int aspectHeight);
            if (_longSideInput == null ||
                !int.TryParse(
                    _longSideInput.text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int value) ||
                !TerrainMapLimits.TryGetBlockDimensions(
                    aspectWidth,
                    aspectHeight,
                    value,
                    out _,
                    out _))
            {
                throw new InvalidDataException(
                    LM.Get("terrain_lab_output_size_error"));
            }
            return value;
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

        private bool TryReadSelection(
            out TerrainImageClassOption surface,
            out TerrainImageBiotopeOption biotope,
            out short elevation)
        {
            surface = null;
            biotope = null;
            elevation = 0;
            if (_surfaceDropdown == null ||
                _surfaceDropdown.value <= 0 ||
                _surfaceDropdown.value >
                TerrainImageClassificationCatalog.Surfaces.Count ||
                !TryReadElevation(out elevation))
            {
                if (_surfaceDropdown?.value <= 0)
                {
                    SetPanelStatus(
                        LM.Get(
                            "terrain_lab_manual_select_all_attributes"),
                        true);
                }
                return false;
            }
            surface = TerrainImageClassificationCatalog.Surfaces[
                _surfaceDropdown.value - 1];
            biotope = surface.SupportsBiotope
                ? TerrainImageClassificationCatalog.FindBiotope(
                    _selectedBiotopeId)
                : TerrainImageClassificationCatalog.FindBiotope("none");
            if (biotope == null)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_select_all_attributes"),
                    true);
                return false;
            }
            return true;
        }

        private bool TryReadLineWidth(out int widthCells)
        {
            if (!int.TryParse(
                    _lineWidthInput?.text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out widthCells) ||
                widthCells < 1 ||
                widthCells >
                TerrainImageClassificationProfile.MaximumLineWidthCells)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_line_width_error"),
                    true);
                widthCells = 0;
                return false;
            }
            return true;
        }

        private bool TryReadElevation(out short elevation)
        {
            if (!short.TryParse(
                    _elevationInput.text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out elevation) ||
                !TerrainElevationEncoding.IsDataValue(elevation) ||
                elevation == TerrainElevationEncoding.NoData)
            {
                SetPanelStatus(
                    LM.Get("terrain_lab_manual_elevation_error"),
                    true);
                elevation = 0;
                return false;
            }

            return true;
        }

        private bool TryReadElevationWithoutNotification(
            out short elevation)
        {
            return short.TryParse(
                       _elevationInput?.text,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out elevation) &&
                   TerrainElevationEncoding.IsDataValue(elevation) &&
                   elevation != TerrainElevationEncoding.NoData;
        }

        private bool TryScreenToSource(
            Vector2 screenPosition,
            out int sourceX,
            out int sourceY)
        {
            sourceX = 0;
            sourceY = 0;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
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
            PositionMarkers();
            ClampPan();
        }

        private void ClampPan()
        {
            if (_imageRect == null || _viewport == null)
            {
                return;
            }

            Vector2 scaledSize = _imageRect.rect.size * _zoom;
            Vector2 viewportSize = _viewport.rect.size;
            float limitX = Mathf.Max(0f, (scaledSize.x - viewportSize.x) * 0.5f);
            float limitY = Mathf.Max(0f, (scaledSize.y - viewportSize.y) * 0.5f);
            Vector2 position = _imageRect.anchoredPosition;
            position.x = Mathf.Clamp(position.x, -limitX, limitX);
            position.y = Mathf.Clamp(position.y, -limitY, limitY);
            _imageRect.anchoredPosition = position;
        }

        private void RebuildAnnotations()
        {
            foreach (GameObject marker in _markers)
            {
                Destroy(marker);
            }
            _markers.Clear();
            foreach (TerrainImagePolygonGraphic graphic in _regionGraphics)
            {
                if (graphic != null)
                {
                    Destroy(graphic.gameObject);
                }
            }
            _regionGraphics.Clear();
            if (_draftGraphic != null)
            {
                Destroy(_draftGraphic.gameObject);
                _draftGraphic = null;
            }

            if (_profile == null)
            {
                return;
            }

            if (_profile.MapBoundary?.Vertices?.Count >= 3)
            {
                TerrainImagePolygonGraphic boundary =
                    CreatePolygonGraphic(
                        _profile.MapBoundary.Vertices,
                        "map_boundary",
                        null,
                        true,
                        true,
                        "TerrainLabClassificationMapBoundary",
                        0.035f);
                _regionGraphics.Add(boundary);
            }

            foreach (TerrainImageClassificationRegion region in
                     _profile.Regions ??
                     Enumerable.Empty<TerrainImageClassificationRegion>())
            {
                TerrainImagePolygonGraphic graphic = CreatePolygonGraphic(
                    region.Vertices,
                    region.Surface,
                    region.Elevation,
                    true,
                    true,
                    "TerrainLabClassificationRegion");
                _regionGraphics.Add(graphic);
            }

            foreach (TerrainImageClassificationLine line in
                     _profile.Lines ??
                     Enumerable.Empty<TerrainImageClassificationLine>())
            {
                TerrainImagePolygonGraphic graphic = CreatePolygonGraphic(
                    line.Vertices,
                    line.Surface,
                    line.Elevation,
                    false,
                    true,
                    "TerrainLabClassificationLine",
                    0.22f,
                    Mathf.Min(14f, 2.2f + line.WidthCells));
                _regionGraphics.Add(graphic);
            }

            RefreshDraftGraphic();
            foreach (TerrainImageClassificationSample sample in
                     _profile.Samples ??
                     Enumerable.Empty<TerrainImageClassificationSample>())
            {
                GameObject marker = new GameObject(
                    "TerrainLabClassificationSample",
                    typeof(RectTransform),
                    typeof(Image),
                    typeof(UnityEngine.UI.Outline));
                marker.transform.SetParent(_markerRoot, false);
                RectTransform rect = marker.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(7f, 7f);
                Image image = marker.GetComponent<Image>();
                image.color =
                    GetElevationVertexColor(sample.Elevation);
                image.raycastTarget = false;
                UnityEngine.UI.Outline outline =
                    marker.GetComponent<UnityEngine.UI.Outline>();
                outline.effectColor = UnityColor.black;
                outline.effectDistance = new Vector2(1f, -1f);
                _markers.Add(marker);
            }

            PositionMarkers();
            UpdateMarkerScale();
        }

        private TerrainImagePolygonGraphic CreatePolygonGraphic(
            IEnumerable<TerrainImageClassificationVertex> vertices,
            string surface,
            short? elevation,
            bool closed,
            bool showVertices,
            string objectName,
            float closedFillAlpha = 0.22f,
            float lineThickness = 2.2f)
        {
            List<TerrainImageClassificationVertex> vertexList =
                vertices?
                    .Where(vertex => vertex != null)
                    .ToList() ??
                new List<TerrainImageClassificationVertex>();
            GameObject polygonObject = new GameObject(
                objectName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TerrainImagePolygonGraphic));
            polygonObject.transform.SetParent(_markerRoot, false);
            RectTransform rect = polygonObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            TerrainImagePolygonGraphic graphic =
                polygonObject.GetComponent<TerrainImagePolygonGraphic>();
            graphic.Configure(
                vertexList,
                _sourceWidth,
                _sourceHeight,
                surface,
                GetSurfaceColor(surface),
                elevation.HasValue
                    ? GetElevationVertexColor(elevation.Value)
                    : new UnityColor(0.12f, 0.95f, 1f, 1f),
                closed,
                showVertices,
                1f / Mathf.Max(_zoom, 0.01f),
                closedFillAlpha,
                lineThickness,
                vertexList.Select(vertex =>
                    GetElevationVertexColor(
                        vertex.Elevation ??
                        elevation ??
                        (short)0)));
            return graphic;
        }

        private void RefreshDraftGraphic()
        {
            if (_markerRoot == null)
            {
                return;
            }
            bool boundaryMode =
                _drawMode ==
                TerrainImageClassificationDrawMode.MapBoundary;
            if (_draftVertices.Count == 0)
            {
                if (_draftGraphic != null)
                {
                    Destroy(_draftGraphic.gameObject);
                    _draftGraphic = null;
                }
                return;
            }

            bool hasSurface =
                !boundaryMode &&
                _surfaceDropdown != null &&
                _surfaceDropdown.value > 0 &&
                _surfaceDropdown.value <=
                TerrainImageClassificationCatalog.Surfaces.Count;
            string surface = boundaryMode
                ? "map_boundary"
                : hasSurface
                    ? TerrainImageClassificationCatalog.Surfaces[
                        _surfaceDropdown.value - 1].Id
                    : "draft";
            UnityColor surfaceColor = hasSurface || boundaryMode
                ? GetSurfaceColor(surface)
                : new UnityColor(0.65f, 0.67f, 0.62f, 1f);
            UnityColor vertexColor =
                TryReadElevationWithoutNotification(out short elevation)
                    ? GetElevationVertexColor(elevation)
                    : new UnityColor(0.12f, 0.95f, 1f, 1f);
            List<TerrainImageClassificationVertex> displayVertices =
                new List<TerrainImageClassificationVertex>(_draftVertices);
            TerrainImageClassificationVertex last =
                displayVertices[displayVertices.Count - 1];
            if (!_draftGeometryComplete &&
                _hasHoverVertex &&
                _hoverVertex != null &&
                (last.X != _hoverVertex.X || last.Y != _hoverVertex.Y))
            {
                displayVertices.Add(_hoverVertex);
            }
            bool closed =
                _draftGeometryComplete &&
                (_drawMode ==
                     TerrainImageClassificationDrawMode.Polygon ||
                 boundaryMode);
            if (_draftGraphic == null)
            {
                _draftGraphic = CreatePolygonGraphic(
                    displayVertices,
                    surface,
                    TryReadElevationWithoutNotification(
                        out short draftElevation)
                        ? draftElevation
                        : (short?)null,
                    closed,
                    true,
                    "TerrainLabClassificationDraftRegion",
                    0.22f,
                    GetDraftLineThickness());
            }
            else
            {
                _draftGraphic.Configure(
                    displayVertices,
                    _sourceWidth,
                    _sourceHeight,
                    surface,
                    surfaceColor,
                    vertexColor,
                    closed,
                    true,
                    1f / Mathf.Max(_zoom, 0.01f),
                    0.22f,
                    GetDraftLineThickness());
            }
        }

        private float GetDraftLineThickness()
        {
            if (_drawMode !=
                    TerrainImageClassificationDrawMode.Line ||
                !int.TryParse(
                    _lineWidthInput?.text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int widthCells))
            {
                return 2.2f;
            }
            return Mathf.Min(
                14f,
                2.2f + Mathf.Clamp(
                    widthCells,
                    1,
                    TerrainImageClassificationProfile
                        .MaximumLineWidthCells));
        }

        private void PositionMarkers()
        {
            if (_profile?.Samples == null || _imageRect == null)
            {
                return;
            }

            Vector2 size = _imageRect.rect.size;
            for (int index = 0;
                 index < _profile.Samples.Count && index < _markers.Count;
                 index++)
            {
                TerrainImageClassificationSample sample =
                    _profile.Samples[index];
                float x =
                    ((sample.X + 0.5f) / _sourceWidth - 0.5f) * size.x;
                float y =
                    (0.5f - (sample.Y + 0.5f) / _sourceHeight) * size.y;
                _markers[index].GetComponent<RectTransform>().anchoredPosition =
                    new Vector2(x, y);
            }
        }

        private void UpdateMarkerScale()
        {
            float inverse = 1f / Mathf.Max(_zoom, 0.01f);
            Vector3 scale = new Vector3(inverse, inverse, 1f);
            foreach (GameObject marker in _markers)
            {
                marker.transform.localScale = scale;
            }
            foreach (TerrainImagePolygonGraphic graphic in _regionGraphics)
            {
                graphic?.SetInverseZoom(inverse);
            }
            _draftGraphic?.SetInverseZoom(inverse);
        }

        private void UpdateLabels()
        {
            if (_profile == null)
            {
                return;
            }

            _fileLabel.text = string.Format(
                LM.Get("terrain_lab_manual_source_format"),
                Path.GetFileName(_imagePath),
                _sourceWidth,
                _sourceHeight);
            _sampleLabel.text = string.Format(
                LM.Get("terrain_lab_manual_samples_format"),
                _profile.Samples.Count,
                TerrainImageClassificationProfile.MaximumSamples,
                _profile.Lines.Count,
                TerrainImageClassificationProfile.MaximumLines,
                _profile.Regions.Count,
                TerrainImageClassificationProfile.MaximumRegions,
                _draftVertices.Count,
                _profile.MapBoundary?.Vertices?.Count ?? 0);
            UpdateModeButtons();
        }

        private static Texture2D LoadPreview(
            string path,
            out int sourceWidth,
            out int sourceHeight)
        {
            return TerrainImagePreviewLoader.Load(
                path,
                "TerrainLabManualClassificationPreview",
                out sourceWidth,
                out sourceHeight);
        }

        private static UnityColor GetSurfaceColor(string surface)
        {
            switch (surface)
            {
                case "draft":
                    return new UnityColor(0.65f, 0.67f, 0.62f, 1f);
                case "map_boundary":
                    return new UnityColor(0.12f, 0.95f, 1f, 1f);
                case "deep_ocean":
                    return new UnityColor(0.15f, 0.35f, 0.95f, 1f);
                case "shelf":
                    return new UnityColor(0.1f, 0.68f, 0.95f, 1f);
                case "shallow_water":
                    return new UnityColor(0.2f, 0.95f, 0.95f, 1f);
                case "river_lake":
                    return new UnityColor(0.1f, 0.85f, 0.65f, 1f);
                case "sand":
                    return new UnityColor(1f, 0.84f, 0.3f, 1f);
                case "plain":
                    return new UnityColor(0.42f, 0.9f, 0.3f, 1f);
                case "lowland":
                    return new UnityColor(0.28f, 0.7f, 0.32f, 1f);
                case "upland":
                    return new UnityColor(0.68f, 0.62f, 0.24f, 1f);
                case "hills":
                    return new UnityColor(0.92f, 0.5f, 0.18f, 1f);
                case "rocks":
                    return new UnityColor(0.75f, 0.75f, 0.72f, 1f);
                case "summit":
                    return UnityColor.white;
                case "depression":
                    return new UnityColor(0.73f, 0.28f, 0.7f, 1f);
                default:
                    return UnityColor.magenta;
            }
        }

        private static UnityColor GetElevationVertexColor(short elevation)
        {
            Color32 color = TerrainElevationOverlay.GetColor(
                elevation,
                0,
                TerrainElevationEncoding.Minimum,
                TerrainElevationEncoding.Maximum);
            return new UnityColor(
                color.r / 255f,
                color.g / 255f,
                color.b / 255f,
                1f);
        }

        private void CreateBiotopePalette(Transform parent)
        {
            _biotopePalette = new GameObject(
                "TerrainLabClassificationBiotopePalette",
                typeof(RectTransform),
                typeof(Image),
                typeof(RectMask2D),
                typeof(ScrollRect));
            _biotopePalette.transform.SetParent(parent, false);
            RectTransform paletteRect =
                _biotopePalette.GetComponent<RectTransform>();
            paletteRect.anchorMin = new Vector2(0f, 1f);
            paletteRect.anchorMax = new Vector2(1f, 1f);
            paletteRect.pivot = new Vector2(0.5f, 1f);
            paletteRect.offsetMin = new Vector2(12f, -72f);
            paletteRect.offsetMax = new Vector2(-12f, -8f);
            Image background = _biotopePalette.GetComponent<Image>();
            Image nativePanel = ToolbarButtons.instance?.main_background;
            background.sprite = nativePanel?.sprite ??
                                SpriteTextureLoader.getSprite(
                                    "ui/special/darkInputFieldEmpty");
            background.type = Image.Type.Sliced;
            background.material = nativePanel?.material;
            background.color = nativePanel != null
                ? nativePanel.color
                : new UnityColor(0.20f, 0.22f, 0.18f, 0.98f);

            GameObject contentObject = new GameObject(
                "TerrainLabClassificationBiotopePaletteContent",
                typeof(RectTransform),
                typeof(GridLayoutGroup));
            contentObject.transform.SetParent(_biotopePalette.transform, false);
            RectTransform contentRect =
                contentObject.GetComponent<RectTransform>();
            int rows = 2;
            int choiceCount =
                TerrainImageClassificationCatalog.QuickPaletteSurfaces.Count +
                TerrainImageClassificationCatalog.SelectableBiotopes.Count;
            int columns = Mathf.CeilToInt(
                choiceCount / (float)rows);
            contentRect.anchorMin = new Vector2(0f, 0.5f);
            contentRect.anchorMax = new Vector2(0f, 0.5f);
            contentRect.pivot = new Vector2(0f, 0.5f);
            contentRect.sizeDelta =
                new Vector2(columns * 34f + 8f, 64f);
            GridLayoutGroup grid =
                contentObject.GetComponent<GridLayoutGroup>();
            grid.padding = new RectOffset(4, 4, 3, 3);
            grid.cellSize = new Vector2(28f, 28f);
            grid.spacing = new Vector2(6f, 2f);
            grid.startAxis = GridLayoutGroup.Axis.Vertical;
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.FixedRowCount;
            grid.constraintCount = rows;

            ScrollRect scroll =
                _biotopePalette.GetComponent<ScrollRect>();
            scroll.content = contentRect;
            scroll.viewport = paletteRect;
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 18f;

            foreach (TerrainImageClassOption option in
                     TerrainImageClassificationCatalog.QuickPaletteSurfaces)
            {
                string id = option.Id;
                int dropdownIndex = FindSurfaceDropdownIndex(id);
                Button button = CreateClassificationPaletteButton(
                    contentObject.transform,
                    "TerrainLabQuickSurface_" + id,
                    "SurfaceMorphotype",
                    TerrainImageUiVisuals.GetSurfaceSprite(id),
                    option.LocalizationKey,
                    delegate
                    {
                        if (_surfaceDropdown == null ||
                            dropdownIndex <= 0 ||
                            _surfaceDropdown.value == dropdownIndex)
                        {
                            return;
                        }

                        _surfaceDropdown.SetValueWithoutNotify(dropdownIndex);
                        _surfaceDropdown.RefreshShownValue();
                        HandleSurfaceChanged(dropdownIndex);
                    },
                    out Image lamp);
                _quickSurfaceButtons[id] = button;
                _quickSurfaceSelectionLamps[id] = lamp;
            }

            foreach (TerrainImageBiotopeOption option in
                     TerrainImageClassificationCatalog.SelectableBiotopes)
            {
                string id = option.Id;
                Button button = CreateClassificationPaletteButton(
                    contentObject.transform,
                    "TerrainLabBiotope_" + id,
                    "BiomeSurface",
                    FindBiotopeSurfaceSprite(id),
                    option.LocalizationKey,
                    delegate
                    {
                        _selectedBiotopeId = id;
                        UpdateBiotopePalette();
                        RefreshDraftGraphic();
                        UpdateModeButtons();
                    },
                    out Image lamp);
                _biotopeButtons[id] = button;
                _biotopeSelectionLamps[id] = lamp;
            }
            _biotopePalette.SetActive(false);
        }

        private Button CreateClassificationPaletteButton(
            Transform parent,
            string objectName,
            string iconName,
            Sprite sprite,
            string localizationKey,
            Action onClick,
            out Image lamp)
        {
            GameObject buttonObject =
                DefaultControls.CreateButton(_defaultResources);
            buttonObject.name = objectName;
            buttonObject.transform.SetParent(parent, false);
            Text label = buttonObject.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.enabled = false;
            }

            Image buttonImage = buttonObject.GetComponent<Image>();
            buttonImage.sprite = ToolbarButtons.getSpriteButtonNormal();
            buttonImage.type = Image.Type.Sliced;
            buttonImage.color =
                new UnityColor(0.31f, 0.33f, 0.28f, 1f);
            Button button = buttonObject.GetComponent<Button>();
            button.onClick.AddListener(
                delegate
                {
                    onClick?.Invoke();
                });

            GameObject iconObject = new GameObject(
                iconName,
                typeof(RectTransform),
                typeof(Image));
            iconObject.transform.SetParent(buttonObject.transform, false);
            RectTransform iconRect =
                iconObject.GetComponent<RectTransform>();
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = new Vector2(3f, 3f);
            iconRect.offsetMax = new Vector2(-3f, -3f);
            Image icon = iconObject.GetComponent<Image>();
            icon.sprite = sprite;
            icon.preserveAspect = false;
            icon.raycastTarget = false;

            GameObject lampObject = new GameObject(
                "TerrainLabPaletteSelectionLamp",
                typeof(RectTransform),
                typeof(Image));
            lampObject.transform.SetParent(buttonObject.transform, false);
            RectTransform lampRect =
                lampObject.GetComponent<RectTransform>();
            lampRect.anchorMin = new Vector2(0.5f, 1f);
            lampRect.anchorMax = new Vector2(0.5f, 1f);
            lampRect.pivot = new Vector2(0.5f, 0.5f);
            lampRect.anchoredPosition = new Vector2(0f, 1f);
            lampRect.sizeDelta = new Vector2(6f, 6f);
            lamp = lampObject.GetComponent<Image>();
            lamp.sprite = TerrainImageUiVisuals.GetActivitySprite(true);
            lamp.preserveAspect = true;
            lamp.raycastTarget = false;
            lampObject.SetActive(false);
            ConfigureTooltip(buttonObject, localizationKey);
            return button;
        }

        private static int FindSurfaceDropdownIndex(string surfaceId)
        {
            for (int index = 0;
                 index < TerrainImageClassificationCatalog.Surfaces.Count;
                 index++)
            {
                if (string.Equals(
                        TerrainImageClassificationCatalog.Surfaces[index].Id,
                        surfaceId,
                        StringComparison.Ordinal))
                {
                    return index + 1;
                }
            }
            return 0;
        }

        private static Sprite FindBiotopeSurfaceSprite(string biotopeId)
        {
            return TerrainImageUiVisuals.GetBiotopeSprite(biotopeId);
        }

        private void UpdateBiotopePalette(bool controlsEnabled = true)
        {
            if (_biotopePalette == null)
            {
                return;
            }

            bool hasSurface =
                _surfaceDropdown != null &&
                _surfaceDropdown.value > 0 &&
                _surfaceDropdown.value <=
                TerrainImageClassificationCatalog.Surfaces.Count;
            TerrainImageClassOption surface = hasSurface
                ? TerrainImageClassificationCatalog.Surfaces[
                    _surfaceDropdown.value - 1]
                : null;
            bool supportsBiotope =
                surface != null && surface.SupportsBiotope;
            if (!supportsBiotope)
            {
                _selectedBiotopeId = null;
            }

            _biotopePalette.SetActive(_editorEnabled && _profile != null);
            bool surfaceInteractive =
                controlsEnabled &&
                _editorEnabled &&
                _draftGeometryComplete;
            string selectedSurfaceId = surface?.Id;
            foreach (KeyValuePair<string, Button> pair in
                     _quickSurfaceButtons)
            {
                Button button = pair.Value;
                if (button == null)
                {
                    continue;
                }

                button.interactable = surfaceInteractive;
                bool selected = string.Equals(
                    pair.Key,
                    selectedSurfaceId,
                    StringComparison.Ordinal);
                Image image = button.GetComponent<Image>();
                if (image != null)
                {
                    image.color = selected
                        ? new UnityColor(0.35f, 0.78f, 0.32f, 1f)
                        : surfaceInteractive
                            ? new UnityColor(0.31f, 0.33f, 0.28f, 1f)
                            : new UnityColor(0.18f, 0.18f, 0.16f, 0.48f);
                }
                if (_quickSurfaceSelectionLamps.TryGetValue(
                        pair.Key,
                        out Image surfaceLamp) &&
                    surfaceLamp != null)
                {
                    surfaceLamp.sprite =
                        TerrainImageUiVisuals.GetActivitySprite(selected);
                    surfaceLamp.gameObject.SetActive(selected);
                }
            }

            bool interactive =
                controlsEnabled &&
                _editorEnabled &&
                _draftGeometryComplete &&
                supportsBiotope;
            foreach (KeyValuePair<string, Button> pair in _biotopeButtons)
            {
                Button button = pair.Value;
                if (button == null)
                {
                    continue;
                }
                button.interactable = interactive;
                Image image = button.GetComponent<Image>();
                if (image != null)
                {
                    bool selected = string.Equals(
                        pair.Key,
                        _selectedBiotopeId,
                        StringComparison.Ordinal);
                    image.color = selected
                        ? new UnityColor(0.35f, 0.78f, 0.32f, 1f)
                        : supportsBiotope
                            ? new UnityColor(0.31f, 0.33f, 0.28f, 1f)
                            : new UnityColor(0.18f, 0.18f, 0.16f, 0.48f);
                    if (_biotopeSelectionLamps.TryGetValue(
                            pair.Key,
                            out Image lamp) &&
                        lamp != null)
                    {
                        lamp.sprite =
                            TerrainImageUiVisuals.GetActivitySprite(selected);
                        lamp.gameObject.SetActive(selected);
                    }
                }
            }
        }

        private static Text CreateCoordinateOverlay(Transform parent)
        {
            GameObject backgroundObject = new GameObject(
                "TerrainLabImageCoordinates",
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
                "TerrainLabImageCoordinatesText",
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
                "TerrainLabClassificationRow",
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

        private static Text CreateLabel(
            Transform parent,
            string value,
            int fontSize,
            FontStyle style,
            float height,
            UnityColor color)
        {
            GameObject labelObject = new GameObject(
                "TerrainLabClassificationLabel",
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
                "TerrainLabClassificationFileSelector",
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
            element.preferredWidth = 174f;
            element.flexibleWidth = 1f;
            element.preferredHeight = 36f;

            GameObject textObject = new GameObject(
                "TerrainLabClassificationFileName",
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
            buttonObject.name = "TerrainLabClassificationButton";
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

        private Dropdown CreateDropdown(
            Transform parent,
            IEnumerable<string> values,
            string tooltipKey)
        {
            GameObject dropdownObject =
                DefaultControls.CreateDropdown(_defaultResources);
            dropdownObject.name = "TerrainLabClassificationDropdown";
            dropdownObject.transform.SetParent(parent, false);
            Dropdown dropdown = dropdownObject.GetComponent<Dropdown>();
            dropdown.ClearOptions();
            dropdown.AddOptions(values.ToList());
            foreach (Text text in
                     dropdownObject.GetComponentsInChildren<Text>(true))
            {
                text.font = LocalizedTextManager.current_font;
                text.fontSize = 11;
                text.color = UnityColor.white;
                text.resizeTextForBestFit = true;
                text.resizeTextMinSize = 7;
                text.resizeTextMaxSize = 11;
            }
            Image image = dropdownObject.GetComponent<Image>();
            image.sprite = SpriteTextureLoader.getSprite(
                "ui/special/darkInputFieldEmpty");
            image.type = Image.Type.Sliced;
            image.color = new UnityColor(0.08f, 0.08f, 0.07f, 1f);
            StyleDropdownPopup(dropdownObject);
            LayoutElement element =
                dropdownObject.AddComponent<LayoutElement>();
            element.minWidth = 0f;
            element.flexibleWidth = 1f;
            element.preferredHeight = 28f;
            ConfigureTooltip(dropdownObject, tooltipKey);
            return dropdown;
        }

        private void RefreshLocalizedDropdowns()
        {
            RefreshLocalizedDropdown(
                _surfaceDropdown,
                new[]
                {
                    "terrain_lab_manual_not_selected"
                }.Concat(
                    TerrainImageClassificationCatalog.Surfaces
                        .Select(option => option.LocalizationKey)));
            RefreshLocalizedDropdown(
                _outsideSurfaceDropdown,
                TerrainImageClassificationCatalog.OutsideSurfaces
                    .Select(option => option.LocalizationKey));
            RefreshLocalizedDropdown(
                _outsideBiotopeDropdown,
                TerrainImageClassificationCatalog.OutsideBiotopes
                    .Select(option => option.LocalizationKey));
        }

        private static void RefreshLocalizedDropdown(
            Dropdown dropdown,
            IEnumerable<string> localizationKeys)
        {
            if (dropdown == null)
            {
                return;
            }

            int selected = dropdown.value;
            dropdown.ClearOptions();
            dropdown.AddOptions(
                localizationKeys
                    .Select(key => LM.Get(key))
                    .ToList());
            dropdown.SetValueWithoutNotify(
                Mathf.Clamp(selected, 0, dropdown.options.Count - 1));
            dropdown.RefreshShownValue();
        }

        private static void StyleDropdownPopup(GameObject dropdownObject)
        {
            Transform arrow = dropdownObject.transform.Find("Arrow");
            if (arrow != null)
            {
                Image arrowImage = arrow.GetComponent<Image>();
                if (arrowImage != null)
                {
                    arrowImage.enabled = false;
                }

                GameObject arrowGlyph = new GameObject(
                    "TerrainLabDropdownArrow",
                    typeof(RectTransform),
                    typeof(Text));
                arrowGlyph.transform.SetParent(arrow, false);
                RectTransform arrowRect =
                    arrowGlyph.GetComponent<RectTransform>();
                arrowRect.anchorMin = Vector2.zero;
                arrowRect.anchorMax = Vector2.one;
                arrowRect.offsetMin = Vector2.zero;
                arrowRect.offsetMax = Vector2.zero;
                Text arrowText = arrowGlyph.GetComponent<Text>();
                arrowText.font = LocalizedTextManager.current_font;
                arrowText.text = "v";
                arrowText.fontSize = 10;
                arrowText.fontStyle = FontStyle.Bold;
                arrowText.alignment = TextAnchor.MiddleCenter;
                arrowText.color =
                    new UnityColor(0.83f, 0.79f, 0.66f, 1f);
                arrowText.raycastTarget = false;
            }

            Transform template = dropdownObject.transform.Find("Template");
            if (template == null)
            {
                return;
            }

            Image templateBackground = template.GetComponent<Image>();
            if (templateBackground != null)
            {
                Sprite darkSprite = SpriteTextureLoader.getSprite(
                    "ui/special/darkInputFieldEmpty");
                if (darkSprite != null)
                {
                    templateBackground.sprite = darkSprite;
                    templateBackground.type = Image.Type.Sliced;
                }
                templateBackground.color =
                    new UnityColor(0.01f, 0.01f, 0.01f, 1f);
            }

            Toggle item = template.GetComponentInChildren<Toggle>(true);
            if (item != null)
            {
                ColorBlock colors = item.colors;
                colors.normalColor =
                    new UnityColor(0.015f, 0.015f, 0.015f, 1f);
                colors.highlightedColor =
                    new UnityColor(0.16f, 0.17f, 0.14f, 1f);
                colors.selectedColor =
                    new UnityColor(0.16f, 0.17f, 0.14f, 1f);
                colors.pressedColor =
                    new UnityColor(0.27f, 0.20f, 0.10f, 1f);
                colors.disabledColor =
                    new UnityColor(0.01f, 0.01f, 0.01f, 0.72f);
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0.04f;
                item.colors = colors;
                if (item.targetGraphic != null)
                {
                    item.targetGraphic.color = colors.normalColor;
                }
                if (item.graphic != null)
                {
                    item.graphic.enabled = false;
                    item.graphic = null;
                }

                Transform itemLabel = item.transform.Find("Item Label");
                if (itemLabel is RectTransform itemLabelRect)
                {
                    itemLabelRect.offsetMin = new Vector2(
                        8f,
                        itemLabelRect.offsetMin.y);
                }
            }

            Scrollbar scrollbar =
                template.GetComponentInChildren<Scrollbar>(true);
            if (scrollbar == null)
            {
                return;
            }

            Image scrollbarBackground = scrollbar.GetComponent<Image>();
            if (scrollbarBackground != null)
            {
                scrollbarBackground.color =
                    new UnityColor(0.035f, 0.035f, 0.03f, 1f);
            }
            if (scrollbar.targetGraphic != null)
            {
                scrollbar.targetGraphic.color =
                    new UnityColor(0.30f, 0.31f, 0.27f, 1f);
            }
        }

        private InputField CreateInputField(
            Transform parent,
            string value,
            string tooltipKey)
        {
            GameObject inputObject =
                DefaultControls.CreateInputField(_defaultResources);
            inputObject.name = "TerrainLabClassificationElevation";
            inputObject.transform.SetParent(parent, false);
            InputField input = inputObject.GetComponent<InputField>();
            input.text = value;
            input.contentType = InputField.ContentType.IntegerNumber;
            input.characterLimit = 6;
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

        private Toggle CreateToggle(
            Transform parent,
            string labelText,
            bool value,
            string tooltipKey)
        {
            GameObject toggleObject =
                DefaultControls.CreateToggle(_defaultResources);
            toggleObject.name = "TerrainLabClassificationToggle";
            toggleObject.transform.SetParent(parent, false);
            Toggle toggle = toggleObject.GetComponent<Toggle>();
            toggle.isOn = value;
            Text label = toggleObject.GetComponentInChildren<Text>();
            label.font = LocalizedTextManager.current_font;
            label.fontSize = 11;
            label.color = UnityColor.white;
            label.text = labelText;
            if (TerrainLocalizedUi.Matches(labelText, tooltipKey))
            {
                TerrainLocalizedUi.Bind(label, tooltipKey);
            }
            LayoutElement element =
                toggleObject.AddComponent<LayoutElement>();
            element.preferredHeight = 24f;
            ConfigureTooltip(toggleObject, tooltipKey);
            return toggle;
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

        private void SetPanelStatus(string message, bool error)
        {
            if (_panelStatus != null)
            {
                _panelStatus.text = message ?? string.Empty;
                _panelStatus.color = error
                    ? new UnityColor(0.95f, 0.52f, 0.46f, 1f)
                    : new UnityColor(0.83f, 0.79f, 0.66f, 1f);
            }
        }

        private void SetStatus(string message, bool error)
        {
            StatusChanged?.Invoke(message, error);
        }

        private void EnsureInitialized()
        {
            if (!_initialized || _workspace == null)
            {
                throw new InvalidOperationException(
                    "Manual image classification is not initialized.");
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
            if (_root != null)
            {
                Destroy(_root);
            }
        }
    }
}
