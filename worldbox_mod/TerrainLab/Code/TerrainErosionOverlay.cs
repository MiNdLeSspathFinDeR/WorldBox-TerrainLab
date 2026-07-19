using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerrainLab
{
    public enum TerrainErosionOverlayMode
    {
        None,
        NetChange,
        Erosion,
        Deposition,
        ResultElevation
    }

    public sealed class TerrainErosionOverlay : MonoBehaviour
    {
        private const int ChunkSize = 256;
        private const int SortingOrder = 32610;

        private readonly List<GameObject> _chunks = new List<GameObject>();
        private readonly List<Texture2D> _textures = new List<Texture2D>();
        private readonly List<Sprite> _sprites = new List<Sprite>();
        private int _minimumResultElevation;
        private int _maximumResultElevation;

        public TerrainErosionOverlayMode Mode { get; private set; }

        public void Show(
            TerrainErosionOverlayMode mode,
            TerrainWorldState state,
            TerrainErosionResult result)
        {
            Clear();
            if (mode == TerrainErosionOverlayMode.None || state == null ||
                result == null || !result.IsCurrent(state))
            {
                return;
            }

            _minimumResultElevation = int.MaxValue;
            _maximumResultElevation = int.MinValue;
            foreach (short elevation in result.ResultElevation)
            {
                if (elevation == TerrainElevationEncoding.NoData)
                {
                    continue;
                }

                _minimumResultElevation = Math.Min(_minimumResultElevation, elevation);
                _maximumResultElevation = Math.Max(_maximumResultElevation, elevation);
            }

            if (_minimumResultElevation == int.MaxValue)
            {
                _minimumResultElevation = 0;
                _maximumResultElevation = 1;
            }

            Mode = mode;
            for (int startY = 0; startY < state.Height; startY += ChunkSize)
            {
                int chunkHeight = Math.Min(ChunkSize, state.Height - startY);
                for (int startX = 0; startX < state.Width; startX += ChunkSize)
                {
                    int chunkWidth = Math.Min(ChunkSize, state.Width - startX);
                    CreateChunk(startX, startY, chunkWidth, chunkHeight, state, result, mode);
                }
            }
        }

        public void Clear()
        {
            Mode = TerrainErosionOverlayMode.None;
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
            TerrainErosionResult result,
            TerrainErosionOverlayMode mode)
        {
            Color32[] pixels = new Color32[checked(width * height)];
            bool visible = false;
            for (int localY = 0; localY < height; localY++)
            {
                int sourceOffset = (startY + localY) * state.Width + startX;
                int pixelOffset = localY * width;
                for (int localX = 0; localX < width; localX++)
                {
                    int index = sourceOffset + localX;
                    Color32 color = GetColor(index, state, result, mode);
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
                name = string.Format("TerrainLabErosion_{0}_{1}_{2}", mode, startX, startY),
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

        private Color32 GetColor(
            int index,
            TerrainWorldState state,
            TerrainErosionResult result,
            TerrainErosionOverlayMode mode)
        {
            if (state.Elevation[index] == TerrainElevationEncoding.NoData)
            {
                return new Color32(0, 0, 0, 0);
            }

            int change = result.NetChange[index];
            switch (mode)
            {
                case TerrainErosionOverlayMode.NetChange:
                    return GetNetChangeColor(
                        change,
                        result.Statistics.MaximumCut,
                        result.Statistics.MaximumFill);
                case TerrainErosionOverlayMode.Erosion:
                    return GetErosionColor(
                        change,
                        result.Statistics.MaximumCut);
                case TerrainErosionOverlayMode.Deposition:
                    return GetDepositionColor(
                        change,
                        result.Statistics.MaximumFill);
                case TerrainErosionOverlayMode.ResultElevation:
                    return GetResultElevationColor(
                        result.ResultElevation[index],
                        _minimumResultElevation,
                        _maximumResultElevation);
                default:
                    return new Color32(0, 0, 0, 0);
            }
        }

        public static Color32 GetNetChangeColor(
            int change,
            int maximumCut,
            int maximumFill)
        {
            if (change < 0)
            {
                return Ramp(
                    new Color32(245, 190, 60, 105),
                    new Color32(205, 40, 45, 235),
                    Ratio(-change, maximumCut));
            }

            if (change > 0)
            {
                return Ramp(
                    new Color32(80, 210, 175, 105),
                    new Color32(40, 95, 225, 235),
                    Ratio(change, maximumFill));
            }

            return new Color32(0, 0, 0, 0);
        }

        public static Color32 GetErosionColor(int change, int maximumCut)
        {
            return change < 0
                ? Ramp(
                    new Color32(245, 190, 60, 115),
                    new Color32(205, 40, 45, 235),
                    Ratio(-change, maximumCut))
                : new Color32(0, 0, 0, 0);
        }

        public static Color32 GetDepositionColor(int change, int maximumFill)
        {
            return change > 0
                ? Ramp(
                    new Color32(100, 220, 155, 115),
                    new Color32(35, 80, 225, 235),
                    Ratio(change, maximumFill))
                : new Color32(0, 0, 0, 0);
        }

        public static Color32 GetResultElevationColor(
            short elevation,
            int minimum,
            int maximum)
        {
            float normalized = Mathf.InverseLerp(
                minimum,
                maximum,
                elevation);
            return Ramp(
                new Color32(30, 90, 185, 130),
                new Color32(245, 220, 90, 195),
                normalized);
        }

        private static float Ratio(int value, int maximum)
        {
            return maximum <= 0 ? 0f : Mathf.Clamp01(value / (float)maximum);
        }

        private static Color32 Ramp(Color32 from, Color32 to, float amount)
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
