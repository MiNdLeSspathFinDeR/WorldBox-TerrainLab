using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerrainLab
{
    public enum TerrainReliefOverlayMode
    {
        None,
        Hypsometry,
        Slope,
        Aspect,
        Hillshade,
        Ruggedness
    }

    public sealed class TerrainReliefOverlay : MonoBehaviour
    {
        private const int ChunkSize = 256;
        private const int SortingOrder = 32590;

        private readonly List<GameObject> _chunks = new List<GameObject>();
        private readonly List<Texture2D> _textures = new List<Texture2D>();
        private readonly List<Sprite> _sprites = new List<Sprite>();

        public TerrainReliefOverlayMode Mode { get; private set; }

        public void Show(
            TerrainReliefOverlayMode mode,
            TerrainWorldState state,
            TerrainReliefResult result)
        {
            Clear();
            if (mode == TerrainReliefOverlayMode.None || state == null ||
                result == null || !result.IsCurrent(state))
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
                    CreateChunk(startX, startY, chunkWidth, chunkHeight, state, result, mode);
                }
            }
        }

        public void Clear()
        {
            Mode = TerrainReliefOverlayMode.None;
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
            TerrainReliefResult result,
            TerrainReliefOverlayMode mode)
        {
            Color32[] pixels = new Color32[checked(width * height)];
            bool visible = false;
            for (int localY = 0; localY < height; localY++)
            {
                int sourceOffset = (startY + localY) * state.Width + startX;
                int pixelOffset = localY * width;
                for (int localX = 0; localX < width; localX++)
                {
                    int sourceIndex = sourceOffset + localX;
                    Color32 color = GetColor(sourceIndex, state, result, mode);
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
                name = string.Format("TerrainLabRelief_{0}_{1}_{2}", mode, startX, startY),
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
            TerrainReliefResult result,
            TerrainReliefOverlayMode mode)
        {
            short elevation = state.Elevation[index];
            if (elevation == TerrainElevationEncoding.NoData)
            {
                return new Color32(0, 0, 0, 0);
            }

            switch (mode)
            {
                case TerrainReliefOverlayMode.Hypsometry:
                    return GetHypsometryColor(
                        elevation,
                        state.SeaLevel,
                        result.Statistics);
                case TerrainReliefOverlayMode.Slope:
                    return GetSlopeColor(result.SlopeTenths[index]);
                case TerrainReliefOverlayMode.Aspect:
                    return GetAspectColor(result.AspectTenths[index]);
                case TerrainReliefOverlayMode.Hillshade:
                    return GetHillshadeColor(result.Hillshade[index]);
                case TerrainReliefOverlayMode.Ruggedness:
                    return GetRuggednessColor(
                        result.Ruggedness[index],
                        result.Statistics.MaximumRuggedness);
                default:
                    return new Color32(0, 0, 0, 0);
            }
        }

        public static Color32 GetLegendColor(
            TerrainReliefOverlayMode mode,
            float normalized,
            short seaLevel,
            TerrainReliefStatistics statistics)
        {
            if (statistics == null)
            {
                return new Color32(0, 0, 0, 0);
            }

            normalized = Mathf.Clamp01(normalized);
            switch (mode)
            {
                case TerrainReliefOverlayMode.Hypsometry:
                    short elevation = (short)Mathf.RoundToInt(Mathf.Lerp(
                        statistics.MinimumElevation,
                        statistics.MaximumElevation,
                        normalized));
                    return GetHypsometryColor(
                        elevation,
                        seaLevel,
                        statistics);
                case TerrainReliefOverlayMode.Slope:
                    return GetSlopeColor((ushort)Mathf.RoundToInt(
                        statistics.MaximumSlopeTenths * normalized));
                case TerrainReliefOverlayMode.Aspect:
                    return GetAspectColor((ushort)Mathf.Min(
                        3599,
                        Mathf.RoundToInt(3599f * normalized)));
                case TerrainReliefOverlayMode.Hillshade:
                    return GetHillshadeColor((byte)Mathf.RoundToInt(
                        byte.MaxValue * normalized));
                case TerrainReliefOverlayMode.Ruggedness:
                    return GetRuggednessColor(
                        (ushort)Mathf.RoundToInt(
                            statistics.MaximumRuggedness * normalized),
                        statistics.MaximumRuggedness);
                default:
                    return new Color32(0, 0, 0, 0);
            }
        }

        public static Color32 GetSlopeColor(ushort slopeTenths)
        {
            if (slopeTenths == ushort.MaxValue || slopeTenths < 10)
            {
                return new Color32(0, 0, 0, 0);
            }

            return Ramp(
                new Color32(245, 220, 70, 70),
                new Color32(220, 45, 45, 220),
                Mathf.Clamp01(slopeTenths / 600f));
        }

        public static Color32 GetAspectColor(ushort aspectTenths)
        {
            if (aspectTenths == ushort.MaxValue)
            {
                return new Color32(125, 125, 125, 80);
            }

            return Color.HSVToRGB(
                    aspectTenths / 3600f,
                    0.72f,
                    0.95f,
                    false)
                .WithAlpha(0.65f);
        }

        public static Color32 GetHillshadeColor(byte shade)
        {
            return new Color32(shade, shade, shade, 175);
        }

        public static Color32 GetRuggednessColor(ushort value, ushort maximum)
        {
            if (value == ushort.MaxValue || value == 0)
            {
                return new Color32(0, 0, 0, 0);
            }

            float normalized = maximum == 0
                ? 0f
                : Mathf.Clamp01(value / (float)maximum);
            return normalized < 0.5f
                ? Ramp(
                    new Color32(250, 220, 80, 85),
                    new Color32(230, 105, 45, 185),
                    normalized * 2f)
                : Ramp(
                    new Color32(230, 105, 45, 185),
                    new Color32(110, 35, 45, 230),
                    (normalized - 0.5f) * 2f);
        }

        public static Color32 GetHypsometryColor(
            short elevation,
            short seaLevel,
            TerrainReliefStatistics statistics)
        {
            if (elevation < seaLevel)
            {
                float water = Mathf.InverseLerp(statistics.MinimumElevation, seaLevel, elevation);
                return Ramp(
                    new Color32(20, 55, 155, 175),
                    new Color32(65, 200, 225, 145),
                    water);
            }

            float land = Mathf.InverseLerp(seaLevel, statistics.MaximumElevation, elevation);
            if (land < 0.4f)
            {
                return Ramp(
                    new Color32(55, 165, 75, 125),
                    new Color32(225, 205, 75, 150),
                    land / 0.4f);
            }

            if (land < 0.75f)
            {
                return Ramp(
                    new Color32(225, 205, 75, 150),
                    new Color32(125, 105, 85, 180),
                    (land - 0.4f) / 0.35f);
            }

            return Ramp(
                new Color32(125, 105, 85, 180),
                new Color32(245, 245, 245, 225),
                (land - 0.75f) / 0.25f);
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

    internal static class TerrainColorExtensions
    {
        public static Color32 WithAlpha(this Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }
    }
}
