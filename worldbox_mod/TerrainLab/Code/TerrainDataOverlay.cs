using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerrainLab
{
    public enum TerrainDataOverlayMode
    {
        None,
        Landform,
        Material,
        Contours,
        ManagedWater,
        WaterStorage,
        HydroFeature,
        Moisture,
        Erodibility,
        LocalSlope,
        LocalAspect
    }

    public sealed class TerrainDataOverlay : MonoBehaviour
    {
        private sealed class OverlayChunk
        {
            public int StartX;
            public int StartY;
            public int Width;
            public int Height;
            public Color32[] Pixels;
            public Texture2D Texture;
            public Sprite Sprite;
            public GameObject GameObject;
        }

        public const int ContourIntervalMetres = 250;
        public const int MajorContourIntervalMetres = 1000;

        private const int ChunkSize = 256;
        private const int SortingOrder = 32600;

        private readonly Dictionary<int, OverlayChunk> _chunks =
            new Dictionary<int, OverlayChunk>();
        private TerrainWorldState _sourceState;
        private int _chunkColumns;

        public TerrainDataOverlayMode Mode { get; private set; }

        public bool IsVisible =>
            Mode != TerrainDataOverlayMode.None && _sourceState != null;

        public bool References(TerrainWorldState state)
        {
            return IsVisible && ReferenceEquals(_sourceState, state);
        }

        public void Show(TerrainDataOverlayMode mode, TerrainWorldState state)
        {
            Clear();
            if (!CanDisplay(mode, state))
            {
                return;
            }

            _sourceState = state;
            _chunkColumns = (state.Width + ChunkSize - 1) / ChunkSize;
            for (int startY = 0; startY < state.Height; startY += ChunkSize)
            {
                int chunkHeight = Math.Min(ChunkSize, state.Height - startY);
                for (int startX = 0; startX < state.Width; startX += ChunkSize)
                {
                    int chunkWidth = Math.Min(ChunkSize, state.Width - startX);
                    CreateChunk(startX, startY, chunkWidth, chunkHeight, state, mode);
                }
            }

            // A valid all-zero raster is still an active layer. Keeping its
            // source allows the first wet cell to create a chunk incrementally.
            Mode = mode;
        }

        public void Refresh(TerrainWorldState state)
        {
            if (!References(state))
            {
                Clear();
                return;
            }

            TerrainDataOverlayMode mode = Mode;
            Show(mode, state);
        }

        public void UpdateCells(TerrainWorldState state, IReadOnlyList<int> indices)
        {
            if (!References(state))
            {
                return;
            }

            if (indices == null || indices.Count > state.CellCount / 4)
            {
                UpdateAllCells(state);
                return;
            }

            IReadOnlyList<int> refreshIndices = indices;
            if (Mode == TerrainDataOverlayMode.Contours)
            {
                HashSet<int> contourIndices = new HashSet<int>();
                for (int offset = 0; offset < indices.Count; offset++)
                {
                    int index = indices[offset];
                    if (index < 0 || index >= state.CellCount)
                    {
                        continue;
                    }

                    int x = index % state.Width;
                    int y = index / state.Width;
                    contourIndices.Add(index);
                    if (x > 0)
                    {
                        contourIndices.Add(index - 1);
                    }

                    if (x + 1 < state.Width)
                    {
                        contourIndices.Add(index + 1);
                    }

                    if (y > 0)
                    {
                        contourIndices.Add(index - state.Width);
                    }

                    if (y + 1 < state.Height)
                    {
                        contourIndices.Add(index + state.Width);
                    }
                }

                refreshIndices = new List<int>(contourIndices);
            }

            HashSet<OverlayChunk> dirtyChunks = new HashSet<OverlayChunk>();
            for (int offset = 0; offset < refreshIndices.Count; offset++)
            {
                int index = refreshIndices[offset];
                if (index < 0 || index >= state.CellCount)
                {
                    continue;
                }

                int x = index % state.Width;
                int y = index / state.Width;
                int key = y / ChunkSize * _chunkColumns + x / ChunkSize;
                if (!_chunks.TryGetValue(key, out OverlayChunk chunk))
                {
                    if (GetColor(index, state, Mode).a != 0)
                    {
                        Refresh(state);
                        return;
                    }

                    continue;
                }

                int localX = x - chunk.StartX;
                int localY = y - chunk.StartY;
                chunk.Pixels[localY * chunk.Width + localX] =
                    GetColor(index, state, Mode);
                dirtyChunks.Add(chunk);
            }

            foreach (OverlayChunk chunk in dirtyChunks)
            {
                chunk.Texture.SetPixels32(chunk.Pixels);
                chunk.Texture.Apply(false, false);
            }
        }

        private void UpdateAllCells(TerrainWorldState state)
        {
            for (int startY = 0; startY < state.Height; startY += ChunkSize)
            {
                int chunkHeight = Math.Min(ChunkSize, state.Height - startY);
                for (int startX = 0; startX < state.Width; startX += ChunkSize)
                {
                    int chunkWidth = Math.Min(ChunkSize, state.Width - startX);
                    int key = startY / ChunkSize * _chunkColumns +
                              startX / ChunkSize;
                    if (!_chunks.TryGetValue(key, out OverlayChunk chunk))
                    {
                        CreateChunk(
                            startX,
                            startY,
                            chunkWidth,
                            chunkHeight,
                            state,
                            Mode);
                        continue;
                    }

                    for (int localY = 0; localY < chunk.Height; localY++)
                    {
                        int sourceOffset =
                            (chunk.StartY + localY) * state.Width + chunk.StartX;
                        int pixelOffset = localY * chunk.Width;
                        for (int localX = 0; localX < chunk.Width; localX++)
                        {
                            chunk.Pixels[pixelOffset + localX] = GetColor(
                                sourceOffset + localX,
                                state,
                                Mode);
                        }
                    }

                    chunk.Texture.SetPixels32(chunk.Pixels);
                    chunk.Texture.Apply(false, false);
                }
            }
        }

        public void Clear()
        {
            foreach (OverlayChunk chunk in _chunks.Values)
            {
                if (chunk.GameObject != null)
                {
                    Destroy(chunk.GameObject);
                }

                if (chunk.Sprite != null)
                {
                    Destroy(chunk.Sprite);
                }

                if (chunk.Texture != null)
                {
                    Destroy(chunk.Texture);
                }
            }

            _chunks.Clear();
            _sourceState = null;
            _chunkColumns = 0;
            Mode = TerrainDataOverlayMode.None;
        }

        public static Color32 GetLandformColor(byte value)
        {
            switch ((TerrainLandform)value)
            {
                case TerrainLandform.Plain:
                    return new Color32(92, 177, 88, 150);
                case TerrainLandform.Lowland:
                    return new Color32(155, 190, 83, 150);
                case TerrainLandform.Upland:
                    return new Color32(218, 183, 72, 165);
                case TerrainLandform.Hill:
                    return new Color32(220, 126, 57, 180);
                case TerrainLandform.Mountain:
                    return new Color32(118, 108, 102, 195);
                case TerrainLandform.Summit:
                    return new Color32(244, 245, 239, 220);
                case TerrainLandform.Channel:
                    return new Color32(48, 151, 225, 205);
                case TerrainLandform.Depression:
                    return new Color32(115, 72, 164, 190);
                case TerrainLandform.Cliff:
                    return new Color32(145, 53, 48, 215);
                case TerrainLandform.Artificial:
                    return new Color32(226, 70, 178, 210);
                default:
                    return new Color32(105, 105, 105, 90);
            }
        }

        public static Color32 GetMaterialColor(byte value)
        {
            switch ((TerrainMaterial)value)
            {
                case TerrainMaterial.Soil:
                    return new Color32(145, 101, 63, 165);
                case TerrainMaterial.Sand:
                    return new Color32(235, 204, 112, 175);
                case TerrainMaterial.Rock:
                    return new Color32(126, 132, 137, 195);
                case TerrainMaterial.Ice:
                    return new Color32(157, 231, 244, 205);
                case TerrainMaterial.Lava:
                    return new Color32(240, 63, 27, 225);
                case TerrainMaterial.Organic:
                    return new Color32(54, 154, 72, 180);
                case TerrainMaterial.Artificial:
                    return new Color32(213, 74, 181, 210);
                case TerrainMaterial.Clay:
                    return new Color32(166, 92, 71, 205);
                default:
                    return new Color32(105, 105, 105, 90);
            }
        }

        public static Color32 GetManagedWaterColor(byte value)
        {
            return value == 0
                ? new Color32(0, 0, 0, 0)
                : new Color32(40, 226, 245, 215);
        }

        public static Color32 GetWaterStorageColor(byte value)
        {
            if (value == 0)
            {
                return new Color32(0, 0, 0, 0);
            }

            float ratio = value / (float)byte.MaxValue;
            return ratio < 0.59f
                ? Lerp(
                    new Color32(100, 235, 230, 145),
                    new Color32(25, 100, 235, 210),
                    ratio / 0.59f)
                : Lerp(
                    new Color32(25, 100, 235, 210),
                    new Color32(205, 55, 185, 235),
                    (ratio - 0.59f) / 0.41f);
        }

        public static Color32 GetHydroFeatureColor(byte value)
        {
            switch (TerrainRiverValleyModel.NormalizeFeature(value))
            {
                case TerrainHydroFeature.River:
                    return new Color32(25, 220, 245, 225);
                case TerrainHydroFeature.Waterbody:
                    return new Color32(42, 90, 235, 220);
                default:
                    return new Color32(0, 0, 0, 0);
            }
        }

        public static Color32 GetMoistureColor(byte value)
        {
            if (value == 0)
            {
                return new Color32(0, 0, 0, 0);
            }

            float ratio = value / (float)byte.MaxValue;
            return ratio < 0.5f
                ? Lerp(
                    new Color32(181, 120, 54, 105),
                    new Color32(46, 190, 155, 185),
                    ratio * 2f)
                : Lerp(
                    new Color32(46, 190, 155, 185),
                    new Color32(26, 82, 238, 230),
                    (ratio - 0.5f) * 2f);
        }

        public static Color32 GetErodibilityColor(byte value)
        {
            if (value == 0 || value == byte.MaxValue)
            {
                return new Color32(0, 0, 0, 0);
            }

            float ratio = value / (float)TerrainRiverValleyModel.MaximumEncodedValue;
            return ratio < 0.5f
                ? Lerp(
                    new Color32(43, 145, 78, 125),
                    new Color32(245, 214, 54, 195),
                    ratio * 2f)
                : Lerp(
                    new Color32(245, 214, 54, 195),
                    new Color32(225, 50, 38, 230),
                    (ratio - 0.5f) * 2f);
        }

        public static Color32 GetLocalSlopeColor(byte value)
        {
            if (value == TerrainRiverValleyModel.NoDirection)
            {
                return new Color32(0, 0, 0, 0);
            }

            float ratio = value / (float)TerrainRiverValleyModel.MaximumEncodedValue;
            return ratio < 0.5f
                ? Lerp(
                    new Color32(36, 157, 88, 110),
                    new Color32(247, 214, 55, 195),
                    ratio * 2f)
                : Lerp(
                    new Color32(247, 214, 55, 195),
                    new Color32(210, 42, 35, 230),
                    (ratio - 0.5f) * 2f);
        }

        public static Color32 GetLocalAspectColor(byte value)
        {
            if (value == TerrainRiverValleyModel.NoDirection)
            {
                return new Color32(0, 0, 0, 0);
            }

            Color color = Color.HSVToRGB(
                value / (float)TerrainRiverValleyModel.MaximumEncodedValue,
                0.82f,
                1f);
            return new Color32(
                (byte)Mathf.RoundToInt(color.r * byte.MaxValue),
                (byte)Mathf.RoundToInt(color.g * byte.MaxValue),
                (byte)Mathf.RoundToInt(color.b * byte.MaxValue),
                205);
        }

        public static Color32 GetContourColor(
            int x,
            int y,
            int width,
            int height,
            short[] elevation)
        {
            int index = checked(y * width + x);
            short center = elevation[index];
            if (center == TerrainElevationEncoding.NoData)
            {
                return new Color32(0, 0, 0, 0);
            }

            bool minor = false;
            bool major = false;
            bool sea = false;
            CheckContourNeighbor(x - 1, y, width, height, center, elevation,
                ref minor, ref major, ref sea);
            CheckContourNeighbor(x + 1, y, width, height, center, elevation,
                ref minor, ref major, ref sea);
            CheckContourNeighbor(x, y - 1, width, height, center, elevation,
                ref minor, ref major, ref sea);
            CheckContourNeighbor(x, y + 1, width, height, center, elevation,
                ref minor, ref major, ref sea);

            if (sea)
            {
                return new Color32(55, 220, 245, 225);
            }

            if (major)
            {
                return new Color32(255, 204, 70, 215);
            }

            return minor
                ? new Color32(235, 235, 225, 150)
                : new Color32(0, 0, 0, 0);
        }

        private static bool CanDisplay(
            TerrainDataOverlayMode mode,
            TerrainWorldState state)
        {
            if (mode == TerrainDataOverlayMode.None || state == null ||
                state.Width <= 0 || state.Height <= 0 ||
                state.Elevation?.Length != checked(state.Width * state.Height))
            {
                return false;
            }

            switch (mode)
            {
                case TerrainDataOverlayMode.Landform:
                    return state.Landform?.Length == state.CellCount;
                case TerrainDataOverlayMode.Material:
                    return state.Material?.Length == state.CellCount;
                case TerrainDataOverlayMode.ManagedWater:
                    return state.WaterDynamics?.ManagedMask?.Length == state.CellCount;
                case TerrainDataOverlayMode.WaterStorage:
                    return state.WaterDynamics?.WaterStorage?.Length == state.CellCount;
                case TerrainDataOverlayMode.HydroFeature:
                    return state.WaterDynamics?.HydroFeature?.Length == state.CellCount;
                case TerrainDataOverlayMode.Moisture:
                    return state.WaterDynamics?.Moisture?.Length == state.CellCount;
                case TerrainDataOverlayMode.Erodibility:
                    return state.WaterDynamics?.Erodibility?.Length == state.CellCount;
                case TerrainDataOverlayMode.LocalSlope:
                    return state.WaterDynamics?.LocalSlope?.Length == state.CellCount;
                case TerrainDataOverlayMode.LocalAspect:
                    return state.WaterDynamics?.LocalAspect?.Length == state.CellCount;
                default:
                    return true;
            }
        }

        private static Color32 GetColor(
            int index,
            TerrainWorldState state,
            TerrainDataOverlayMode mode)
        {
            if (state.Elevation[index] == TerrainElevationEncoding.NoData)
            {
                return new Color32(0, 0, 0, 0);
            }

            switch (mode)
            {
                case TerrainDataOverlayMode.Landform:
                    return GetLandformColor(state.Landform[index]);
                case TerrainDataOverlayMode.Material:
                    return GetMaterialColor(state.Material[index]);
                case TerrainDataOverlayMode.Contours:
                    return GetContourColor(
                        index % state.Width,
                        index / state.Width,
                        state.Width,
                        state.Height,
                        state.Elevation);
                case TerrainDataOverlayMode.ManagedWater:
                    return GetManagedWaterColor(state.WaterDynamics.ManagedMask[index]);
                case TerrainDataOverlayMode.WaterStorage:
                    return GetWaterStorageColor(state.WaterDynamics.WaterStorage[index]);
                case TerrainDataOverlayMode.HydroFeature:
                    return GetHydroFeatureColor(state.WaterDynamics.HydroFeature[index]);
                case TerrainDataOverlayMode.Moisture:
                    return GetMoistureColor(state.WaterDynamics.Moisture[index]);
                case TerrainDataOverlayMode.Erodibility:
                    return GetErodibilityColor(state.WaterDynamics.Erodibility[index]);
                case TerrainDataOverlayMode.LocalSlope:
                    return GetLocalSlopeColor(state.WaterDynamics.LocalSlope[index]);
                case TerrainDataOverlayMode.LocalAspect:
                    return GetLocalAspectColor(state.WaterDynamics.LocalAspect[index]);
                default:
                    return new Color32(0, 0, 0, 0);
            }
        }

        private static void CheckContourNeighbor(
            int x,
            int y,
            int width,
            int height,
            short center,
            short[] elevation,
            ref bool minor,
            ref bool major,
            ref bool sea)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
            {
                return;
            }

            short neighbor = elevation[y * width + x];
            if (neighbor == TerrainElevationEncoding.NoData)
            {
                return;
            }

            sea |= center < 0 && neighbor >= 0 || center >= 0 && neighbor < 0;
            major |= FloorDivide(center, MajorContourIntervalMetres) !=
                     FloorDivide(neighbor, MajorContourIntervalMetres);
            minor |= FloorDivide(center, ContourIntervalMetres) !=
                     FloorDivide(neighbor, ContourIntervalMetres);
        }

        private static int FloorDivide(int value, int divisor)
        {
            int quotient = value / divisor;
            return value < 0 && value % divisor != 0 ? quotient - 1 : quotient;
        }

        private static Color32 Lerp(Color32 from, Color32 to, float amount)
        {
            amount = Mathf.Clamp01(amount);
            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(from.r, to.r, amount)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(from.g, to.g, amount)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(from.b, to.b, amount)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(from.a, to.a, amount)));
        }

        private void CreateChunk(
            int startX,
            int startY,
            int width,
            int height,
            TerrainWorldState state,
            TerrainDataOverlayMode mode)
        {
            Color32[] pixels = new Color32[checked(width * height)];
            bool visible = false;
            for (int localY = 0; localY < height; localY++)
            {
                int sourceOffset = (startY + localY) * state.Width + startX;
                int pixelOffset = localY * width;
                for (int localX = 0; localX < width; localX++)
                {
                    Color32 color = GetColor(sourceOffset + localX, state, mode);
                    pixels[pixelOffset + localX] = color;
                    visible |= color.a != 0;
                }
            }

            if (!visible)
            {
                return;
            }

            Texture2D texture = new Texture2D(
                width,
                height,
                TextureFormat.RGBA32,
                false,
                true)
            {
                name = string.Format(
                    "TerrainLabData_{0}_{1}_{2}",
                    mode,
                    startX,
                    startY),
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, width, height),
                Vector2.zero,
                1f);
            sprite.name = texture.name;

            GameObject chunkObject = new GameObject(
                sprite.name,
                typeof(SpriteRenderer));
            chunkObject.transform.SetParent(transform, false);
            chunkObject.transform.position = new Vector3(
                startX - 0.5f,
                startY - 0.5f,
                -1f);
            SpriteRenderer renderer = chunkObject.GetComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            TerrainOverlayRendering.Configure(chunkObject, renderer, SortingOrder);

            int chunkX = startX / ChunkSize;
            int chunkY = startY / ChunkSize;
            _chunks.Add(
                chunkY * _chunkColumns + chunkX,
                new OverlayChunk
                {
                    StartX = startX,
                    StartY = startY,
                    Width = width,
                    Height = height,
                    Pixels = pixels,
                    Texture = texture,
                    Sprite = sprite,
                    GameObject = chunkObject
                });
        }

        private void OnDestroy()
        {
            Clear();
        }
    }
}
