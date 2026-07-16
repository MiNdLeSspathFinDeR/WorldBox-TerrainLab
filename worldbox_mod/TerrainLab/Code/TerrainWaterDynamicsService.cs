using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TerrainLab
{
    public sealed class TerrainWaterDynamicsService
    {
        private const float TickIntervalSeconds = 0.2f;
        private const float RepaintDebounceSeconds = 0.75f;
        private const int MaximumInitialContacts = 96;
        private const int MaximumRecentPaintCells = 8192;
        private const int ContactBucketColumns = 16;
        private const int ContactBucketRows = 8;

        private readonly Queue<WorldTile> _paintedWater = new Queue<WorldTile>();
        private readonly HashSet<int> _queuedPaintIndices = new HashSet<int>();
        private readonly Dictionary<int, float> _recentPaintTimes =
            new Dictionary<int, float>();
        private TerrainWorldState _state;
        private TerrainWaterRouting _routing;
        private TerrainWaterSimulation _simulation;
        private WorldTile[] _tiles;
        private long _routingRevision = -1;
        private float _nextTickTime;

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
            if (state?.WaterDynamics?.Enabled != true)
            {
                return;
            }

            try
            {
                // Existing managed water is restored from the sidecar. Re-seeding every
                // load would turn a finite source into an implicit infinite one.
                BuildSimulation(seedExistingContacts: false);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                state.WaterDynamics.Enabled = false;
                state.WaterDynamics.IsDirty = true;
                Debug.LogError("[TerrainLab] Water dynamics disabled: " + exception);
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
                if (state.WaterDynamics == null)
                {
                    state.WaterDynamics = TerrainWaterState.Create(state.CellCount);
                }

                state.WaterDynamics.ValidateAndRecount(state.CellCount);
                state.WaterDynamics.Enabled = enabled;
                state.WaterDynamics.IsDirty = true;
                _state = state;
                LastError = null;
                if (enabled)
                {
                    bool firstActivation =
                        state.WaterDynamics.TotalInjectedVolume == 0L &&
                        state.WaterDynamics.ManagedCellCount == 0 &&
                        state.WaterDynamics.GeyserPulseCount == 0L;
                    BuildSimulation(seedExistingContacts: firstActivation);
                }
                else
                {
                    ResetRuntime(keepState: true);
                }

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

            if (state.WaterDynamics == null)
            {
                state.WaterDynamics = TerrainWaterState.Create(state.CellCount);
            }

            TerrainWaterParameters normalized = parameters.Normalize();
            state.WaterDynamics.Parameters = normalized;
            state.WaterDynamics.IsDirty = true;
            if (ReferenceEquals(state, _state))
            {
                _simulation?.UpdateParameters(normalized);
            }

            return true;
        }

        public void NotifyGeyserPulse(WorldTile tile, int pulseCount)
        {
            if (!Enabled || tile == null || pulseCount <= 0)
            {
                return;
            }

            try
            {
                EnsureSimulation();
                int origin = GetTileIndex(tile);
                if (origin < 0)
                {
                    return;
                }

                TerrainWaterState water = _state.WaterDynamics;
                long volume = SaturatingMultiply(
                    pulseCount,
                    water.Parameters.GeyserPulseVolume);
                int head = Math.Max(
                    _state.Elevation[origin],
                    _routing.FilledElevation[origin]);
                if (_simulation.AddSource(origin, head, volume))
                {
                    water.TotalInjectedVolume = SaturatingAdd(
                        water.TotalInjectedVolume,
                        volume);
                    water.GeyserPulseCount = SaturatingAdd(
                        water.GeyserPulseCount,
                        pulseCount);
                    water.IsDirty = true;
                }
            }
            catch (Exception exception)
            {
                DisableAfterError(exception);
            }
        }

        public void NotifySurfaceLayerPainted(WorldTile tile, bool waterLayer)
        {
            if (!Enabled || tile == null)
            {
                return;
            }

            int index = GetTileIndex(tile);
            if (index < 0)
            {
                return;
            }

            if (!waterLayer)
            {
                if (IsWater(tile))
                {
                    _simulation.MarkExternalWater(index);
                }
                else if (_simulation.MarkExternalDry(index))
                {
                    _state.WaterDynamics.ManagedCellCount =
                        _simulation.ManagedCellCount;
                    _state.WaterDynamics.IsDirty = true;
                }

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

            if (analysisRunning || Config.paused || ScrollWindow.isWindowActive())
            {
                return false;
            }

            try
            {
                EnsureSimulation();
                ProcessPaintedWater();
                if (Time.unscaledTime < _nextTickTime || _simulation.LimitReached)
                {
                    return false;
                }

                _nextTickTime = Time.unscaledTime + TickIntervalSeconds;
                IReadOnlyList<TerrainWaterCellChange> changes = _simulation.Step(
                    state.WaterDynamics.Parameters.CellsPerTick,
                    CanFloodCell);
                if (changes.Count == 0)
                {
                    return false;
                }

                ApplyChanges(changes);
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
                BuildSimulation(seedExistingContacts: false);
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
                TerrainWaterState.Create(_state.CellCount);
            _state.WaterDynamics = water;
            water.ValidateAndRecount(_state.CellCount);

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
                water.Parameters);
            water.ManagedCellCount = _simulation.ManagedCellCount;
            _routingRevision = _state.Revision;
            _nextTickTime = Time.unscaledTime;
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
            long consumed = 0L;
            foreach (IGrouping<TerrainWaterDepthClass, TerrainWaterCellChange> group in
                     changes.GroupBy(item => item.DepthClass))
            {
                string tileTypeId;
                switch (group.Key)
                {
                    case TerrainWaterDepthClass.Deep:
                        tileTypeId = "deep_ocean";
                        break;
                    case TerrainWaterDepthClass.Coastal:
                        tileTypeId = "close_ocean";
                        break;
                    default:
                        tileTypeId = "shallow_waters";
                        break;
                }

                int[] indices = group.Select(item => item.Index).ToArray();
                _state.ApplySurfaceCells(
                    indices,
                    new TerrainSurfaceStamp(tileTypeId, string.Empty, false));
                consumed = SaturatingAdd(consumed, group.Sum(item => (long)item.Cost));
            }

            TerrainWaterState water = _state.WaterDynamics;
            water.ManagedCellCount = _simulation.ManagedCellCount;
            water.TotalConsumedVolume = SaturatingAdd(
                water.TotalConsumedVolume,
                consumed);
            water.IsDirty = true;
            _routingRevision = _state.Revision;
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
            _paintedWater.Clear();
            _queuedPaintIndices.Clear();
            _recentPaintTimes.Clear();
            LastError = null;
            if (!keepState)
            {
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
