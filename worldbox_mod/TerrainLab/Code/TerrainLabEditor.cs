using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerrainLab
{
    public enum TerrainEditorTool
    {
        Inspect,
        SetElevation,
        RaiseElevation,
        LowerElevation,
        SmoothElevation
    }

    public sealed class TerrainLabEditor : MonoBehaviour
    {
        private const int MaximumHistoryEntries = 32;
        private const int OutlineSegments = 48;

        private readonly List<TerrainElevationEdit> _undoHistory =
            new List<TerrainElevationEdit>();
        private readonly List<TerrainElevationEdit> _redoHistory =
            new List<TerrainElevationEdit>();

        private TerrainLabRuntime _runtime;
        private TerrainWorldState _historyState;
        private LineRenderer _brushOutline;
        private Material _outlineMaterial;
        private bool _interfaceVisible;
        private bool _interactionEnabled;
        private bool _initialized;

        public event Action<int> EditApplied;

        public TerrainEditorTool Tool { get; private set; } = TerrainEditorTool.Inspect;

        public int BrushRadius { get; private set; } = 2;

        public short TargetElevation { get; private set; } = 100;

        public short Step { get; private set; } = 10;

        public WorldTile HoveredTile { get; private set; }

        public bool CanUndo => _undoHistory.Count > 0;

        public bool CanRedo => _redoHistory.Count > 0;

        public void Initialize(TerrainLabRuntime runtime)
        {
            if (_initialized)
            {
                return;
            }

            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            CreateBrushOutline();
            SynchronizeProjectState();
            _initialized = true;
        }

        public void SetInterfaceState(bool visible, bool interactionEnabled)
        {
            _interfaceVisible = visible;
            _interactionEnabled = visible && interactionEnabled;
            if (!visible)
            {
                HoveredTile = null;
                SetOutlineVisible(false);
            }
        }

        public void SetTool(TerrainEditorTool tool)
        {
            Tool = tool;
            UpdateOutlineColor();
        }

        public void SetBrushRadius(int radius)
        {
            BrushRadius = Mathf.Clamp(radius, 0, 16);
        }

        public bool TrySetTargetElevation(string value, out string error)
        {
            error = null;
            if (!short.TryParse(value, out short parsed))
            {
                error = "Elevation must be a signed Int16 value.";
                return false;
            }

            if (parsed == TerrainElevationEncoding.NoData)
            {
                error = "Elevation 9999 is reserved for NODATA.";
                return false;
            }

            TargetElevation = parsed;
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
            TerrainElevationEdit edit = _undoHistory[lastIndex];
            _undoHistory.RemoveAt(lastIndex);
            _historyState.ApplyElevationEdit(edit, false);
            _redoHistory.Add(edit);
            changedCells = edit.ChangedCellCount;
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
            TerrainElevationEdit edit = _redoHistory[lastIndex];
            _redoHistory.RemoveAt(lastIndex);
            _historyState.ApplyElevationEdit(edit, true);
            _undoHistory.Add(edit);
            changedCells = edit.ChangedCellCount;
            return true;
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
                return;
            }

            if (MapBox.instance.isOverUI())
            {
                HoveredTile = null;
                SetOutlineVisible(false);
                return;
            }

            HoveredTile = MapBox.instance.getMouseTilePos();
            if (HoveredTile == null)
            {
                SetOutlineVisible(false);
                return;
            }

            UpdateBrushOutline(HoveredTile);
            if (_interactionEnabled && Tool != TerrainEditorTool.Inspect &&
                Input.GetMouseButtonDown(0))
            {
                ApplyCurrentTool(HoveredTile);
            }
        }

        private void ApplyCurrentTool(WorldTile centerTile)
        {
            TerrainElevationOperation operation;
            switch (Tool)
            {
                case TerrainEditorTool.SetElevation:
                    operation = TerrainElevationOperation.Set;
                    break;
                case TerrainEditorTool.RaiseElevation:
                    operation = TerrainElevationOperation.Raise;
                    break;
                case TerrainEditorTool.LowerElevation:
                    operation = TerrainElevationOperation.Lower;
                    break;
                case TerrainEditorTool.SmoothElevation:
                    operation = TerrainElevationOperation.Smooth;
                    break;
                default:
                    return;
            }

            TerrainElevationEdit edit = _historyState.ApplyElevationBrush(
                centerTile.x,
                centerTile.y,
                BrushRadius,
                operation,
                TargetElevation,
                Step);
            if (edit.ChangedCellCount > 0)
            {
                _undoHistory.Add(edit);
                if (_undoHistory.Count > MaximumHistoryEntries)
                {
                    _undoHistory.RemoveAt(0);
                }

                _redoHistory.Clear();
            }

            EditApplied?.Invoke(edit.ChangedCellCount);
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
            if (_historyState != null)
            {
                TargetElevation = _historyState.SeaLevel;
                if (TargetElevation == TerrainElevationEncoding.NoData)
                {
                    TargetElevation = 0;
                }
            }
        }

        private void CreateBrushOutline()
        {
            GameObject outlineObject = new GameObject("TerrainLabBrushOutline");
            outlineObject.transform.SetParent(transform, false);
            _brushOutline = outlineObject.AddComponent<LineRenderer>();
            _brushOutline.useWorldSpace = true;
            _brushOutline.loop = true;
            _brushOutline.positionCount = OutlineSegments;
            _brushOutline.widthMultiplier = 0.08f;
            _brushOutline.numCornerVertices = 2;
            _brushOutline.numCapVertices = 2;
            _brushOutline.sortingOrder = 32700;

            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            if (shader != null)
            {
                _outlineMaterial = new Material(shader);
                _brushOutline.sharedMaterial = _outlineMaterial;
            }

            UpdateOutlineColor();
            SetOutlineVisible(false);
        }

        private void UpdateBrushOutline(WorldTile tile)
        {
            if (!_interactionEnabled)
            {
                SetOutlineVisible(false);
                return;
            }

            float radius = Tool == TerrainEditorTool.Inspect
                ? 0.5f
                : BrushRadius + 0.5f;
            Vector3 center = new Vector3(tile.x, tile.y, -1f);
            for (int index = 0; index < OutlineSegments; index++)
            {
                float angle = index * Mathf.PI * 2f / OutlineSegments;
                _brushOutline.SetPosition(
                    index,
                    center + new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f));
            }

            SetOutlineVisible(true);
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
                default:
                    color = new Color(0.86f, 0.82f, 0.68f, 0.9f);
                    break;
            }

            _brushOutline.startColor = color;
            _brushOutline.endColor = color;
        }

        private void SetOutlineVisible(bool visible)
        {
            if (_brushOutline != null)
            {
                _brushOutline.enabled = visible;
            }
        }

        private void OnDestroy()
        {
            if (_outlineMaterial != null)
            {
                Destroy(_outlineMaterial);
            }
        }
    }
}
