using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerrainLab
{
    public enum TerrainHydrologyOverlayMode
    {
        None,
        Streams,
        Accumulation,
        FillDepth,
        FilledElevation,
        FlowDirection,
        Watersheds,
        StreamOrder
    }

    public sealed class TerrainHydrologyOverlay : MonoBehaviour
    {
        private const int ChunkSize = 256;
        private const int SortingOrder = 32600;

        private readonly List<GameObject> _chunks = new List<GameObject>();
        private readonly List<Texture2D> _textures = new List<Texture2D>();
        private readonly List<Sprite> _sprites = new List<Sprite>();

        public TerrainHydrologyOverlayMode Mode { get; private set; }

        public void Show(
            TerrainHydrologyOverlayMode mode,
            TerrainWorldState state,
            TerrainHydrologyResult result)
        {
            Clear();
            if (mode == TerrainHydrologyOverlayMode.None ||
                state == null || result == null || !result.IsCurrent(state))
            {
                return;
            }

            Mode = mode;
            for (int startY = 0; startY < state.Height; startY += ChunkSize)
            {
                int chunkHeight = Math.Min(ChunkSize, state.Height - startY);
                for (int startX = 0; startX < state.Width; startX += ChunkSize)
                {
                    int chunkWidth = Math.Min(ChunkSize, state.Width - startX);
                    CreateChunk(
                        startX,
                        startY,
                        chunkWidth,
                        chunkHeight,
                        state,
                        result,
                        mode);
                }
            }
        }

        public void Clear()
        {
            Mode = TerrainHydrologyOverlayMode.None;
            foreach (GameObject chunk in _chunks)
            {
                if (chunk != null)
                {
                    Destroy(chunk);
                }
            }

            foreach (Sprite sprite in _sprites)
            {
                if (sprite != null)
                {
                    Destroy(sprite);
                }
            }

            foreach (Texture2D texture in _textures)
            {
                if (texture != null)
                {
                    Destroy(texture);
                }
            }

            _chunks.Clear();
            _sprites.Clear();
            _textures.Clear();
        }

        private void CreateChunk(
            int startX,
            int startY,
            int width,
            int height,
            TerrainWorldState state,
            TerrainHydrologyResult result,
            TerrainHydrologyOverlayMode mode)
        {
            Color32[] pixels = new Color32[checked(width * height)];
            bool hasVisiblePixels = false;
            for (int localY = 0; localY < height; localY++)
            {
                int sourceOffset = (startY + localY) * state.Width + startX;
                int pixelOffset = localY * width;
                for (int localX = 0; localX < width; localX++)
                {
                    int sourceIndex = sourceOffset + localX;
                    Color32 color = GetColor(sourceIndex, state, result, mode);
                    pixels[pixelOffset + localX] = color;
                    hasVisiblePixels |= color.a != 0;
                }
            }

            if (!hasVisiblePixels)
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
                name = string.Format("TerrainLabHydrology_{0}_{1}_{2}", mode, startX, startY),
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, width, height),
                Vector2.zero,
                1f);
            sprite.name = texture.name;

            GameObject chunk = new GameObject(sprite.name, typeof(SpriteRenderer));
            chunk.transform.SetParent(transform, false);
            chunk.transform.position = new Vector3(startX - 0.5f, startY - 0.5f, -1f);
            SpriteRenderer renderer = chunk.GetComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            TerrainOverlayRendering.Configure(chunk, renderer, SortingOrder);

            _textures.Add(texture);
            _sprites.Add(sprite);
            _chunks.Add(chunk);
        }

        private static Color32 GetColor(
            int index,
            TerrainWorldState state,
            TerrainHydrologyResult result,
            TerrainHydrologyOverlayMode mode)
        {
            if (state.Elevation[index] == TerrainElevationEncoding.NoData)
            {
                return new Color32(0, 0, 0, 0);
            }

            switch (mode)
            {
                case TerrainHydrologyOverlayMode.Streams:
                    if (result.StreamMask[index] != 1)
                    {
                        return new Color32(0, 0, 0, 0);
                    }

                    return GetStreamColor(
                        result.FlowAccumulation[index],
                        result.Statistics.MaximumAccumulation);

                case TerrainHydrologyOverlayMode.Accumulation:
                    return GetAccumulationColor(
                        result.FlowAccumulation[index],
                        result.Statistics.MaximumAccumulation);

                case TerrainHydrologyOverlayMode.FillDepth:
                    int depth = (int)result.FilledElevation[index] - state.Elevation[index];
                    return GetFillDepthColor(
                        depth,
                        result.Statistics.MaximumFillDepth);

                case TerrainHydrologyOverlayMode.FilledElevation:
                    return TerrainElevationOverlay.GetColor(
                        result.FilledElevation[index],
                        state.SeaLevel,
                        TerrainElevationEncoding.Minimum,
                        TerrainElevationEncoding.Maximum);

                case TerrainHydrologyOverlayMode.FlowDirection:
                    return GetFlowDirectionColor(result.FlowDirection[index]);

                case TerrainHydrologyOverlayMode.Watersheds:
                    return GetWatershedColor(result.Watershed[index]);

                case TerrainHydrologyOverlayMode.StreamOrder:
                    byte order = result.StreamOrder[index];
                    if (order == 0 || order == byte.MaxValue ||
                        result.StreamMask[index] != 1)
                    {
                        return new Color32(0, 0, 0, 0);
                    }

                    return GetStreamOrderColor(
                        order,
                        result.Statistics.MaximumStreamOrder);

                default:
                    return new Color32(0, 0, 0, 0);
            }
        }

        public static Color32 GetLegendColor(
            TerrainHydrologyOverlayMode mode,
            float normalized,
            TerrainWorldState state,
            TerrainHydrologyResult result)
        {
            if (state == null || result?.Statistics == null)
            {
                return new Color32(0, 0, 0, 0);
            }

            normalized = Mathf.Clamp01(normalized);
            TerrainHydrologyStatistics statistics = result.Statistics;
            switch (mode)
            {
                case TerrainHydrologyOverlayMode.Streams:
                    uint streamMinimum = (uint)Math.Max(
                        1,
                        result.StreamThreshold);
                    uint streamMaximum = Math.Max(
                        streamMinimum,
                        statistics.MaximumAccumulation);
                    uint streamValue = (uint)Mathf.RoundToInt(Mathf.Lerp(
                        streamMinimum,
                        streamMaximum,
                        normalized));
                    return GetStreamColor(streamValue, streamMaximum);
                case TerrainHydrologyOverlayMode.Accumulation:
                    uint accumulationMaximum = Math.Max(
                        1u,
                        statistics.MaximumAccumulation);
                    return GetAccumulationColor(
                        (uint)Mathf.RoundToInt(
                            accumulationMaximum * normalized),
                        accumulationMaximum);
                case TerrainHydrologyOverlayMode.FillDepth:
                    int fillMaximum = Math.Max(
                        1,
                        statistics.MaximumFillDepth);
                    return GetFillDepthColor(
                        Mathf.RoundToInt(fillMaximum * normalized),
                        fillMaximum);
                case TerrainHydrologyOverlayMode.FilledElevation:
                    short elevation = (short)Mathf.RoundToInt(Mathf.Lerp(
                        TerrainElevationEncoding.Minimum,
                        TerrainElevationEncoding.Maximum,
                        normalized));
                    return TerrainElevationOverlay.GetColor(
                        elevation,
                        state.SeaLevel,
                        TerrainElevationEncoding.Minimum,
                        TerrainElevationEncoding.Maximum);
                case TerrainHydrologyOverlayMode.StreamOrder:
                    byte maximumOrder = Math.Max(
                        (byte)1,
                        statistics.MaximumStreamOrder);
                    byte order = (byte)Mathf.RoundToInt(Mathf.Lerp(
                        1f,
                        maximumOrder,
                        normalized));
                    return GetStreamOrderColor(order, maximumOrder);
                default:
                    return new Color32(0, 0, 0, 0);
            }
        }

        public static Color32 GetStreamColor(uint value, uint maximum)
        {
            float streamStrength = LogRatio(value, maximum);
            return Lerp(
                new Color32(70, 210, 255, 175),
                new Color32(15, 85, 235, 235),
                streamStrength);
        }

        public static Color32 GetAccumulationColor(uint value, uint maximum)
        {
            float accumulation = LogRatio(value, maximum);
            return accumulation < 0.08f
                ? new Color32(0, 0, 0, 0)
                : Lerp(
                    new Color32(100, 220, 195, 35),
                    new Color32(25, 75, 230, 190),
                    accumulation);
        }

        public static Color32 GetFillDepthColor(int depth, int maximumDepth)
        {
            if (depth <= 0)
            {
                return new Color32(0, 0, 0, 0);
            }

            float fill = maximumDepth <= 0
                ? 0f
                : Mathf.Clamp01(depth / (float)maximumDepth);
            return Lerp(
                new Color32(255, 220, 70, 115),
                new Color32(225, 55, 45, 215),
                fill);
        }

        public static Color32 GetWatershedColor(uint watershed)
        {
            if (watershed == 0)
            {
                return new Color32(0, 0, 0, 0);
            }

            uint hash = watershed * 2654435761u;
            Color watershedColor = Color.HSVToRGB(
                (hash & 0xffffu) / 65535f,
                0.58f,
                0.95f,
                false);
            watershedColor.a = 0.42f;
            return watershedColor;
        }

        public static Color32 GetStreamOrderColor(byte order, byte maximumOrder)
        {
            if (order == 0 || order == byte.MaxValue)
            {
                return new Color32(0, 0, 0, 0);
            }

            float orderRatio = maximumOrder <= 1
                ? 0f
                : (order - 1f) / (maximumOrder - 1f);
            return orderRatio < 0.5f
                ? Lerp(
                    new Color32(80, 225, 245, 175),
                    new Color32(45, 105, 235, 225),
                    orderRatio * 2f)
                : Lerp(
                    new Color32(45, 105, 235, 225),
                    new Color32(225, 65, 180, 245),
                    (orderRatio - 0.5f) * 2f);
        }

        public static Color32 GetFlowDirectionColor(byte value)
        {
            if (value == byte.MaxValue || value > (byte)TerrainFlowDirection.SouthEast)
            {
                return new Color32(0, 0, 0, 0);
            }

            Color color = Color.HSVToRGB(value / 8f, 0.78f, 0.98f, false);
            color.a = 0.72f;
            return color;
        }

        private static float LogRatio(uint value, uint maximum)
        {
            if (value == 0 || maximum <= 1)
            {
                return 0f;
            }

            return Mathf.Clamp01(
                Mathf.Log(1f + value) /
                Mathf.Log(1f + maximum));
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

        private void OnDestroy()
        {
            Clear();
        }
    }
}
