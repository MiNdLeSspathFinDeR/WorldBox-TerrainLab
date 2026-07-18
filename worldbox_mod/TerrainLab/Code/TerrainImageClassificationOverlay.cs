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
    internal sealed class TerrainImageCanvasInput : MonoBehaviour,
        IPointerClickHandler,
        IScrollHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
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
                _owner.HandleImageClick(eventData.position, false);
            }
            else if (eventData.button == PointerEventData.InputButton.Right)
            {
                _owner.HandleImageClick(eventData.position, true);
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
    }

    public sealed class TerrainImageClassificationOverlay : MonoBehaviour
    {
        private const int MaximumPreviewDimension = 2048;
        private const long MaximumPreviewFileBytes = 256L * 1024L * 1024L;
        private const float MinimumZoom = 1f;
        private const float MaximumZoom = 12f;

        private readonly List<GameObject> _markers = new List<GameObject>();
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
        private Texture2D _texture;
        private TerrainImageClassificationProfile _profile;
        private List<string> _images = new List<string>();
        private string _imagePath;
        private int _imageIndex;
        private int _sourceWidth;
        private int _sourceHeight;
        private float _zoom = 1f;
        private Vector2 _lastViewportSize;
        private float _clearArmedUntil;
        private bool _initialized;

        public event Action<string, bool> StatusChanged;

        public event Action VisibilityChanged;

        public bool IsVisible =>
            _root != null && _root.activeSelf;

        public string CurrentImageName =>
            string.IsNullOrWhiteSpace(_imagePath)
                ? null
                : Path.GetFileName(_imagePath);

        public int SampleCount => _profile?.Samples?.Count ?? 0;

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

            ShowLatest();
        }

        public void ShowLatest()
        {
            EnsureInitialized();
            try
            {
                _images = _workspace.GetRecentImages(64).ToList();
                if (_images.Count == 0)
                {
                    SetStatus(
                        LM.Get("terrain_lab_manual_no_images"),
                        true);
                    return;
                }

                string previous = _imagePath;
                int selected = previous == null
                    ? 0
                    : _images.FindIndex(path => string.Equals(
                        path,
                        previous,
                        StringComparison.OrdinalIgnoreCase));
                OpenImage(selected < 0 ? 0 : selected);
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
                _root.SetActive(false);
                if (_image != null)
                {
                    _image.texture = null;
                }
                ReleaseTexture();
                VisibilityChanged?.Invoke();
            }
        }

        public void HandleImageClick(Vector2 screenPosition, bool remove)
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
                if (remove)
                {
                    if (!RemoveNearestSample(sourceX, sourceY))
                    {
                        SetPanelStatus(
                            LM.Get("terrain_lab_manual_no_near_sample"),
                            false);
                        return;
                    }
                }
                else
                {
                    if (!TryReadElevation(out short elevation))
                    {
                        return;
                    }

                    TerrainImageClassOption surface =
                        TerrainImageClassificationCatalog.Surfaces[
                            _surfaceDropdown.value];
                    TerrainImageBiotopeOption biotope =
                        TerrainImageClassificationCatalog.Biotopes[
                            _biotopeDropdown.value];
                    _profile.AddOrReplaceSample(
                        sourceX,
                        sourceY,
                        surface.Id,
                        biotope.Id,
                        elevation);
                }

                SaveProfile();
                RebuildMarkers();
                UpdateLabels();
                SetPanelStatus(
                    string.Format(
                        LM.Get("terrain_lab_manual_sample_saved_format"),
                        sourceX,
                        sourceY),
                    false);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
            }
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
                _sourceWidth = width;
                _sourceHeight = height;
                _profile = nextProfile;
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
                RebuildMarkers();
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

        private void CycleImage(int direction)
        {
            if (_images.Count == 0)
            {
                return;
            }

            int next = (_imageIndex + direction) % _images.Count;
            if (next < 0)
            {
                next += _images.Count;
            }

            try
            {
                OpenImage(next);
            }
            catch (Exception exception)
            {
                SetPanelStatus(exception.Message, true);
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
                typeof(VerticalLayoutGroup));
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

            VerticalLayoutGroup layout =
                panel.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(9, 9, 8, 8);
            layout.spacing = 5f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            CreateLabel(
                panel.transform,
                LM.Get("terrain_lab_manual_heading"),
                18,
                FontStyle.Bold,
                30f,
                UnityColor.white);
            _fileLabel = CreateLabel(
                panel.transform,
                string.Empty,
                11,
                FontStyle.Bold,
                34f,
                new UnityColor(0.95f, 0.76f, 0.28f, 1f));
            Transform navigation = CreateRow(panel.transform, 30f);
            CreateButton(
                navigation,
                "<",
                delegate { CycleImage(-1); },
                48f,
                "terrain_lab_manual_previous_image");
            CreateButton(
                navigation,
                LM.Get("terrain_lab_manual_fit"),
                delegate
                {
                    _zoom = 1f;
                    _imageRect.localScale = Vector3.one;
                    _imageRect.anchoredPosition = Vector2.zero;
                    FitImageToViewport(true);
                },
                144f,
                "terrain_lab_manual_fit");
            CreateButton(
                navigation,
                ">",
                delegate { CycleImage(1); },
                48f,
                "terrain_lab_manual_next_image");

            CreateLabel(
                panel.transform,
                LM.Get("terrain_lab_manual_surface"),
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            _surfaceDropdown = CreateDropdown(
                panel.transform,
                TerrainImageClassificationCatalog.Surfaces
                    .Select(option => LM.Get(option.LocalizationKey)),
                "terrain_lab_manual_surface");
            _surfaceDropdown.onValueChanged.AddListener(
                HandleSurfaceChanged);

            CreateLabel(
                panel.transform,
                LM.Get("terrain_lab_manual_biotope"),
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            _biotopeDropdown = CreateDropdown(
                panel.transform,
                TerrainImageClassificationCatalog.Biotopes
                    .Select(option => LM.Get(option.LocalizationKey)),
                "terrain_lab_manual_biotope");

            CreateLabel(
                panel.transform,
                LM.Get("terrain_lab_manual_elevation"),
                11,
                FontStyle.Bold,
                18f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));
            _elevationInput = CreateInputField(
                panel.transform,
                "150",
                "terrain_lab_manual_elevation");
            _surfaceDropdown.SetValueWithoutNotify(5);
            _surfaceDropdown.RefreshShownValue();

            _globalDemToggle = CreateToggle(
                panel.transform,
                LM.Get("terrain_lab_manual_global_dem"),
                true,
                "terrain_lab_manual_global_dem");
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
                panel.transform,
                string.Empty,
                11,
                FontStyle.Normal,
                24f,
                UnityColor.white);
            _coordinateLabel = CreateLabel(
                panel.transform,
                "X -  Y -",
                11,
                FontStyle.Normal,
                20f,
                new UnityColor(0.72f, 0.84f, 0.94f, 1f));
            _panelStatus = CreateLabel(
                panel.transform,
                string.Empty,
                10,
                FontStyle.Normal,
                38f,
                new UnityColor(0.83f, 0.79f, 0.66f, 1f));

            Transform profileActions = CreateRow(panel.transform, 30f);
            CreateButton(
                profileActions,
                LM.Get("terrain_lab_manual_undo"),
                UndoSample,
                118f,
                "terrain_lab_manual_undo");
            CreateButton(
                profileActions,
                LM.Get("terrain_lab_manual_clear"),
                ClearSamples,
                118f,
                "terrain_lab_manual_clear");

            Transform saveActions = CreateRow(panel.transform, 34f);
            CreateButton(
                saveActions,
                LM.Get("terrain_lab_manual_save_profile"),
                SaveProfileCommand,
                118f,
                "terrain_lab_manual_save_profile");
            CreateButton(
                saveActions,
                LM.Get("terrain_lab_manual_build_map"),
                QueueConversion,
                118f,
                "terrain_lab_manual_build_map",
                true);

            CreateButton(
                panel.transform,
                LM.Get("terrain_lab_manual_close"),
                Hide,
                252f,
                "terrain_lab_manual_close");
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
        }

        private void SaveProfileCommand()
        {
            try
            {
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
                if (_profile == null || _profile.Samples.Count < 2)
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
                if (_profile == null || !_profile.RemoveLastSample())
                {
                    SetPanelStatus(
                        LM.Get("terrain_lab_manual_nothing_to_undo"),
                        false);
                    return;
                }

                SaveProfile();
                RebuildMarkers();
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
            if (_profile == null || _profile.Samples.Count == 0)
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
                SaveProfile();
                RebuildMarkers();
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

        private bool RemoveNearestSample(int sourceX, int sourceY)
        {
            if (_profile?.Samples == null || _profile.Samples.Count == 0)
            {
                return false;
            }

            double maximumDistance = Math.Max(
                3.0,
                Math.Max(_sourceWidth, _sourceHeight) * 0.012 / _zoom);
            double maximumSquared = maximumDistance * maximumDistance;
            int nearest = -1;
            double nearestSquared = double.MaxValue;
            for (int index = 0; index < _profile.Samples.Count; index++)
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

            if (nearest < 0)
            {
                return false;
            }

            _profile.Samples.RemoveAt(nearest);
            return true;
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

        private void RebuildMarkers()
        {
            foreach (GameObject marker in _markers)
            {
                Destroy(marker);
            }
            _markers.Clear();

            if (_profile?.Samples == null)
            {
                return;
            }

            foreach (TerrainImageClassificationSample sample in
                     _profile.Samples)
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
                TerrainImageClassificationProfile.MaximumSamples);
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
            }
            Image image = dropdownObject.GetComponent<Image>();
            image.sprite = SpriteTextureLoader.getSprite(
                "ui/special/darkInputFieldEmpty");
            image.type = Image.Type.Sliced;
            image.color = new UnityColor(0.08f, 0.08f, 0.07f, 1f);
            LayoutElement element =
                dropdownObject.AddComponent<LayoutElement>();
            element.preferredHeight = 28f;
            ConfigureTooltip(dropdownObject, tooltipKey);
            return dropdown;
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
