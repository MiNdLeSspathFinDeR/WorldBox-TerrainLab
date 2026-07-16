using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerrainLab
{
    public sealed class TerrainElevationOverlay : MonoBehaviour
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

        private const int ChunkSize = 256;
        private const int SortingOrder = 32590;
        private const byte OverlayAlpha = 156;
        private const float NegativeRampStart = 0.08f;
        private const float NegativeRampEnd = 0.30f;
        private const float PositiveRampStart = 0.60f;
        private const float PositiveRampEnd = 0.92f;

        private readonly Dictionary<int, OverlayChunk> _chunks =
            new Dictionary<int, OverlayChunk>();
        private TerrainWorldState _sourceState;
        private int _chunkColumns;

        public bool IsVisible { get; private set; }

        public short DisplayMinimum { get; private set; }

        public short DisplayMaximum { get; private set; }

        public short SeaLevel { get; private set; }

        public bool References(TerrainWorldState state)
        {
            return IsVisible && ReferenceEquals(_sourceState, state);
        }

        public void Show(TerrainWorldState state)
        {
            Clear();
            if (state?.Elevation == null || state.Width <= 0 || state.Height <= 0 ||
                state.Elevation.Length != checked(state.Width * state.Height) ||
                !TryGetRange(state, out short minimum, out short maximum))
            {
                return;
            }

            _sourceState = state;
            DisplayMinimum = minimum;
            DisplayMaximum = maximum;
            SeaLevel = state.SeaLevel;
            _chunkColumns = (state.Width + ChunkSize - 1) / ChunkSize;

            for (int startY = 0; startY < state.Height; startY += ChunkSize)
            {
                int chunkHeight = Math.Min(ChunkSize, state.Height - startY);
                for (int startX = 0; startX < state.Width; startX += ChunkSize)
                {
                    int chunkWidth = Math.Min(ChunkSize, state.Width - startX);
                    CreateChunk(startX, startY, chunkWidth, chunkHeight, state);
                }
            }

            IsVisible = _chunks.Count > 0;
            if (!IsVisible)
            {
                _sourceState = null;
            }
        }

        public void UpdateCells(TerrainWorldState state, TerrainElevationEdit edit)
        {
            if (!References(state) || edit == null || edit.ChangedCellCount == 0)
            {
                return;
            }

            bool rangeExpanded = false;
            for (int offset = 0; offset < edit.Indices.Length; offset++)
            {
                int index = edit.Indices[offset];
                if (index < 0 || index >= state.Elevation.Length)
                {
                    continue;
                }

                short elevation = state.Elevation[index];
                if (elevation == TerrainElevationEncoding.NoData)
                {
                    continue;
                }

                if (elevation < DisplayMinimum || elevation > DisplayMaximum)
                {
                    rangeExpanded = true;
                    break;
                }
            }

            if (rangeExpanded)
            {
                Show(state);
                return;
            }

            HashSet<OverlayChunk> dirtyChunks = new HashSet<OverlayChunk>();
            for (int offset = 0; offset < edit.Indices.Length; offset++)
            {
                int index = edit.Indices[offset];
                if (index < 0 || index >= state.Elevation.Length)
                {
                    continue;
                }

                int x = index % state.Width;
                int y = index / state.Width;
                int chunkX = x / ChunkSize;
                int chunkY = y / ChunkSize;
                int key = chunkY * _chunkColumns + chunkX;
                if (!_chunks.TryGetValue(key, out OverlayChunk chunk))
                {
                    continue;
                }

                int localX = x - chunk.StartX;
                int localY = y - chunk.StartY;
                chunk.Pixels[localY * chunk.Width + localX] = GetColor(
                    state.Elevation[index],
                    SeaLevel,
                    DisplayMinimum,
                    DisplayMaximum);
                dirtyChunks.Add(chunk);
            }

            foreach (OverlayChunk chunk in dirtyChunks)
            {
                chunk.Texture.SetPixels32(chunk.Pixels);
                chunk.Texture.Apply(false, false);
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
            IsVisible = false;
        }

        public static Color32 GetColor(
            short elevation,
            short seaLevel,
            short minimum,
            short maximum)
        {
            if (elevation == TerrainElevationEncoding.NoData)
            {
                return new Color32(0, 0, 0, 0);
            }

            float rampPosition;
            if (elevation < seaLevel)
            {
                short negativeMaximum = maximum < seaLevel
                    ? maximum
                    : seaLevel;
                float normalized = minimum >= negativeMaximum
                    ? 1f
                    : Mathf.InverseLerp(minimum, negativeMaximum, elevation);
                rampPosition = Mathf.Lerp(
                    NegativeRampStart,
                    NegativeRampEnd,
                    normalized);
            }
            else
            {
                short positiveMinimum = minimum > seaLevel
                    ? minimum
                    : seaLevel;
                float normalized = maximum <= positiveMinimum
                    ? 0f
                    : Mathf.InverseLerp(positiveMinimum, maximum, elevation);
                rampPosition = Mathf.Lerp(
                    PositiveRampStart,
                    PositiveRampEnd,
                    normalized);
            }

            Color color = Turbo(rampPosition);
            return new Color32(
                (byte)Mathf.RoundToInt(color.r * 255f),
                (byte)Mathf.RoundToInt(color.g * 255f),
                (byte)Mathf.RoundToInt(color.b * 255f),
                OverlayAlpha);
        }

        public static Color Turbo(float position)
        {
            float x = Mathf.Clamp01(position);
            float red = 0.13572138f + x * (4.61539260f + x *
                (-42.66032258f + x * (132.13108234f + x *
                    (-152.94239396f + x * 59.28637943f))));
            float green = 0.09140261f + x * (2.19418839f + x *
                (4.84296658f + x * (-14.18503333f + x *
                    (4.27729857f + x * 2.82956604f))));
            float blue = 0.10667330f + x * (12.64194608f + x *
                (-60.58204836f + x * (110.36276771f + x *
                    (-89.90310912f + x * 27.34824973f))));
            return new Color(
                Mathf.Clamp01(red),
                Mathf.Clamp01(green),
                Mathf.Clamp01(blue),
                1f);
        }

        private static bool TryGetRange(
            TerrainWorldState state,
            out short minimum,
            out short maximum)
        {
            minimum = short.MaxValue;
            maximum = short.MinValue;
            bool found = false;
            for (int index = 0; index < state.Elevation.Length; index++)
            {
                short elevation = state.Elevation[index];
                if (elevation == TerrainElevationEncoding.NoData)
                {
                    continue;
                }

                minimum = Math.Min(minimum, elevation);
                maximum = Math.Max(maximum, elevation);
                found = true;
            }

            return found;
        }

        private void CreateChunk(
            int startX,
            int startY,
            int width,
            int height,
            TerrainWorldState state)
        {
            Color32[] pixels = new Color32[checked(width * height)];
            bool visible = false;
            for (int localY = 0; localY < height; localY++)
            {
                int sourceOffset = (startY + localY) * state.Width + startX;
                int pixelOffset = localY * width;
                for (int localX = 0; localX < width; localX++)
                {
                    Color32 color = GetColor(
                        state.Elevation[sourceOffset + localX],
                        SeaLevel,
                        DisplayMinimum,
                        DisplayMaximum);
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
                    "TerrainLabElevation_{0}_{1}",
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
            renderer.sortingOrder = SortingOrder;

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
