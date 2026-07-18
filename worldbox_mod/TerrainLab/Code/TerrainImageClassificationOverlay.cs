using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NeoModLoader.General;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingCompositingMode = System.Drawing.Drawing2D.CompositingMode;
using DrawingCompositingQuality = System.Drawing.Drawing2D.CompositingQuality;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImage = System.Drawing.Image;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;
using DrawingInterpolationMode = System.Drawing.Drawing2D.InterpolationMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingPixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode;
using DrawingRectangle = System.Drawing.Rectangle;
using UnityColor = UnityEngine.Color;

namespace TerrainLab
{
    internal enum TerrainImageClassificationDrawMode
    {
        Point,
        Polygon,
        MapBoundary
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
        private const int MaximumPreviewDimension = 2048;
        private const long MaximumPreviewFileBytes = 256L * 1024L * 1024L;
        private const float MinimumZoom = 1f;
        private const float MaximumZoom = 12f;

        private readonly List<GameObject> _markers = new List<GameObject>();
        private readonly List<TerrainImagePolygonGraphic> _regionGraphics =
            new List<TerrainImagePolygonGraphic>();
        private readonly List<TerrainImageClassificationVertex> _draftVertices =
            new List<TerrainImageClassificationVertex>();
        private readonly List<Selectable> _editorControls =
            new List<Selectable>();
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
        private Text _panelStatus;
        private Dropdown _surfaceDropdown;
        private Dropdown _biotopeDropdown;
        private InputField _elevationInput;
        private Toggle _globalDemToggle;
        private Button _previousImageButton;
        private Button _nextImageButton;
        private Button _openSelectedButton;
        private Button _pointModeButton;
        private Button _polygonModeButton;
        private Button _boundaryModeButton;
        private Button _finishPolygonButton;
        private Button _cancelPolygonButton;
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
            TerrainImageClassificationDrawMode.Point;
        private TerrainImageClassificationVertex _hoverVertex;
        private bool _hasHoverVertex;
        private bool _editorEnabled;
        private bool _initialized;

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
                if (_drawMode !=
                    TerrainImageClassificationDrawMode.Point)
                {
                    if (secondary)
                    {
                        FinishPolygon();
                        return;
                    }
                    AddPolygonVertex(sourceX, sourceY);
                    if (doubleClick && _draftVertices.Count >= 3)
                    {
                        FinishPolygon();
                    }
                    return;
                }

