using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerrainLab
{
    public sealed class TerrainWaterDynamicsService
    {
        private const float TickIntervalSeconds = 0.2f;
        private const float ClimateIntervalSeconds = 30f;
        private const float RepaintDebounceSeconds = 0.75f;
        private const int MaximumInitialContacts = 96;
        private const int MaximumRecentPaintCells = 8192;
        private const int ContactBucketColumns = 16;
        private const int ContactBucketRows = 8;
        private const string TerrainLabWindowId = "terrain_lab_window";

        private readonly Queue<WorldTile> _paintedWater = new Queue<WorldTile>();
        private readonly HashSet<int> _queuedPaintIndices = new HashSet<int>();
        private readonly Dictionary<int, float> _recentPaintTimes =
            new Dictionary<int, float>();
        private readonly Dictionary<int, int> _geyserOutlets =
            new Dictionary<int, int>();
        private readonly Dictionary<int, TerrainSurfaceStamp> _autoDriedSurfaces =
            new Dictionary<int, TerrainSurfaceStamp>();
        private readonly List<int> _driedCells = new List<int>();
        private TerrainWorldState _state;
        private TerrainWaterRouting _routing;
        private TerrainWaterSimulation _simulation;
        private WorldTile[] _tiles;
        private long _routingRevision = -1;
        private float _nextTickTime;
        private float _nextClimateTime;
        private bool _pausedByUser;

        public event Action<IReadOnlyList<int>> CellsChanged;

        public event Action<TerrainElevationEdit> ElevationChanged;

        public string LastError { get; private set; }

        public bool Enabled => _state?.WaterDynamics?.Enabled == true;

        public int ActiveSourceCount => _simulation?.ActiveSourceCount ?? 0;

        public long PendingVolume => _simulation?.PendingVolume ?? 0L;

        public int ManagedCellCount =>
            _simulation?.ManagedCellCount ?? _state?.WaterDynamics?.ManagedCellCount ?? 0;

        public int FloodCellLimit => _simulation?.FloodCellLimit ?? 0;

        public bool LimitReached => _simulation?.LimitReached == true;

        public void AttachState(TerrainWorldState state)
        {
            ResetRuntime();
            _state = state;
            if (state == null)
            {
                return;
            }

            try
            {
                TerrainWaterState water = state.WaterDynamics ??
                    state.EnsureWaterDynamics();
                if (water.Enabled)
                {
                    // Existing oceans are classifications, not live sources.
                    BuildSimulation(seedExistingContacts: false);
                }
                else
                {
                    ReconcileWaterSurface(state, null);
                    _tiles = null;
                }
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                if (state.WaterDynamics != null)
                {
                    state.WaterDynamics.Enabled = false;
                    state.WaterDynamics.IsDirty = true;
                }

                Debug.LogError("[TerrainLab] Water dynamics disabled: " + exception);
            }
        }

        public bool TryReconcileWaterSurface(
            TerrainWorldState state,
            IReadOnlyList<int> changedIndices,
            out string error)
        {
            error = null;
            if (state == null || !state.MatchesCurrentWorld())
            {
                error = "Water classification requires a project matching the loaded world.";
                return false;
            }

            try
            {
                _state = state;
                ReconcileWaterSurface(state, changedIndices);
                NotifyCellsChanged(changedIndices);
                if (!Enabled)
                {
                    _tiles = null;
                }

                LastError = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                LastError = error;
                return false;
            }
        }

        public void Reset()
        {
            _state = null;
            ResetRuntime();
        }

        public bool TrySetEnabled(
            TerrainWorldState state,
            bool enabled,
            out string error)
        {
            error = null;
            if (state == null || !state.MatchesCurrentWorld())
            {
                error = "Water dynamics requires a TerrainLab project matching the loaded world.";
                return false;
            }

            try
            {
                TerrainWaterState water = state.WaterDynamics ??
                    state.EnsureWaterDynamics();
                water.Enabled = enabled;
                water.IsDirty = true;
                _state = state;
                LastError = null;
                if (enabled)
                {
                    bool firstActivation =
                        water.TotalInjectedVolume == 0L &&
                        water.ManagedCellCount == 0 &&
                        water.GeyserPulseCount == 0L;
                    BuildSimulation(seedExistingContacts: firstActivation);
                }
                else
                {
                    ResetRuntime(keepState: true);
                }

                _pausedByUser = !enabled;

                NotifyCellsChanged(null);

                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                LastError = error;
                if (state.WaterDynamics != null && enabled)
                {
                    state.WaterDynamics.Enabled = false;
                    state.WaterDynamics.IsDirty = true;
                }

                ResetRuntime(keepState: true);
                LastError = error;
                return false;
            }
        }

        public bool TryUpdateParameters(
            TerrainWorldState state,
            TerrainWaterParameters parameters,
            out string error)
        {
            error = null;
            if (state == null || parameters == null)
            {
                error = "Water parameters require an active TerrainLab project.";
                return false;
            }

            TerrainWaterState water = state.WaterDynamics ??
                state.EnsureWaterDynamics();
            TerrainWaterParameters normalized = parameters.Normalize();
            water.Parameters = normalized;
            water.IsDirty = true;
            if (ReferenceEquals(state, _state))
            {
                _simulation?.UpdateParameters(normalized);
            }

            return true;
        }

        public void NotifyGeyserPulse(WorldTile tile, int pulseCount)
        {
            if (tile == null || pulseCount <= 0)
            {
                return;
            }

            try
            {
                if (!TryAutoStartForGeyser())
                {
                    return;
                }

                EnsureSimulation();
                TerrainWaterState water = _state.WaterDynamics;
                int geyserIndex = GetTileIndex(tile);
                if (geyserIndex < 0)
                {
                    LastError = "Geyser pulse tile is outside the active TerrainLab grid.";
                    return;
                }

                water.GeyserPulseCount = SaturatingAdd(
                    water.GeyserPulseCount,
                    pulseCount);
                water.IsDirty = true;
                if (!TryGetGeyserOutlet(geyserIndex, out int origin))
                {
                    LastError = "Geyser has no gameplay-safe outlet within four cells.";
                    return;
                }

                long volume = SaturatingMultiply(
                    pulseCount,
                    water.Parameters.GeyserPulseVolume);
                int head = Math.Max(
                    Math.Max(
                        _state.Elevation[geyserIndex],
                        _routing.FilledElevation[geyserIndex]),
                    _state.Elevation[origin]);
                if (_simulation.AddSource(origin, head, volume))
                {
                    water.TotalInjectedVolume = SaturatingAdd(
                        water.TotalInjectedVolume,
                        volume);
                    LastError = null;
                }
                else
                {
                    LastError = "Geyser source registry is full.";
                }
            }
            catch (Exception exception)
            {
                DisableAfterError(exception);
            }
        }

        private bool TryAutoStartForGeyser()
        {
            if (Enabled)
            {
                return true;
            }

            if (_pausedByUser || _state == null || !_state.MatchesCurrentWorld())
            {
                return false;
            }

            TerrainWaterState water = _state.WaterDynamics ??
                _state.EnsureWaterDynamics();
            bool pristine = water.ManagedCellCount == 0 &&
                            water.TotalInjectedVolume == 0L &&
                            water.GeyserPulseCount == 0L;
            if (!pristine)
            {
                return false;
            }

            water.Enabled = true;
            water.IsDirty = true;
            BuildSimulation(seedExistingContacts: false);
            NotifyCellsChanged(null);
            return true;
        }

        private bool TryGetGeyserOutlet(int geyserIndex, out int outlet)
        {
            if (_geyserOutlets.TryGetValue(geyserIndex, out outlet) &&
                IsUsableGeyserOutlet(outlet))
            {
                return true;
            }

            int centerX = geyserIndex % _state.Width;
            int centerY = geyserIndex / _state.Width;
            for (int radius = 1; radius <= 4; radius++)
            {
                int selectedOutlet = -1;
                for (int offsetY = -radius; offsetY <= radius; offsetY++)
                {
                    int y = centerY + offsetY;
                    if (y < 0 || y >= _state.Height)
                    {
                        continue;
                    }

                    for (int offsetX = -radius; offsetX <= radius; offsetX++)
                    {
                        if (Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) != radius)
                        {
                            continue;
                        }

                        int x = centerX + offsetX;
                        if (x < 0 || x >= _state.Width)
                        {
                            continue;
                        }

                        int candidate = y * _state.Width + x;
                        if (!IsUsableGeyserOutlet(candidate) ||
                            selectedOutlet >= 0 &&
                            !IsBetterGeyserOutlet(candidate, selectedOutlet))
                        {
                            continue;
                        }

                        selectedOutlet = candidate;
                    }
                }

                if (selectedOutlet >= 0)
                {
                    outlet = selectedOutlet;
                    _geyserOutlets[geyserIndex] = outlet;
                    return true;
                }
            }

            outlet = -1;
            _geyserOutlets.Remove(geyserIndex);
            return false;
        }

        private bool IsBetterGeyserOutlet(int candidate, int current)
        {
            return _routing.FilledElevation[candidate] <
                   _routing.FilledElevation[current] ||
                   _routing.FilledElevation[candidate] ==
                   _routing.FilledElevation[current] &&
                   (_routing.Elevation[candidate] <
                    _routing.Elevation[current] ||
                    _routing.Elevation[candidate] ==
                    _routing.Elevation[current] && candidate < current);
        }

        private bool IsUsableGeyserOutlet(int index)
        {
            if (index < 0 || index >= _tiles.Length ||
                _state.Elevation[index] == TerrainElevationEncoding.NoData)
            {
                return false;
            }

            WorldTile candidate = _tiles[index];
            if (candidate == null || candidate.building != null)
            {
                return false;
            }

            if (_simulation.IsWater(index))
            {
                return _state.WaterDynamics.ManagedMask[index] != 0;
            }

            return TerrainSurfaceStamp.TryCaptureSafe(candidate, out _, out _);
        }

        public void NotifySurfaceLayerPainted(WorldTile tile, bool waterLayer)
        {
            NotifySurfaceLayerPainted(
                tile,
                waterLayer,
                default(TerrainSurfaceStamp));
        }

        public void NotifySurfaceLayerPainted(
            WorldTile tile,
            bool waterLayer,
            TerrainSurfaceStamp previousSurface)
        {
            if (tile == null || _state == null ||
                tile.x < 0 || tile.x >= _state.Width ||
                tile.y < 0 || tile.y >= _state.Height)
            {
                return;
            }

            int index = checked(tile.y * _state.Width + tile.x);
            if (waterLayer)
            {
                TerrainWaterState water = _state.WaterDynamics;
                if (water == null)
                {
                    water = _state.EnsureWaterDynamics();
                }

                TerrainHydroFeature existingFeature =
                    TerrainRiverValleyModel.NormalizeFeature(
                        water.HydroFeature[index]);
                bool previousSurfaceKnown =
                    !string.IsNullOrWhiteSpace(previousSurface.MainTypeId);
                bool freshwater = existingFeature != TerrainHydroFeature.None ||
                                  _state.Elevation[index] > _state.SeaLevel ||
                                  previousSurfaceKnown &&
                                  !IsWaterSurface(previousSurface);
                byte substrateMaterial = _state.Material[index];
                if (freshwater)
                {
                    TerrainSurfaceStamp restore = previousSurfaceKnown &&
                                                  !IsWaterSurface(previousSurface)
                        ? previousSurface
                        : GetDefaultRestoreSurface(
                            substrateMaterial,
                            _state.Landform[index]);
                    water.SetRestoreSurface(index, restore);
                    int fillDepth = _routing?.GetFillDepth(index) ?? 0;
                    TerrainHydroFeature feature = TerrainRiverValleyModel.ActivateCell(
                        _state,
                        water,
                        index,
                        fillDepth);
                    bool added = water.ManagedMask[index] == 0;
                    water.ManagedMask[index] = 1;
                    water.WaterStorage[index] = (byte)Math.Max(
                        water.WaterStorage[index],
                        TerrainWaterDepthModel.ShallowStorageUnits);
                    _simulation?.MarkExternalManagedWater(index);
                    water.ManagedCellCount = _simulation?.ManagedCellCount ??
                                             water.ManagedCellCount + (added ? 1 : 0);
                    water.IsDirty = true;
                    if (SetHydroSemantic(index, feature, substrateMaterial))
                    {
                        _state.MarkSemanticLayersChanged();
                    }
                }

                if (!TryReconcileWaterSurface(
                    _state,
                    new[] { index },
                    out string error))
                {
                    Debug.LogError(
                        "[TerrainLab] Water surface classification failed: " + error);
                    return;
                }

                if (freshwater)
                {
                    TerrainHydroFeature feature =
                        TerrainRiverValleyModel.NormalizeFeature(
                            water.HydroFeature[index]);
                    if (SetHydroSemantic(index, feature, substrateMaterial))
                    {
                        _state.MarkSemanticLayersChanged();
                    }

                    _routingRevision = _state.Revision;
                }

                if (!Enabled || !IsWater(tile))
                {
                    return;
                }
            }
            else
            {
                _autoDriedSurfaces.Remove(index);
                TerrainWaterState water = _state.WaterDynamics;
                if (IsWater(tile))
                {
                    _simulation?.MarkExternalWater(index);
                    return;
                }

                bool changed = false;
                if (_simulation != null)
                {
                    changed = _simulation.MarkExternalDry(index);
                }
                else if (water?.ManagedMask != null && water.ManagedMask[index] != 0)
                {
                    water.ManagedMask[index] = 0;
                    water.ManagedCellCount = Math.Max(0, water.ManagedCellCount - 1);
                    changed = true;
                }

                if (water != null)
                {
                    changed |= TerrainRiverValleyModel.NormalizeFeature(
                        water.HydroFeature[index]) != TerrainHydroFeature.None;
                    water.ClearManagedStorage(index);
                    TerrainRiverValleyModel.ClearCell(_state, water, index);
                    water.ManagedCellCount = _simulation?.ManagedCellCount ??
                                             water.ManagedCellCount;
                    water.IsDirty |= changed;
                }

                _state.RefreshSemanticCellFromWorld(index);
                _routingRevision = _state.Revision;
                NotifyCellsChanged(new[] { index });
                return;
            }

            index = GetTileIndex(tile);
            if (index < 0)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (_recentPaintTimes.TryGetValue(index, out float previous) &&
                now - previous < RepaintDebounceSeconds)
            {
                return;
            }

            if (_recentPaintTimes.Count >= MaximumRecentPaintCells &&
                !_recentPaintTimes.ContainsKey(index))
            {
                return;
            }

            _recentPaintTimes[index] = now;
            if (_queuedPaintIndices.Add(index))
            {
                _paintedWater.Enqueue(tile);
            }
        }

        public bool Poll(TerrainWorldState state, bool analysisRunning)
        {
            if (!ReferenceEquals(state, _state) || !Enabled)
            {
                return false;
            }

            bool blockingWindow = ScrollWindow.isWindowActive() &&
                                  !ScrollWindow.isCurrentWindow(
                                      TerrainLabWindowId);
            if (analysisRunning || Config.paused || blockingWindow)
            {
                return false;
            }

            try
            {
                EnsureSimulation();
                ProcessPaintedWater();
                bool climateChanged = ProcessClimate();
                bool changed = climateChanged;
                if (Time.unscaledTime < _nextTickTime || _simulation.LimitReached)
                {
                    if (climateChanged)
                    {
                        NotifyCellsChanged(null);
                    }

                    return changed;
                }

                _nextTickTime = Time.unscaledTime + TickIntervalSeconds;
                IReadOnlyList<TerrainWaterCellChange> changes = _simulation.Step(
                    state.WaterDynamics.Parameters.CellsPerTick,
                    CanFloodCell);
                IReadOnlyList<int> rechargedCells = _simulation.RechargedCells;
                bool recharged = ApplyRecharge(rechargedCells);
                changed |= recharged;
                if (changes.Count == 0)
                {
                    if (climateChanged)
                    {
                        NotifyCellsChanged(null);
                    }
                    else if (recharged)
                    {
                        NotifyCellsChanged(rechargedCells);
                    }

                    return changed;
                }

                ApplyChanges(changes);
                if (climateChanged)
                {
                    NotifyCellsChanged(null);
                }
                else if (recharged)
                {
                    List<int> combined = new List<int>(
                        changes.Count + rechargedCells.Count);
                    combined.AddRange(changes.Select(item => item.Index));
                    combined.AddRange(rechargedCells);
                    NotifyCellsChanged(combined);
                }
                else
                {
                    NotifyCellsChanged(changes.Select(item => item.Index).ToArray());
                }

                return true;
            }
            catch (Exception exception)
            {
                DisableAfterError(exception);
                return true;
            }
        }

        private void EnsureSimulation()
        {
            if (_state == null || !_state.MatchesCurrentWorld())
            {
                throw new InvalidOperationException(
                    "Water dynamics state no longer matches the loaded world.");
            }

            if (_simulation == null || _routingRevision != _state.Revision)
            {
                if (_simulation == null)
                {
                    BuildSimulation(seedExistingContacts: false);
                }
                else
                {
                    RebuildSimulationPreservingSources();
                }
            }
        }

        private void RebuildSimulationPreservingSources()
        {
            IReadOnlyList<TerrainWaterSourceSnapshot> sources =
                _simulation?.CaptureActiveSources() ??
                Array.Empty<TerrainWaterSourceSnapshot>();
            BuildSimulation(seedExistingContacts: false);
            foreach (TerrainWaterSourceSnapshot source in sources)
            {
                _simulation.AddSource(
                    source.Origin,
                    source.HeadElevation,
                    source.RemainingVolume);
            }
        }

        private void BuildSimulation(bool seedExistingContacts)
        {
            if (_state == null || !_state.MatchesCurrentWorld())
            {
                throw new InvalidOperationException(
                    "Water dynamics requires a project matching the loaded world.");
            }

            _tiles = _state.GetCurrentWorldTilesForRuntime();
            if (_tiles == null)
            {
                throw new InvalidOperationException("World tile cache is unavailable.");
            }

            TerrainWaterState water = _state.WaterDynamics ??
                _state.EnsureWaterDynamics();

            _routing = TerrainWaterRouting.Build(
                _state.Width,
                _state.Height,
                _state.Elevation);
            byte[] currentWater = new byte[_state.CellCount];
            bool managedMaskChanged = false;
            for (int index = 0; index < _tiles.Length; index++)
            {
                if (IsWater(_tiles[index]))
                {
                    currentWater[index] = 1;
                }
                else if (water.ManagedMask[index] != 0)
                {
                    water.ManagedMask[index] = 0;
                    water.ClearManagedStorage(index);
                    managedMaskChanged = true;
                }
            }

            if (managedMaskChanged)
            {
                water.ValidateAndRecount(_state.CellCount);
                water.IsDirty = true;
            }

            _simulation = new TerrainWaterSimulation(
                _routing,
                currentWater,
                water.ManagedMask,
                water.Parameters,
                _state.SeaLevel,
                _state.Material,
                water.HydroFeature,
                water.Moisture);
            ReclassifyExistingWater(currentWater, null);
            water.ManagedCellCount = _simulation.ManagedCellCount;
            _routingRevision = _state.Revision;
            _nextTickTime = Time.unscaledTime;
            _nextClimateTime = Time.unscaledTime + ClimateIntervalSeconds;
            _paintedWater.Clear();
            LastError = null;

            if (seedExistingContacts)
            {
                SeedExistingWaterContacts();
            }
        }

        private void SeedExistingWaterContacts()
        {
            Dictionary<int, ContactCandidate> buckets =
                new Dictionary<int, ContactCandidate>();
            for (int waterIndex = 0; waterIndex < _tiles.Length; waterIndex++)
            {
                if (!_simulation.IsWater(waterIndex))
                {
                    continue;
                }

                int head = Math.Max(_state.SeaLevel, _state.Elevation[waterIndex]);
                ContactCandidate candidate = FindBestContact(waterIndex, head);
                if (candidate == null)
                {
                    continue;
                }

                int x = candidate.Index % _state.Width;
                int y = candidate.Index / _state.Width;
                int bucketX = Math.Min(
                    ContactBucketColumns - 1,
                    x * ContactBucketColumns / _state.Width);
                int bucketY = Math.Min(
                    ContactBucketRows - 1,
                    y * ContactBucketRows / _state.Height);
                int bucket = bucketY * ContactBucketColumns + bucketX;
                if (!buckets.TryGetValue(bucket, out ContactCandidate previous) ||
                    candidate.Drop > previous.Drop ||
                    candidate.Drop == previous.Drop && candidate.Index < previous.Index)
                {
                    buckets[bucket] = candidate;
                }
            }

            TerrainWaterParameters parameters = _state.WaterDynamics.Parameters;
            foreach (ContactCandidate contact in buckets.Values
                         .OrderByDescending(item => item.Drop)
                         .ThenBy(item => item.Index)
                         .Take(MaximumInitialContacts))
            {
                AddFiniteSource(
                    contact.Index,
                    contact.HeadElevation,
                    parameters.InitialSourceVolume);
            }
        }

        private void ProcessPaintedWater()
        {
            while (_paintedWater.Count > 0)
            {
                WorldTile tile = _paintedWater.Dequeue();
                int waterIndex = GetTileIndex(tile);
                if (waterIndex >= 0)
                {
                    _queuedPaintIndices.Remove(waterIndex);
                }

                if (waterIndex < 0)
                {
                    continue;
                }

                if (!IsWater(tile) &&
                    (tile.Type == null || !tile.Type.can_be_filled_with_ocean))
                {
                    continue;
                }

                _simulation.MarkExternalWater(waterIndex);
                int head = Math.Max(_state.SeaLevel, _state.Elevation[waterIndex]);
                ContactCandidate best = FindBestContact(waterIndex, head);
                if (best != null)
                {
                    AddFiniteSource(
                        best.Index,
                        best.HeadElevation,
                        _state.WaterDynamics.Parameters.InitialSourceVolume);
                }
            }
        }

        private ContactCandidate FindBestContact(int waterIndex, int head)
        {
            ContactCandidate best = null;
            _routing.ForEachNeighbor(waterIndex, delegate(int neighbor)
            {
                if (_simulation.IsWater(neighbor) ||
                    _routing.Elevation[neighbor] == TerrainElevationEncoding.NoData ||
                    _routing.Elevation[neighbor] > head)
                {
                    return;
                }

                int drop = head - _routing.Elevation[neighbor];
                if (best == null || drop > best.Drop ||
                    drop == best.Drop && neighbor < best.Index)
                {
                    best = new ContactCandidate(neighbor, head, drop);
                }
            });
            return best;
        }

        private void AddFiniteSource(int origin, int head, long volume)
        {
            if (_simulation.AddSource(origin, head, volume))
            {
                TerrainWaterState water = _state.WaterDynamics;
                water.TotalInjectedVolume = SaturatingAdd(
                    water.TotalInjectedVolume,
                    volume);
                water.IsDirty = true;
            }
        }

        private bool CanFloodCell(int index)
        {
            if (index < 0 || index >= _tiles.Length ||
                _state.Elevation[index] == TerrainElevationEncoding.NoData)
            {
                return false;
            }

            WorldTile tile = _tiles[index];
            if (tile == null || IsWater(tile) ||
                !TerrainSurfaceStamp.TryCaptureSafe(tile, out _, out _))
            {
                return false;
            }

            return tile.building == null || IsGeyser(tile.building);
        }

        private void ApplyChanges(IReadOnlyList<TerrainWaterCellChange> changes)
        {
            TerrainWaterState water = _state.WaterDynamics;
            byte[] substrateMaterials = new byte[changes.Count];
            TerrainHydroFeature[] features = new TerrainHydroFeature[changes.Count];
            int[] indices = new int[changes.Count];
            for (int offset = 0; offset < changes.Count; offset++)
            {
                TerrainWaterCellChange change = changes[offset];
                int index = change.Index;
                indices[offset] = index;
                substrateMaterials[offset] = _state.Material[index];
                water.CaptureRestoreSurface(
                    index,
                    TerrainSurfaceStamp.Capture(_tiles[index]));
                int fillDepth = _routing.GetFillDepth(index);
                TerrainHydroFeature feature = TerrainRiverValleyModel.ActivateCell(
                    _state,
                    water,
                    index,
                    fillDepth);
                features[offset] = feature;
                int storedDepth = feature == TerrainHydroFeature.Waterbody
                    ? Math.Max(
                        TerrainWaterDepthModel.ShallowStorageUnits,
                        Math.Min(TerrainWaterDepthModel.ShelfStorageUnits, fillDepth))
                    : TerrainWaterDepthModel.ShallowStorageUnits;
                water.WaterStorage[index] = TerrainWaterDepthModel.GetStorage(storedDepth);
            }

            _state.ApplySurfaceCells(
                indices,
                new TerrainSurfaceStamp("shallow_waters", string.Empty, false));
            bool semanticChanged = false;
            for (int offset = 0; offset < indices.Length; offset++)
            {
                semanticChanged |= SetHydroSemantic(
                    indices[offset],
                    features[offset],
                    substrateMaterials[offset]);
            }

            if (semanticChanged)
            {
                _state.MarkSemanticLayersChanged();
            }

            water.ManagedCellCount = _simulation.ManagedCellCount;
            water.TotalConsumedVolume = SaturatingAdd(
                water.TotalConsumedVolume,
                changes.Sum(item => (long)item.Cost));
            water.IsDirty = true;
            _routingRevision = _state.Revision;
        }

        private void NotifyCellsChanged(IReadOnlyList<int> indices)
        {
            try
            {
                CellsChanged?.Invoke(indices);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "[TerrainLab] Water overlay listener failed: " + exception);
            }
        }

        private bool ApplyRecharge(IReadOnlyList<int> rechargedCells)
        {
            if (rechargedCells == null || rechargedCells.Count == 0)
            {
                return false;
            }

            TerrainWaterState water = _state.WaterDynamics;
            bool changed = false;
            for (int offset = 0; offset < rechargedCells.Count; offset++)
            {
                int index = rechargedCells[offset];
                if (index < 0 || index >= _tiles.Length ||
                    water.ManagedMask[index] == 0)
                {
                    continue;
                }

                byte target = TerrainWaterDepthModel.GetStorage(
                    GetDepthClass(_tiles[index]));
                if (water.WaterStorage[index] < target)
                {
                    water.WaterStorage[index] = target;
                    changed = true;
                }
            }

            water.IsDirty |= changed;
            return changed;
        }

        private bool ProcessClimate()
        {
            if (Time.unscaledTime < _nextClimateTime)
            {
                return false;
            }

            _nextClimateTime = Time.unscaledTime + ClimateIntervalSeconds;
            TerrainWaterState water = _state.WaterDynamics;
            int evaporation = water.Parameters.EvaporationPerClimateStep;
            bool changed = false;
            if (evaporation > 0 && water.ManagedCellCount > 0)
            {
                _driedCells.Clear();
                TerrainWaterBalance.ApplyEvaporation(
                    water.ManagedMask,
                    water.WaterStorage,
                    evaporation,
                    _driedCells);
                changed = true;

                Dictionary<TerrainSurfaceStamp, List<int>> restoreGroups =
                    new Dictionary<TerrainSurfaceStamp, List<int>>();
                List<int> clearedCells = new List<int>(_driedCells.Count);
                Dictionary<int, byte> substrateMaterials =
                    new Dictionary<int, byte>(_driedCells.Count);
                foreach (int index in _driedCells)
                {
                    if (index < 0 || index >= _tiles.Length ||
                        water.ManagedMask[index] == 0)
                    {
                        continue;
                    }

                    WorldTile tile = _tiles[index];
                    if (tile?.building != null)
                    {
                        water.WaterStorage[index] =
                            TerrainWaterDepthModel.ShallowStorageUnits;
                        continue;
                    }

                    substrateMaterials[index] = _state.Material[index];
                    if (!IsWater(tile))
                    {
                        clearedCells.Add(index);
                        continue;
                    }

                    TerrainSurfaceStamp restore = GetRestoreSurface(water, index);
                    if (!restoreGroups.TryGetValue(restore, out List<int> indices))
                    {
                        indices = new List<int>();
                        restoreGroups.Add(restore, indices);
                    }

                    indices.Add(index);
                    clearedCells.Add(index);
                }

                foreach (KeyValuePair<TerrainSurfaceStamp, List<int>> group in restoreGroups)
                {
                    _state.ApplySurfaceCells(group.Value.ToArray(), group.Key);
                }

                bool semanticChanged = false;
                foreach (int index in clearedCells)
                {
                    _simulation.MarkExternalDry(index);
                    water.ClearManagedStorage(index);
                    TerrainHydroFeature feature =
                        TerrainRiverValleyModel.NormalizeFeature(
                            water.HydroFeature[index]);
                    semanticChanged |= SetHydroSemantic(
                        index,
                        feature,
                        substrateMaterials[index]);
                }

                if (semanticChanged)
                {
                    _state.MarkSemanticLayersChanged();
                }
            }

            TerrainRiverEvolution evolution = TerrainRiverValleyModel.Step(
                _state,
                water,
                Math.Max(64, water.Parameters.CellsPerTick * 2));
            changed |= ApplyRiverEvolution(evolution);
            water.ManagedCellCount = _simulation.ManagedCellCount;
            water.IsDirty |= changed;
            _routingRevision = _state.Revision;
            return changed;
        }

        private bool ApplyRiverEvolution(TerrainRiverEvolution evolution)
        {
            if (evolution == null || !evolution.HasChanges)
            {
                return false;
            }

            TerrainWaterState water = _state.WaterDynamics;
            TerrainSurfaceStamp alluvium = new TerrainSurfaceStamp(
                "sand",
                string.Empty,
                false);
            bool semanticChanged = false;
            foreach (int index in evolution.SandIndices)
            {
                water.SetRestoreSurface(index, alluvium);
                semanticChanged |= SetHydroSemantic(
                    index,
                    TerrainRiverValleyModel.NormalizeFeature(
                        water.HydroFeature[index]),
                    (byte)TerrainMaterial.Sand);
            }

            foreach (int index in evolution.ClayIndices)
            {
                water.SetRestoreSurface(index, alluvium);
                semanticChanged |= SetHydroSemantic(
                    index,
                    TerrainRiverValleyModel.NormalizeFeature(
                        water.HydroFeature[index]),
                    (byte)TerrainMaterial.Clay);
            }

            if (semanticChanged)
            {
                _state.MarkSemanticLayersChanged();
            }

            if (evolution.IncisionIndices.Length > 0)
            {
                TerrainElevationEdit edit = _state.ApplyElevationValues(
                    evolution.IncisionIndices,
                    evolution.IncisionElevations);
                if (edit.ChangedCellCount > 0)
                {
                    NotifyElevationChanged(edit);
                    RebuildSimulationPreservingSources();
                }
            }

            water.IsDirty = true;
            return true;
        }

        private TerrainSurfaceStamp GetRestoreSurface(
            TerrainWaterState water,
            int index)
        {
            if (water.TryGetRestoreSurface(index, out TerrainSurfaceStamp restore) &&
                restore.TryResolve(out _, out _, out _))
            {
                return restore;
            }

            return new TerrainSurfaceStamp("soil_low", string.Empty, false);
        }

        private bool SetHydroSemantic(
            int index,
            TerrainHydroFeature feature,
            byte substrateMaterial)
        {
            if (index < 0 || index >= _state.CellCount ||
                feature == TerrainHydroFeature.None)
            {
                return false;
            }

            byte targetLandform = (byte)(feature == TerrainHydroFeature.River
                ? TerrainLandform.Channel
                : TerrainLandform.Depression);
            byte targetMaterial = substrateMaterial == (byte)TerrainMaterial.Unknown
                ? _state.Material[index]
                : substrateMaterial;
            bool changed = _state.Landform[index] != targetLandform ||
                           _state.Material[index] != targetMaterial;
            _state.Landform[index] = targetLandform;
            _state.Material[index] = targetMaterial;
            return changed;
        }

        private static bool IsWaterSurface(TerrainSurfaceStamp surface)
        {
            string id = surface.MainTypeId ?? string.Empty;
            return id.Contains("shallow_waters") ||
                   id.Contains("close_ocean") ||
                   id.Contains("deep_ocean");
        }

        private static TerrainSurfaceStamp GetDefaultRestoreSurface(
            byte material,
            byte landform)
        {
            string mainTypeId;
            switch ((TerrainMaterial)material)
            {
                case TerrainMaterial.Sand:
                case TerrainMaterial.Clay:
                    mainTypeId = "sand";
                    break;
                case TerrainMaterial.Rock:
                    TerrainLandform form = (TerrainLandform)landform;
                    mainTypeId = form == TerrainLandform.Summit
                        ? "mountains"
                        : form == TerrainLandform.Mountain ||
                          form == TerrainLandform.Cliff
                            ? "hills"
                            : "soil_high";
                    break;
                default:
                    mainTypeId = (TerrainLandform)landform == TerrainLandform.Upland ||
                                 (TerrainLandform)landform == TerrainLandform.Hill
                        ? "soil_high"
                        : "soil_low";
                    break;
            }

            TerrainSurfaceStamp result = new TerrainSurfaceStamp(
                mainTypeId,
                string.Empty,
                false);
            return result.TryResolve(out _, out _, out _)
                ? result
                : new TerrainSurfaceStamp("soil_low", string.Empty, false);
        }

        private void NotifyElevationChanged(TerrainElevationEdit edit)
        {
            try
            {
                ElevationChanged?.Invoke(edit);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "[TerrainLab] River elevation listener failed: " + exception);
            }
        }

        private void ReconcileWaterSurface(
            TerrainWorldState state,
            IReadOnlyList<int> changedIndices)
        {
            _tiles = state.GetCurrentWorldTilesForRuntime();
            if (_tiles == null || _tiles.Length != state.CellCount)
            {
                throw new InvalidOperationException("World tile cache is unavailable.");
            }

            ReclassifyExistingWater(null, changedIndices);
        }

        private void ReclassifyExistingWater(
            byte[] currentWater,
            IReadOnlyList<int> changedIndices)
        {
            TerrainWaterState water = _state.WaterDynamics;
            Dictionary<TerrainSurfaceStamp, List<int>> groups =
                new Dictionary<TerrainSurfaceStamp, List<int>>();
            List<int> dried = new List<int>();
            List<int> rewetted = new List<int>();
            Dictionary<int, byte> substrateMaterials =
                new Dictionary<int, byte>();
            bool storageChanged = false;
            int candidateCount = changedIndices?.Count ?? _tiles.Length;
            for (int offset = 0; offset < candidateCount; offset++)
            {
                int index = changedIndices == null ? offset : changedIndices[offset];
                if (index < 0 || index >= _tiles.Length)
                {
                    continue;
                }

                WorldTile tile = _tiles[index];
                if (tile == null || tile.building != null ||
                    _state.Elevation[index] == TerrainElevationEncoding.NoData)
                {
                    continue;
                }

                short elevation = _state.Elevation[index];
                TerrainHydroFeature hydroFeature = water == null
                    ? TerrainHydroFeature.None
                    : TerrainRiverValleyModel.NormalizeFeature(
                        water.HydroFeature[index]);
                if (hydroFeature != TerrainHydroFeature.None)
                {
                    substrateMaterials[index] = _state.Material[index];
                }

                if (!IsWater(tile))
                {
                    if (!_autoDriedSurfaces.TryGetValue(
                            index,
                            out TerrainSurfaceStamp drySurface))
                    {
                        continue;
                    }

                    if (!TerrainSurfaceStamp.Capture(tile).Equals(drySurface))
                    {
                        _autoDriedSurfaces.Remove(index);
                        continue;
                    }

                    if (!TerrainWaterDepthModel.TryClassifyElevation(
                        elevation,
                        _state.SeaLevel,
                        out TerrainWaterDepthClass restoredClass))
                    {
                        continue;
                    }

                    TerrainSurfaceStamp restored = new TerrainSurfaceStamp(
                        GetTileTypeId(restoredClass, false),
                        string.Empty,
                        drySurface.Frozen);
                    if (!groups.TryGetValue(restored, out List<int> restoreIndices))
                    {
                        restoreIndices = new List<int>();
                        groups.Add(restored, restoreIndices);
                    }

                    restoreIndices.Add(index);
                    rewetted.Add(index);
                    continue;
                }

                bool managed = water != null && water.ManagedMask[index] != 0;
                bool freshwater = managed ||
                                  hydroFeature != TerrainHydroFeature.None;
                TerrainSurfaceStamp target;
                TerrainWaterDepthClass depthClass =
                    TerrainWaterDepthClass.Shallow;
                bool marine = !freshwater &&
                              TerrainWaterDepthModel.TryClassifyElevation(
                                  elevation,
                                  _state.SeaLevel,
                                  out depthClass);
                if (!marine && !freshwater)
                {
                    target = new TerrainSurfaceStamp(
                        "soil_low",
                        string.Empty,
                        false);
                    _autoDriedSurfaces[index] = target;
                    dried.Add(index);
                }
                else
                {
                    if (freshwater)
                    {
                        depthClass = TerrainWaterDepthClass.Shallow;
                    }

                    int depth = marine
                        ? TerrainWaterDepthModel.GetDepthMetres(
                            elevation,
                            _state.SeaLevel)
                        : TerrainWaterDepthModel.ShallowStorageUnits;
                    if (managed)
                    {
                        byte storage = freshwater
                            ? (byte)Math.Max(
                                TerrainWaterDepthModel.ShallowStorageUnits,
                                water.WaterStorage[index])
                            : TerrainWaterDepthModel.GetStorage(depth);
                        if (water.WaterStorage[index] != storage)
                        {
                            water.WaterStorage[index] = storage;
                            storageChanged = true;
                        }
                    }

                    string targetId = GetTileTypeId(
                        depthClass,
                        (tile.main_type?.id ?? string.Empty).StartsWith(
                            "pit_",
                            StringComparison.Ordinal));
                    target = new TerrainSurfaceStamp(
                        targetId,
                        string.Empty,
                        tile.data != null && tile.data.frozen);
                    if (!target.TryResolve(out _, out _, out _))
                    {
                        target = new TerrainSurfaceStamp(
                            GetTileTypeId(depthClass, false),
                            string.Empty,
                            tile.data != null && tile.data.frozen);
                    }
                }

                if (string.Equals(
                    tile.main_type?.id,
                    target.MainTypeId,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                if (!groups.TryGetValue(target, out List<int> indices))
                {
                    indices = new List<int>();
                    groups.Add(target, indices);
                }

                indices.Add(index);
            }

            foreach (KeyValuePair<TerrainSurfaceStamp, List<int>> group in groups)
            {
                _state.ApplySurfaceCells(group.Value.ToArray(), group.Key);
            }

            bool semanticChanged = false;
            foreach (KeyValuePair<int, byte> substrate in substrateMaterials)
            {
                TerrainHydroFeature feature = TerrainRiverValleyModel.NormalizeFeature(
                    water.HydroFeature[substrate.Key]);
                semanticChanged |= SetHydroSemantic(
                    substrate.Key,
                    feature,
                    substrate.Value);
            }

            if (semanticChanged)
            {
                _state.MarkSemanticLayersChanged();
            }

            foreach (int index in rewetted)
            {
                _simulation?.MarkExternalWater(index);
                _autoDriedSurfaces.Remove(index);
            }

            foreach (int index in dried)
            {
                if (currentWater != null)
                {
                    currentWater[index] = 0;
                }

                _simulation?.MarkExternalDry(index);
                if (_simulation == null && water != null)
                {
                    water.ManagedMask[index] = 0;
                }

                water?.ClearManagedStorage(index);
            }

            if (water != null && (dried.Count > 0 || storageChanged))
            {
                if (_simulation != null)
                {
                    water.ManagedCellCount = _simulation.ManagedCellCount;
                }
                else
                {
                    water.ValidateAndRecount(_state.CellCount);
                }

                water.IsDirty = true;
            }
        }

        private static TerrainWaterDepthClass GetDepthClass(WorldTile tile)
        {
            string id = tile?.main_type?.id ?? string.Empty;
            if (id.Contains("deep_ocean"))
            {
                return TerrainWaterDepthClass.Deep;
            }

            return id.Contains("close_ocean")
                ? TerrainWaterDepthClass.Shelf
                : TerrainWaterDepthClass.Shallow;
        }

        private static string GetTileTypeId(
            TerrainWaterDepthClass depthClass,
            bool pit)
        {
            string id;
            switch (depthClass)
            {
                case TerrainWaterDepthClass.Deep:
                    id = "deep_ocean";
                    break;
                case TerrainWaterDepthClass.Shelf:
                    id = "close_ocean";
                    break;
                default:
                    id = "shallow_waters";
                    break;
            }

            return pit ? "pit_" + id : id;
        }

        private int GetTileIndex(WorldTile tile)
        {
            if (tile == null || tile.x < 0 || tile.x >= _state.Width ||
                tile.y < 0 || tile.y >= _state.Height)
            {
                return -1;
            }

            int index = checked(tile.y * _state.Width + tile.x);
            return ReferenceEquals(_tiles[index], tile) ? index : -1;
        }

        private static bool IsWater(WorldTile tile)
        {
            return tile?.Type != null && tile.Type.ocean;
        }

        private static bool IsGeyser(Building building)
        {
            BuildingData data = building?.getData() as BuildingData;
            return string.Equals(data?.asset_id, "geyser", StringComparison.Ordinal);
        }

        private void DisableAfterError(Exception exception)
        {
            string message = exception.Message;
            if (_state?.WaterDynamics != null)
            {
                _state.WaterDynamics.Enabled = false;
                _state.WaterDynamics.IsDirty = true;
            }

            Debug.LogError("[TerrainLab] Water dynamics disabled: " + exception);
            ResetRuntime(keepState: true);
            LastError = message;
        }

        private void ResetRuntime(bool keepState = false)
        {
            _routing = null;
            _simulation = null;
            _tiles = null;
            _routingRevision = -1;
            _nextTickTime = 0f;
            _nextClimateTime = 0f;
            _paintedWater.Clear();
            _queuedPaintIndices.Clear();
            _recentPaintTimes.Clear();
            _geyserOutlets.Clear();
            _driedCells.Clear();
            LastError = null;
            if (!keepState)
            {
                _pausedByUser = false;
                _autoDriedSurfaces.Clear();
                _state = null;
            }
        }

        private static long SaturatingAdd(long left, long right)
        {
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        private static long SaturatingMultiply(int left, int right)
        {
            long value = (long)left * right;
            return value < 0L ? long.MaxValue : value;
        }

        private sealed class ContactCandidate
        {
            public ContactCandidate(int index, int headElevation, int drop)
            {
                Index = index;
                HeadElevation = headElevation;
                Drop = drop;
            }

            public int Index { get; }

            public int HeadElevation { get; }

            public int Drop { get; }
        }
    }
}
