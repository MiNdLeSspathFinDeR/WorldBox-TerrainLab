using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerrainLab
{
    public enum TerrainEditorTool
    {
        None = -1,
        Inspect,
        SetElevation,
        RaiseElevation,
        LowerElevation,
        SmoothElevation,
        RampElevation,
        SampleSurface,
        FloodFillSurface,
        DrawLineSurface,
        DrawPolygonSurface,
        DrawRectangleSurface,
        PolygonizeSurface
    }

    public sealed class TerrainLabEditor : MonoBehaviour
    {
        private const int MaximumHistoryEntries = 32;
        private const long MaximumHistoryBytes = 64L * 1024L * 1024L;
        private const int OutlineSegments = 48;

        private readonly List<ITerrainLabEdit> _undoHistory =
            new List<ITerrainLabEdit>();
        private readonly List<ITerrainLabEdit> _redoHistory =
            new List<ITerrainLabEdit>();
        private readonly List<TerrainGridPoint> _digitizingVertices =
            new List<TerrainGridPoint>();

        private TerrainLabRuntime _runtime;
        private TerrainWorldState _historyState;
        private LineRenderer _brushOutline;
        private LineRenderer _digitizingOutline;
        private MeshFilter _selectionMeshFilter;
        private MeshRenderer _selectionMeshRenderer;
        private Material _outlineMaterial;
        private Mesh _selectionMesh;
        private TerrainSurfaceStamp? _surfaceSample;
        private int[] _polygonizedSelection = Array.Empty<int>();
        private long _undoHistoryBytes;
        private long _redoHistoryBytes;
        private bool _interfaceVisible;
        private bool _interactionEnabled;
        private bool _initialized;

        public event Action<int> EditApplied;

        public event Action<TerrainElevationEdit> ElevationChanged;

        public event Action<TerrainSurfaceStamp> SurfaceSampled;

        public event Action<string, string> OperationFailed;

        public event Action<int> RegionPolygonized;

        public event Action<short> RampStarted;

        public TerrainEditorTool Tool { get; private set; } = TerrainEditorTool.None;

        public int BrushRadius { get; private set; } = 2;

        public short TargetElevation { get; private set; } = 100;

        public short Step { get; private set; } = 10;

        public WorldTile HoveredTile { get; private set; }

        public bool CanUndo => _undoHistory.Count > 0;

        public bool CanRedo => _redoHistory.Count > 0;

        public bool HasSurfaceSample => _surfaceSample.HasValue;

        public bool HasPolygonizedSelection => _polygonizedSelection.Length > 0;

        public string SurfaceSampleName =>
            _surfaceSample.HasValue ? _surfaceSample.Value.DisplayName : string.Empty;

        public void Initialize(TerrainLabRuntime runtime)
        {
            if (_initialized)
            {
                return;
            }

            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            CreateOutlines();
            SynchronizeProjectState();
            _initialized = true;
        }

        public void SetInterfaceState(bool visible, bool interactionEnabled)
        {
            bool wasVisible = _interfaceVisible;
            bool wasInteractive = _interactionEnabled;
            _interfaceVisible = visible;
            _interactionEnabled = visible && interactionEnabled;
            if (!visible)
            {
                HoveredTile = null;
                SetOutlineVisible(false);
                SetDigitizingVisible(false);
            }
            else if ((wasVisible != visible || wasInteractive != _interactionEnabled) &&
                     !_interactionEnabled)
            {
                SetOutlineVisible(false);
                SetDigitizingVisible(false);
            }

            if (_selectionMeshRenderer != null)
            {
                _selectionMeshRenderer.enabled = visible && HasPolygonizedSelection;
            }
        }

        public void SetTool(TerrainEditorTool tool)
        {
            if (Tool == tool)
            {
                return;
            }

            Tool = tool;
            _digitizingVertices.Clear();
            SetDigitizingVisible(false);
            UpdateOutlineColor();
        }

        public void SetBrushRadius(int radius)
        {
            BrushRadius = Mathf.Clamp(radius, 0, 16);
        }

        public bool TrySetTargetElevation(string value, out string error)
        {
            error = null;
            if (!int.TryParse(value, out int parsed) ||
                !TerrainElevationEncoding.IsDataValue(parsed))
            {
                error = "Elevation must be between -20000 and 9000 metres.";
                return false;
            }

            TargetElevation = (short)parsed;
            return true;
        }

        public bool TrySetStep(string value, out string error)
        {
            error = null;
            if (!short.TryParse(value, out short parsed) || parsed <= 0)
            {
                error = "Elevation step must be between 1 and 32767.";
                return false;
            }

            Step = parsed;
            return true;
        }

        public bool Undo(out int changedCells)
        {
            SynchronizeProjectState();
            changedCells = 0;
            if (_historyState == null || _undoHistory.Count == 0)
            {
                return false;
            }

            int lastIndex = _undoHistory.Count - 1;
            ITerrainLabEdit edit = _undoHistory[lastIndex];
            _undoHistory.RemoveAt(lastIndex);
            _undoHistoryBytes -= edit.EstimatedBytes;
            edit.Apply(_historyState, false);
            _redoHistory.Add(edit);
            _redoHistoryBytes += edit.EstimatedBytes;
            changedCells = edit.ChangedCellCount;
            ClearPolygonizedSelection();
            if (edit is TerrainElevationEdit elevationEdit)
            {
                ElevationChanged?.Invoke(elevationEdit);
            }

            EditApplied?.Invoke(changedCells);

            return true;
        }

        public bool Redo(out int changedCells)
        {
            SynchronizeProjectState();
            changedCells = 0;
            if (_historyState == null || _redoHistory.Count == 0)
            {
                return false;
            }

            int lastIndex = _redoHistory.Count - 1;
            ITerrainLabEdit edit = _redoHistory[lastIndex];
            _redoHistory.RemoveAt(lastIndex);
            _redoHistoryBytes -= edit.EstimatedBytes;
            edit.Apply(_historyState, true);
            AddUndoEntry(edit);
            changedCells = edit.ChangedCellCount;
            ClearPolygonizedSelection();
            if (edit is TerrainElevationEdit elevationEdit)
            {
                ElevationChanged?.Invoke(elevationEdit);
            }

            EditApplied?.Invoke(changedCells);

            return true;
        }

        public void RecordAppliedEdit(ITerrainLabEdit edit)
        {
            SynchronizeProjectState();
            if (edit == null || edit.ChangedCellCount == 0)
            {
                return;
            }

            AddUndoEntry(edit);
            ClearRedoHistory();
            ClearPolygonizedSelection();
            if (edit is TerrainElevationEdit elevationEdit)
            {
                ElevationChanged?.Invoke(elevationEdit);
            }

            EditApplied?.Invoke(edit.ChangedCellCount);
        }

        public bool ApplyPolygonizedSelection()
        {
            SynchronizeProjectState();
            if (_historyState == null || _polygonizedSelection.Length == 0)
            {
                RaiseFailure("terrain_lab_error_surface_selection_required", null);
                return false;
            }

            if (!TryGetSurfaceSample(out TerrainSurfaceStamp target))
            {
                return false;
            }

            try
            {
                TerrainSurfaceEdit edit = _historyState.ApplySurfaceCells(
                    _polygonizedSelection,
                    target);
                RecordAppliedEdit(edit);
                if (edit.ChangedCellCount == 0)
                {
                    EditApplied?.Invoke(0);
                }

                return edit.ChangedCellCount > 0;
            }
            catch (Exception exception)
            {
                RaiseFailure("terrain_lab_error_surface_operation", exception.Message);
                return false;
            }
        }

        public void CancelDigitizing()
        {
            if (_digitizingVertices.Count > 0)
            {
                _digitizingVertices.Clear();
                SetDigitizingVisible(false);
                return;
            }

            ClearPolygonizedSelection();
        }

        private void Update()
        {
            if (!_initialized)
            {
                return;
            }

            SynchronizeProjectState();
            if (!_interfaceVisible || _historyState == null ||
                MapBox.instance == null || World.world == null)
            {
                HoveredTile = null;
                SetOutlineVisible(false);
                SetDigitizingVisible(false);
                return;
            }

            HandleDigitizingKeyboard();
            if (MapBox.instance.isOverUI())
            {
                HoveredTile = null;
                SetOutlineVisible(false);
                SetDigitizingVisible(false);
                return;
            }

            HoveredTile = MapBox.instance.getMouseTilePos();
            if (HoveredTile == null)
            {
                SetOutlineVisible(false);
                SetDigitizingVisible(false);
                return;
            }

            UpdateBrushOutline(HoveredTile);
            UpdateDigitizingPreview(HoveredTile);
            if (!_interactionEnabled)
            {
                return;
            }

            if (Input.GetMouseButtonDown(1) &&
                Tool == TerrainEditorTool.RampElevation &&
                _digitizingVertices.Count > 0)
            {
                CancelDigitizing();
                return;
            }

            if (Input.GetMouseButtonDown(1) &&
                (Tool == TerrainEditorTool.DrawLineSurface ||
                 Tool == TerrainEditorTool.DrawPolygonSurface) &&
                _digitizingVertices.Count > 0)
            {
                FinishDigitization();
                return;
            }

            if (Input.GetMouseButtonDown(0) &&
                Tool != TerrainEditorTool.None &&
                Tool != TerrainEditorTool.Inspect)
            {
                ApplyCurrentTool(HoveredTile);
            }
        }

        private void ApplyCurrentTool(WorldTile centerTile)
        {
            switch (Tool)
            {
                case TerrainEditorTool.SetElevation:
                    ApplyElevationTool(centerTile, TerrainElevationOperation.Set);
                    break;
                case TerrainEditorTool.RaiseElevation:
                    ApplyElevationTool(centerTile, TerrainElevationOperation.Raise);
                    break;
                case TerrainEditorTool.LowerElevation:
                    ApplyElevationTool(centerTile, TerrainElevationOperation.Lower);
                    break;
                case TerrainEditorTool.SmoothElevation:
                    ApplyElevationTool(centerTile, TerrainElevationOperation.Smooth);
                    break;
                case TerrainEditorTool.RampElevation:
                    AddElevationRampPoint(centerTile);
                    break;
                case TerrainEditorTool.SampleSurface:
                    SampleSurface(centerTile);
                    break;
                case TerrainEditorTool.FloodFillSurface:
                    FloodFillSurface(centerTile);
                    break;
                case TerrainEditorTool.DrawLineSurface:
                case TerrainEditorTool.DrawPolygonSurface:
                    AddDigitizingVertex(centerTile);
                    break;
                case TerrainEditorTool.DrawRectangleSurface:
                    AddRectangleCorner(centerTile);
                    break;
                case TerrainEditorTool.PolygonizeSurface:
                    PolygonizeSurface(centerTile);
                    break;
            }
        }

        private void ApplyElevationTool(
            WorldTile centerTile,
            TerrainElevationOperation operation)
        {
            TerrainElevationEdit edit = _historyState.ApplyElevationBrush(
                centerTile.x,
                centerTile.y,
                BrushRadius,
                operation,
                TargetElevation,
                Step);
            if (edit.ChangedCellCount > 0)
            {
                RecordAppliedEdit(edit);
            }
            else
            {
                EditApplied?.Invoke(0);
            }
        }

        private void SampleSurface(WorldTile tile)
        {
            if (!TerrainSurfaceStamp.TryCaptureSafe(
                    tile,
                    out TerrainSurfaceStamp stamp,
                    out string error))
            {
                RaiseFailure("terrain_lab_error_surface_unsafe", error);
                return;
            }

            _surfaceSample = stamp;
            if (_historyState.TryGetElevation(tile.x, tile.y, out short elevation) &&
                elevation != TerrainElevationEncoding.NoData)
            {
                TargetElevation = elevation;
            }

            SurfaceSampled?.Invoke(stamp);
        }

        private void AddElevationRampPoint(WorldTile tile)
        {
            TerrainGridPoint point = new TerrainGridPoint(tile.x, tile.y);
            if (_digitizingVertices.Count == 0)
            {
                if (!_historyState.TryGetElevation(
                        tile.x,
                        tile.y,
                        out short startElevation) ||
                    startElevation == TerrainElevationEncoding.NoData)
                {
                    RaiseFailure("terrain_lab_error_ramp_nodata", null);
                    return;
                }

                _digitizingVertices.Add(point);
                RampStarted?.Invoke(startElevation);
                return;
            }

            TerrainGridPoint start = _digitizingVertices[0];
            _digitizingVertices.Clear();
            SetDigitizingVisible(false);
            try
            {
                TerrainElevationEdit edit = _historyState.ApplyElevationRamp(
                    start.X,
                    start.Y,
                    point.X,
                    point.Y,
                    BrushRadius,
                    TargetElevation);
                if (edit.ChangedCellCount > 0)
                {
                    RecordAppliedEdit(edit);
                }
                else
                {
                    EditApplied?.Invoke(0);
                }
            }
            catch (Exception exception)
            {
                RaiseFailure("terrain_lab_error_ramp_operation", exception.Message);
            }
        }

        private void FloodFillSurface(WorldTile tile)
        {
            if (!TryGetSurfaceSample(out TerrainSurfaceStamp target))
            {
                return;
            }

            try
            {
                int[] region = _historyState.CollectConnectedSurfaceRegion(
                    tile.x,
                    tile.y,
                    out TerrainSurfaceStamp source);
                TerrainSurfaceEdit edit = _historyState.ApplyUniformSurfaceRegion(
                    region,
                    source,
                    target);
                if (edit.ChangedCellCount > 0)
                {
                    RecordAppliedEdit(edit);
                }
                else
                {
                    EditApplied?.Invoke(0);
                }
            }
            catch (Exception exception)
            {
                RaiseFailure("terrain_lab_error_surface_operation", exception.Message);
            }
        }

        private void AddDigitizingVertex(WorldTile tile)
        {
            TerrainGridPoint point = new TerrainGridPoint(tile.x, tile.y);
            if (_digitizingVertices.Count == 0 ||
                !_digitizingVertices[_digitizingVertices.Count - 1].Equals(point))
            {
                _digitizingVertices.Add(point);
            }
        }

        private void AddRectangleCorner(WorldTile tile)
        {
            TerrainGridPoint point = new TerrainGridPoint(tile.x, tile.y);
            if (_digitizingVertices.Count == 0)
            {
                _digitizingVertices.Add(point);
                return;
            }

            TerrainGridPoint first = _digitizingVertices[0];
            _digitizingVertices.Clear();
            int[] cells = TerrainDigitizingRaster.RasterizeRectangle(
                first,
                point,
                _historyState.Width,
                _historyState.Height);
            ApplyDigitizedSurface(cells);
        }

        private void FinishDigitization()
        {
            int minimumVertices = Tool == TerrainEditorTool.DrawPolygonSurface ? 3 : 2;
            if (_digitizingVertices.Count < minimumVertices)
            {
                RaiseFailure(
                    "terrain_lab_error_digitizing_vertices",
                    minimumVertices.ToString());
                return;
            }

            int[] cells = Tool == TerrainEditorTool.DrawPolygonSurface
                ? TerrainDigitizingRaster.RasterizePolygon(
                    _digitizingVertices,
                    _historyState.Width,
                    _historyState.Height)
                : TerrainDigitizingRaster.RasterizePolyline(
                    _digitizingVertices,
                    _historyState.Width,
                    _historyState.Height,
                    BrushRadius);
            _digitizingVertices.Clear();
            SetDigitizingVisible(false);
            ApplyDigitizedSurface(cells);
        }

        private void ApplyDigitizedSurface(int[] cells)
        {
            if (!TryGetSurfaceSample(out TerrainSurfaceStamp target))
            {
                return;
            }

            try
            {
                TerrainSurfaceEdit edit = _historyState.ApplySurfaceCells(cells, target);
                if (edit.ChangedCellCount > 0)
                {
                    RecordAppliedEdit(edit);
                }
                else
                {
                    EditApplied?.Invoke(0);
                }
            }
            catch (Exception exception)
            {
                RaiseFailure("terrain_lab_error_surface_operation", exception.Message);
            }
        }

        private void PolygonizeSurface(WorldTile tile)
        {
            try
            {
                _polygonizedSelection = _historyState.CollectConnectedSurfaceRegion(
                    tile.x,
                    tile.y,
                    out _);
                TerrainBoundarySegment[] boundary = TerrainDigitizingRaster.BuildBoundary(
                    _polygonizedSelection,
                    _historyState.Width,
                    _historyState.Height);
                UpdateSelectionMesh(boundary);
                RegionPolygonized?.Invoke(_polygonizedSelection.Length);
            }
            catch (Exception exception)
            {
                RaiseFailure("terrain_lab_error_surface_operation", exception.Message);
            }
        }

        private bool TryGetSurfaceSample(out TerrainSurfaceStamp target)
        {
            if (!_surfaceSample.HasValue)
            {
                target = default(TerrainSurfaceStamp);
                RaiseFailure("terrain_lab_error_surface_sample_required", null);
                return false;
            }

            target = _surfaceSample.Value;
            return true;
        }

        private void HandleDigitizingKeyboard()
        {
            if (!_interactionEnabled)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CancelDigitizing();
            }

            if (Input.GetKeyDown(KeyCode.Backspace) && _digitizingVertices.Count > 0)
            {
                _digitizingVertices.RemoveAt(_digitizingVertices.Count - 1);
            }

            if ((Input.GetKeyDown(KeyCode.Return) ||
                 Input.GetKeyDown(KeyCode.KeypadEnter)) &&
                (Tool == TerrainEditorTool.DrawLineSurface ||
                 Tool == TerrainEditorTool.DrawPolygonSurface) &&
                _digitizingVertices.Count > 0)
            {
                FinishDigitization();
            }
        }

        private void AddUndoEntry(ITerrainLabEdit edit)
        {
            _undoHistory.Add(edit);
            _undoHistoryBytes += edit.EstimatedBytes;
            while (_undoHistory.Count > 1 &&
                   (_undoHistory.Count > MaximumHistoryEntries ||
                    _undoHistoryBytes > MaximumHistoryBytes))
            {
                _undoHistoryBytes -= _undoHistory[0].EstimatedBytes;
                _undoHistory.RemoveAt(0);
            }
        }

        private void ClearRedoHistory()
        {
            _redoHistory.Clear();
            _redoHistoryBytes = 0;
        }

        private void SynchronizeProjectState()
        {
            TerrainWorldState current = _runtime?.State;
            if (ReferenceEquals(current, _historyState))
            {
                return;
            }

            _historyState = current;
            _undoHistory.Clear();
            _redoHistory.Clear();
            _undoHistoryBytes = 0;
            _redoHistoryBytes = 0;
            _surfaceSample = null;
            _digitizingVertices.Clear();
            ClearPolygonizedSelection();
            if (_historyState != null)
            {
                TargetElevation = _historyState.SeaLevel;
                if (TargetElevation == TerrainElevationEncoding.NoData)
                {
                    TargetElevation = 0;
                }
            }
        }

        private void CreateOutlines()
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            if (shader != null)
            {
                _outlineMaterial = new Material(shader);
            }

            _brushOutline = CreateLineRenderer(
                "TerrainLabBrushOutline",
                true,
                0.08f,
                OutlineSegments);
            _digitizingOutline = CreateLineRenderer(
                "TerrainLabDigitizingOutline",
                false,
                0.1f,
                0);

            GameObject selectionObject = new GameObject(
                "TerrainLabPolygonizedSelection",
                typeof(MeshFilter),
                typeof(MeshRenderer));
            selectionObject.transform.SetParent(transform, false);
            _selectionMeshFilter = selectionObject.GetComponent<MeshFilter>();
            _selectionMeshRenderer = selectionObject.GetComponent<MeshRenderer>();
            _selectionMeshRenderer.sharedMaterial = _outlineMaterial;
            _selectionMeshRenderer.sortingOrder = 32701;
            _selectionMeshRenderer.enabled = false;
            _selectionMesh = new Mesh { name = "TerrainLabSelectionBoundary" };
            _selectionMesh.MarkDynamic();
            _selectionMeshFilter.sharedMesh = _selectionMesh;

            UpdateOutlineColor();
            SetOutlineVisible(false);
            SetDigitizingVisible(false);
        }

        private LineRenderer CreateLineRenderer(
            string objectName,
            bool loop,
            float width,
            int positionCount)
        {
            GameObject outlineObject = new GameObject(objectName);
            outlineObject.transform.SetParent(transform, false);
            LineRenderer renderer = outlineObject.AddComponent<LineRenderer>();
            renderer.useWorldSpace = true;
            renderer.loop = loop;
            renderer.positionCount = positionCount;
            renderer.widthMultiplier = width;
            renderer.numCornerVertices = 2;
            renderer.numCapVertices = 2;
            renderer.sortingOrder = 32700;
            renderer.sharedMaterial = _outlineMaterial;
            return renderer;
        }

        private void UpdateBrushOutline(WorldTile tile)
        {
            if (!_interactionEnabled || Tool == TerrainEditorTool.None)
            {
                SetOutlineVisible(false);
                return;
            }

            bool usesRadius = Tool == TerrainEditorTool.SetElevation ||
                              Tool == TerrainEditorTool.RaiseElevation ||
                              Tool == TerrainEditorTool.LowerElevation ||
                              Tool == TerrainEditorTool.SmoothElevation ||
                              Tool == TerrainEditorTool.RampElevation ||
                              Tool == TerrainEditorTool.DrawLineSurface;
            float radius = usesRadius ? BrushRadius + 0.5f : 0.5f;
            Vector3 center = new Vector3(tile.x, tile.y, -1f);
            for (int index = 0; index < OutlineSegments; index++)
            {
                float angle = index * Mathf.PI * 2f / OutlineSegments;
                _brushOutline.SetPosition(
                    index,
                    center + new Vector3(
                        Mathf.Cos(angle) * radius,
                        Mathf.Sin(angle) * radius,
                        0f));
            }

            SetOutlineVisible(true);
        }

        private void UpdateDigitizingPreview(WorldTile hover)
        {
            if (!_interactionEnabled || _digitizingVertices.Count == 0 ||
                (Tool != TerrainEditorTool.DrawLineSurface &&
                 Tool != TerrainEditorTool.DrawPolygonSurface &&
                 Tool != TerrainEditorTool.DrawRectangleSurface &&
                 Tool != TerrainEditorTool.RampElevation))
            {
                SetDigitizingVisible(false);
                return;
            }

            if (Tool == TerrainEditorTool.DrawRectangleSurface)
            {
                TerrainGridPoint first = _digitizingVertices[0];
                float minX = Math.Min(first.X, hover.x) - 0.5f;
                float maxX = Math.Max(first.X, hover.x) + 0.5f;
                float minY = Math.Min(first.Y, hover.y) - 0.5f;
                float maxY = Math.Max(first.Y, hover.y) + 0.5f;
                _digitizingOutline.loop = true;
                _digitizingOutline.positionCount = 4;
                _digitizingOutline.SetPosition(0, new Vector3(minX, minY, -1.1f));
                _digitizingOutline.SetPosition(1, new Vector3(maxX, minY, -1.1f));
                _digitizingOutline.SetPosition(2, new Vector3(maxX, maxY, -1.1f));
                _digitizingOutline.SetPosition(3, new Vector3(minX, maxY, -1.1f));
                SetDigitizingVisible(true);
                return;
            }

            TerrainGridPoint hoverPoint = new TerrainGridPoint(hover.x, hover.y);
            bool appendHover = !_digitizingVertices[_digitizingVertices.Count - 1]
                .Equals(hoverPoint);
            int positionCount = _digitizingVertices.Count + (appendHover ? 1 : 0);
            _digitizingOutline.loop = Tool == TerrainEditorTool.DrawPolygonSurface &&
                                      positionCount >= 3;
            _digitizingOutline.positionCount = positionCount;
            for (int index = 0; index < _digitizingVertices.Count; index++)
            {
                TerrainGridPoint point = _digitizingVertices[index];
                _digitizingOutline.SetPosition(
                    index,
                    new Vector3(point.X, point.Y, -1.1f));
            }

            if (appendHover)
            {
                _digitizingOutline.SetPosition(
                    positionCount - 1,
                    new Vector3(hover.x, hover.y, -1.1f));
            }

            SetDigitizingVisible(positionCount > 1);
        }

        private void UpdateSelectionMesh(TerrainBoundarySegment[] boundary)
        {
            _selectionMesh.Clear();
            if (boundary == null || boundary.Length == 0)
            {
                _selectionMeshRenderer.enabled = false;
                return;
            }

            Vector3[] vertices = new Vector3[checked(boundary.Length * 2)];
            Color[] colors = new Color[vertices.Length];
            int[] indices = new int[vertices.Length];
            Color color = new Color(0.95f, 0.45f, 0.9f, 0.95f);
            for (int index = 0; index < boundary.Length; index++)
            {
                int vertex = index * 2;
                vertices[vertex] = new Vector3(
                    boundary[index].Start.X,
                    boundary[index].Start.Y,
                    -1.15f);
                vertices[vertex + 1] = new Vector3(
                    boundary[index].End.X,
                    boundary[index].End.Y,
                    -1.15f);
                colors[vertex] = color;
                colors[vertex + 1] = color;
                indices[vertex] = vertex;
                indices[vertex + 1] = vertex + 1;
            }

            _selectionMesh.vertices = vertices;
            _selectionMesh.colors = colors;
            _selectionMesh.SetIndices(indices, MeshTopology.Lines, 0, false);
            _selectionMeshRenderer.enabled = _interfaceVisible;
        }

        private void ClearPolygonizedSelection()
        {
            _polygonizedSelection = Array.Empty<int>();
            if (_selectionMesh != null)
            {
                _selectionMesh.Clear();
            }

            if (_selectionMeshRenderer != null)
            {
                _selectionMeshRenderer.enabled = false;
            }
        }

        private void UpdateOutlineColor()
        {
            if (_brushOutline == null)
            {
                return;
            }

            Color color;
            switch (Tool)
            {
                case TerrainEditorTool.RaiseElevation:
                    color = new Color(0.45f, 0.9f, 0.48f, 0.95f);
                    break;
                case TerrainEditorTool.LowerElevation:
                    color = new Color(0.42f, 0.7f, 1f, 0.95f);
                    break;
                case TerrainEditorTool.SmoothElevation:
                    color = new Color(0.95f, 0.72f, 0.3f, 0.95f);
                    break;
                case TerrainEditorTool.SetElevation:
                    color = new Color(0.95f, 0.9f, 0.42f, 0.95f);
                    break;
                case TerrainEditorTool.RampElevation:
                    color = new Color(0.45f, 0.95f, 0.8f, 0.95f);
                    break;
                case TerrainEditorTool.SampleSurface:
                    color = new Color(0.42f, 0.92f, 1f, 0.95f);
                    break;
                case TerrainEditorTool.FloodFillSurface:
                    color = new Color(1f, 0.65f, 0.25f, 0.95f);
                    break;
                case TerrainEditorTool.DrawLineSurface:
                case TerrainEditorTool.DrawPolygonSurface:
                case TerrainEditorTool.DrawRectangleSurface:
                    color = new Color(0.65f, 1f, 0.45f, 0.95f);
                    break;
                case TerrainEditorTool.PolygonizeSurface:
                    color = new Color(0.95f, 0.45f, 0.9f, 0.95f);
                    break;
                default:
                    color = new Color(0.86f, 0.82f, 0.68f, 0.9f);
                    break;
            }

            _brushOutline.startColor = color;
            _brushOutline.endColor = color;
            if (_digitizingOutline != null)
            {
                _digitizingOutline.startColor = color;
                _digitizingOutline.endColor = color;
            }
        }

        private void SetOutlineVisible(bool visible)
        {
            if (_brushOutline != null)
            {
                _brushOutline.enabled = visible;
            }
        }

        private void SetDigitizingVisible(bool visible)
        {
            if (_digitizingOutline != null)
            {
                _digitizingOutline.enabled = visible;
            }
        }

        private void RaiseFailure(string localizationKey, string detail)
        {
            OperationFailed?.Invoke(localizationKey, detail);
        }

        private void OnDestroy()
        {
            if (_selectionMesh != null)
            {
                Destroy(_selectionMesh);
            }

            if (_outlineMaterial != null)
            {
                Destroy(_outlineMaterial);
            }
        }
    }
}