                if (secondary)
                {
                    if (!RemoveNearestAnnotation(sourceX, sourceY))
                    {
                        SetPanelStatus(
                            LM.Get("terrain_lab_manual_no_near_annotation"),
                            false);
                        return;
                    }
                }
                else if (!_profile.IsInsideMapBoundary(sourceX, sourceY))
                {
                    SetPanelStatus(
                        LM.Get("terrain_lab_manual_outside_boundary"),
                        true);
                    return;
                }
                else if (!TryReadSelection(
                             out TerrainImageClassOption surface,
                             out TerrainImageBiotopeOption biotope,
                             out short elevation))
                {
                    return;
                }
                else
                {
                    _profile.AddOrReplaceSample(
                        sourceX,
                        sourceY,
                        surface.Id,
                        biotope.Id,
                        elevation);
                }
                SaveProfile();
                RebuildAnnotations();
                UpdateLabels();
                SetPanelStatus(
                    secondary
                        ? LM.Get(
                            "terrain_lab_manual_annotation_removed")
                        : string.Format(
                            LM.Get(
                                "terrain_lab_manual_sample_saved_format"),
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
            if (_drawMode == TerrainImageClassificationDrawMode.Point ||
                _draftVertices.Count == 0)
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
                _hoverVertex = null;
                _hasHoverVertex = false;
                _globalDemToggle.SetIsOnWithoutNotify(
                    _profile.Settings.InterpolateElevationGlobally);
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
                SetDrawMode(TerrainImageClassificationDrawMode.Point);
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
            RebuildAnnotations();
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
                LM.Get("terrain_lab_manual_heading"),
                18,
                FontStyle.Bold,
                30f,
                UnityColor.white);
            CreateLabel(
                content,
                LM.Get("terrain_lab_manual_image_choice"),
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

            Transform drawModes = CreateRow(content, 30f);
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
            _boundaryModeButton = TrackEditorControl(
                CreateButton(
                    drawModes,
                    LM.Get("terrain_lab_manual_mode_boundary"),
                    delegate
                    {
                        SetDrawMode(
                            TerrainImageClassificationDrawMode.MapBoundary);
                    },
                    78f,
                    "terrain_lab_manual_mode_boundary"));

            CreateLabel(
                content,
                LM.Get("terrain_lab_manual_surface"),
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            _surfaceDropdown = TrackEditorControl(
                CreateDropdown(
                    content,
                    TerrainImageClassificationCatalog.Surfaces
                        .Select(option => LM.Get(option.LocalizationKey)),
                    "terrain_lab_manual_surface"));
            _surfaceDropdown.onValueChanged.AddListener(
                HandleSurfaceChanged);

            CreateLabel(
                content,
                LM.Get("terrain_lab_manual_biotope"),
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            _biotopeDropdown = TrackEditorControl(
                CreateDropdown(
                    content,
                    TerrainImageClassificationCatalog.Biotopes
                        .Select(option => LM.Get(option.LocalizationKey)),
                    "terrain_lab_manual_biotope"));

            CreateLabel(
                content,
                LM.Get("terrain_lab_manual_elevation"),
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            _elevationInput = TrackEditorControl(
                CreateInputField(
                    content,
                    "150",
                    "terrain_lab_manual_elevation"));
            _surfaceDropdown.SetValueWithoutNotify(5);
            _surfaceDropdown.RefreshShownValue();

            _globalDemToggle = TrackEditorControl(
                CreateToggle(
                    content,
                    LM.Get("terrain_lab_manual_global_dem"),
                    true,
                    "terrain_lab_manual_global_dem"));
            _globalDemToggle.onValueChanged.AddListener(
                delegate(bool enabled)
                {
                    if (_profile != null)
                    {
                        _profile.Settings.InterpolateElevationGlobally =
                            enabled;
                    }
                });

            _sampleLabel = CreateLabel(
                content,
                string.Empty,
                11,
                FontStyle.Normal,
                36f,
                UnityColor.white);
            _coordinateLabel = CreateLabel(
                content,
                "X -  Y -",
                11,
                FontStyle.Normal,
                20f,
                new UnityColor(0.72f, 0.84f, 0.94f, 1f));
            _panelStatus = CreateLabel(
                content,
                string.Empty,
                10,
                FontStyle.Normal,
                38f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));

            Transform polygonActions = CreateRow(content, 30f);
            _finishPolygonButton = TrackEditorControl(
                CreateButton(
                    polygonActions,
                    LM.Get("terrain_lab_manual_finish_polygon"),
                    FinishPolygon,
                    118f,
                    "terrain_lab_manual_finish_polygon"));
            _cancelPolygonButton = TrackEditorControl(
                CreateButton(
                    polygonActions,
                    LM.Get("terrain_lab_manual_cancel_polygon"),
                    CancelPolygonOrBoundary,
                    118f,
                    "terrain_lab_manual_cancel_polygon"));

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
            if (_elevationInput == null ||
                index < 0 ||
                index >= TerrainImageClassificationCatalog.Surfaces.Count)
            {
                return;
            }

            TerrainImageClassOption option =
                TerrainImageClassificationCatalog.Surfaces[index];
            _elevationInput.SetTextWithoutNotify(
                option.DefaultElevation.ToString(
                    CultureInfo.InvariantCulture));
            RefreshDraftGraphic();
        }

        private void SetDrawMode(TerrainImageClassificationDrawMode mode)
        {
            if (!_editorEnabled)
            {
                return;
            }
            if (_drawMode == mode)
            {
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
            _drawMode = mode;
            UpdateModeButtons();
            string statusKey;
            switch (mode)
            {
                case TerrainImageClassificationDrawMode.Polygon:
                    statusKey =
                        "terrain_lab_manual_mode_polygon_active";
                    break;
                case TerrainImageClassificationDrawMode.MapBoundary:
                    statusKey =
                        "terrain_lab_manual_mode_boundary_active";
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
            SetModeButtonState(
                _pointModeButton,
                _editorEnabled &&
                _drawMode == TerrainImageClassificationDrawMode.Point);
            SetModeButtonState(
                _polygonModeButton,
                _editorEnabled &&
                _drawMode == TerrainImageClassificationDrawMode.Polygon);
            SetModeButtonState(
                _boundaryModeButton,
                _editorEnabled &&
                _drawMode ==
                TerrainImageClassificationDrawMode.MapBoundary);
            if (_pointModeButton != null)
            {
                _pointModeButton.interactable = _editorEnabled;
            }
            if (_polygonModeButton != null)
            {
                _polygonModeButton.interactable = _editorEnabled;
            }
            if (_boundaryModeButton != null)
            {
                _boundaryModeButton.interactable = _editorEnabled;
            }

            bool boundaryMode =
                _drawMode ==
                TerrainImageClassificationDrawMode.MapBoundary;
            bool classificationControls =
                _editorEnabled && !boundaryMode;
            if (_surfaceDropdown != null)
            {
                _surfaceDropdown.interactable = classificationControls;
            }
            if (_biotopeDropdown != null)
            {
                _biotopeDropdown.interactable = classificationControls;
            }
            if (_elevationInput != null)
            {
                _elevationInput.interactable = classificationControls;
            }

            bool polygonMode =
                _editorEnabled &&
                _drawMode != TerrainImageClassificationDrawMode.Point;
            if (_finishPolygonButton != null)
            {
                _finishPolygonButton.interactable =
                    polygonMode && _draftVertices.Count >= 3;
                string finishKey = boundaryMode
                    ? "terrain_lab_manual_finish_boundary"
                    : "terrain_lab_manual_finish_polygon";
                SetButtonText(
                    _finishPolygonButton,
                    LM.Get(finishKey));
                ConfigureTooltip(
                    _finishPolygonButton.gameObject,
                    finishKey);
            }
            if (_cancelPolygonButton != null)
            {
                _cancelPolygonButton.interactable =
                    polygonMode &&
                    (_draftVertices.Count > 0 ||
                     boundaryMode && _profile?.MapBoundary != null);
                string cancelKey = boundaryMode
                    ? (_draftVertices.Count > 0
                        ? "terrain_lab_manual_cancel_boundary"
                        : "terrain_lab_manual_remove_boundary")
                    : "terrain_lab_manual_cancel_polygon";
                SetButtonText(
                    _cancelPolygonButton,
                    LM.Get(cancelKey));
                ConfigureTooltip(
                    _cancelPolygonButton.gameObject,
                    cancelKey);
            }
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
            if (_draftVertices.Count >=
                TerrainImageClassificationProfile.MaximumRegionVertices)
            {
                SetPanelStatus(
                    LM.Get(
                        _drawMode ==
                        TerrainImageClassificationDrawMode.MapBoundary
                            ? "terrain_lab_manual_boundary_vertex_limit"
                            : "terrain_lab_manual_polygon_vertex_limit"),
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
            SetPanelStatus(
                string.Format(
                    LM.Get(
                        _drawMode ==
                        TerrainImageClassificationDrawMode.MapBoundary
                            ? "terrain_lab_manual_boundary_vertex_format"
                            : "terrain_lab_manual_polygon_vertex_format"),
                    _draftVertices.Count),
                false);
        }

        private void FinishPolygon()
        {
            if (_drawMode == TerrainImageClassificationDrawMode.Point)
            {
                return;
            }
            if (_draftVertices.Count < 3)
            {
                SetPanelStatus(
                    LM.Get(
                        _drawMode ==
                        TerrainImageClassificationDrawMode.MapBoundary
                            ? "terrain_lab_manual_boundary_need_vertices"
                            : "terrain_lab_manual_polygon_need_vertices"),
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
                }
                else
                {
                    if (!TryReadSelection(
                            out TerrainImageClassOption surface,
                            out TerrainImageBiotopeOption biotope,
                            out short elevation))
                    {
                        return;
                    }
                    _profile.AddRegion(
                        _draftVertices,
                        surface.Id,
                        biotope.Id,
                        elevation);
                }
                _draftVertices.Clear();
                _hoverVertex = null;
                _hasHoverVertex = false;
                SaveProfile();
                RebuildAnnotations();
                UpdateLabels();
                UpdateModeButtons();
                SetPanelStatus(
                    LM.Get(
                        boundaryMode
                            ? "terrain_lab_manual_boundary_saved"
                            : "terrain_lab_manual_polygon_saved"),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
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
            RefreshDraftGraphic();
            UpdateLabels();
            UpdateModeButtons();
            if (notify)
            {
                SetPanelStatus(
                    LM.Get(
                        _drawMode ==
                        TerrainImageClassificationDrawMode.MapBoundary
                            ? "terrain_lab_manual_boundary_cancelled"
                            : "terrain_lab_manual_polygon_cancelled"),
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
                SaveProfile();
                RebuildAnnotations();
                UpdateLabels();
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
                if (!TryReadElevation(out short _))
                {
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

        private void ClearSamples()
        {
            if (_profile == null ||
                ((_profile.Samples?.Count ?? 0) == 0 &&
                 (_profile.Regions?.Count ?? 0) == 0 &&
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
                _profile.ClearMapBoundary();
                _draftVertices.Clear();
                SaveProfile();
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

            _profile.Settings.InterpolateElevationGlobally =
                _globalDemToggle.isOn;
            _profile.Save(_imagePath);
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
                _biotopeDropdown == null ||
                _surfaceDropdown.value < 0 ||
                _surfaceDropdown.value >=
                TerrainImageClassificationCatalog.Surfaces.Count ||
                _biotopeDropdown.value < 0 ||
                _biotopeDropdown.value >=
                TerrainImageClassificationCatalog.Biotopes.Count ||
                !TryReadElevation(out elevation))
            {
                return false;
            }
            surface = TerrainImageClassificationCatalog.Surfaces[
                _surfaceDropdown.value];
            biotope = TerrainImageClassificationCatalog.Biotopes[
                _biotopeDropdown.value];
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
                        true,
                        false,
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
                    true,
                    false,
                    "TerrainLabClassificationRegion");
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
                image.color = GetSurfaceColor(sample.Surface);
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
            bool closed,
            bool showVertices,
            string objectName,
            float closedFillAlpha = 0.22f)
        {
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
                vertices,
                _sourceWidth,
                _sourceHeight,
                GetSurfaceColor(surface),
                closed,
                showVertices,
                1f / Mathf.Max(_zoom, 0.01f),
                closedFillAlpha);
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
            if (_draftVertices.Count == 0 ||
                !boundaryMode &&
                (_surfaceDropdown == null ||
                 _surfaceDropdown.value < 0 ||
                 _surfaceDropdown.value >=
                 TerrainImageClassificationCatalog.Surfaces.Count))
            {
                if (_draftGraphic != null)
                {
                    Destroy(_draftGraphic.gameObject);
                    _draftGraphic = null;
                }
                return;
            }

            string surface = boundaryMode
                ? "map_boundary"
                : TerrainImageClassificationCatalog.Surfaces[
                    _surfaceDropdown.value].Id;
            List<TerrainImageClassificationVertex> displayVertices =
                new List<TerrainImageClassificationVertex>(_draftVertices);
            TerrainImageClassificationVertex last =
                displayVertices[displayVertices.Count - 1];
            if (_hasHoverVertex &&
                _hoverVertex != null &&
                (last.X != _hoverVertex.X || last.Y != _hoverVertex.Y))
            {
                displayVertices.Add(_hoverVertex);
            }
            if (_draftGraphic == null)
            {
                _draftGraphic = CreatePolygonGraphic(
                    displayVertices,
                    surface,
                    false,
                    true,
                    "TerrainLabClassificationDraftRegion");
            }
            else
            {
                _draftGraphic.Configure(
                    displayVertices,
                    _sourceWidth,
                    _sourceHeight,
                    GetSurfaceColor(surface),
                    false,
                    true,
                    1f / Mathf.Max(_zoom, 0.01f));
            }
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
            FileInfo info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException(
                    "Source image was not found.",
                    path);
            }
            if (info.Length > MaximumPreviewFileBytes)
            {
                throw new InvalidDataException(
                    "Manual-classification preview is limited to 256 MiB per file.");
            }

            byte[] png;
            using (DrawingImage source = DrawingImage.FromFile(path))
            {
                sourceWidth = source.Width;
                sourceHeight = source.Height;
                float previewScale = Math.Min(
                    1f,
                    MaximumPreviewDimension /
                    (float)Math.Max(sourceWidth, sourceHeight));
                int previewWidth = Math.Max(
                    1,
                    (int)Math.Round(sourceWidth * previewScale));
                int previewHeight = Math.Max(
                    1,
                    (int)Math.Round(sourceHeight * previewScale));
                using (DrawingBitmap bitmap = new DrawingBitmap(
                    previewWidth,
                    previewHeight,
                    DrawingPixelFormat.Format32bppArgb))
                using (DrawingGraphics graphics =
                       DrawingGraphics.FromImage(bitmap))
                using (MemoryStream stream = new MemoryStream())
                {
                    graphics.CompositingMode =
                        DrawingCompositingMode.SourceCopy;
                    graphics.CompositingQuality =
                        DrawingCompositingQuality.HighQuality;
                    graphics.InterpolationMode =
                        DrawingInterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode =
                        DrawingPixelOffsetMode.HighQuality;
                    graphics.DrawImage(
                        source,
                        new DrawingRectangle(
                            0,
                            0,
                            previewWidth,
                            previewHeight));
                    bitmap.Save(stream, DrawingImageFormat.Png);
                    png = stream.ToArray();
                }
            }

            Texture2D texture = new Texture2D(
                2,
                2,
                TextureFormat.RGBA32,
                false);
            texture.name = "TerrainLabManualClassificationPreview";
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            if (!texture.LoadImage(png, true))
            {
                Destroy(texture);
                throw new InvalidDataException(
                    "Unity could not decode the classification preview.");
            }

            return texture;
        }

        private static UnityColor GetSurfaceColor(string surface)
        {
            switch (surface)
            {
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
            element.preferredWidth = 174f;
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
            element.preferredHeight = 28f;
            ConfigureTooltip(dropdownObject, tooltipKey);
            return dropdown;
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
