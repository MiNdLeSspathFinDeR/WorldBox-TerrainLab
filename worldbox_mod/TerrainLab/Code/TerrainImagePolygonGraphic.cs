using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TerrainLab
{
    internal sealed class TerrainImagePolygonGraphic : MaskableGraphic
    {
        private readonly List<TerrainImageClassificationVertex> _vertices =
            new List<TerrainImageClassificationVertex>();

        private int _sourceWidth = 1;
        private int _sourceHeight = 1;
        private bool _closed;
        private bool _showVertices;
        private float _inverseZoom = 1f;
        private float _lineThickness = 2.2f;
        private Color _fillColor = new Color(1f, 1f, 1f, 0.18f);
        private Color _lineColor = Color.white;
        private Color _vertexColor = Color.white;
        private Texture2D _surfacePattern;

        public override Texture mainTexture =>
            _surfacePattern == null
                ? Texture2D.whiteTexture
                : _surfacePattern;

        public void Configure(
            IEnumerable<TerrainImageClassificationVertex> vertices,
            int sourceWidth,
            int sourceHeight,
            string surface,
            Color surfaceColor,
            Color vertexColor,
            bool closed,
            bool showVertices,
            float inverseZoom,
            float closedFillAlpha = 0.22f,
            float lineThickness = 2.2f)
        {
            _vertices.Clear();
            if (vertices != null)
            {
                foreach (TerrainImageClassificationVertex vertex in vertices)
                {
                    if (vertex != null)
                    {
                        _vertices.Add(vertex);
                    }
                }
            }

            _sourceWidth = Math.Max(1, sourceWidth);
            _sourceHeight = Math.Max(1, sourceHeight);
            _closed = closed;
            _showVertices = showVertices;
            _inverseZoom = Mathf.Max(0.01f, inverseZoom);
            _lineThickness = Mathf.Max(1f, lineThickness);
            _surfacePattern =
                TerrainImageMorphotypePatterns.Get(surface, surfaceColor);
            _fillColor = new Color(
                _surfacePattern == null ? surfaceColor.r : 1f,
                _surfacePattern == null ? surfaceColor.g : 1f,
                _surfacePattern == null ? surfaceColor.b : 1f,
                closed ? Mathf.Clamp01(closedFillAlpha) : 0.08f);
            _lineColor = new Color(
                surfaceColor.r,
                surfaceColor.g,
                surfaceColor.b,
                0.98f);
            _vertexColor = new Color(
                vertexColor.r,
                vertexColor.g,
                vertexColor.b,
                1f);
            raycastTarget = false;
            SetVerticesDirty();
            SetMaterialDirty();
        }

        public void SetInverseZoom(float inverseZoom)
        {
            float next = Mathf.Max(0.01f, inverseZoom);
            if (Mathf.Approximately(next, _inverseZoom))
            {
                return;
            }
            _inverseZoom = next;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper helper)
        {
            helper.Clear();
            if (_vertices.Count == 0)
            {
                return;
            }

            Rect rect = rectTransform.rect;
            List<Vector2> points = new List<Vector2>(_vertices.Count);
            foreach (TerrainImageClassificationVertex vertex in _vertices)
            {
                float normalizedX = (vertex.X + 0.5f) / _sourceWidth;
                float normalizedY = (vertex.Y + 0.5f) / _sourceHeight;
                points.Add(
                    new Vector2(
                        Mathf.Lerp(rect.xMin, rect.xMax, normalizedX),
                        Mathf.Lerp(rect.yMax, rect.yMin, normalizedY)));
            }

            if (_closed && points.Count >= 3)
            {
                AddFill(helper, points, rect);
            }

            int segmentCount = _closed
                ? points.Count
                : Math.Max(0, points.Count - 1);
            float thickness = _lineThickness * _inverseZoom;
            for (int index = 0; index < segmentCount; index++)
            {
                AddLine(
                    helper,
                    points[index],
                    points[(index + 1) % points.Count],
                    thickness,
                    _lineColor);
            }

            if (_showVertices)
            {
                float radius = 3.6f * _inverseZoom;
                foreach (Vector2 point in points)
                {
                    AddDiamond(helper, point, radius, _vertexColor);
                }
            }
        }

        private void AddFill(
            VertexHelper helper,
            IReadOnlyList<Vector2> points,
            Rect rect)
        {
            List<int> triangles = Triangulate(points);
            for (int index = 0; index + 2 < triangles.Count; index += 3)
            {
                AddTexturedTriangle(
                    helper,
                    points[triangles[index]],
                    points[triangles[index + 1]],
                    points[triangles[index + 2]],
                    _fillColor,
                    rect);
            }
        }

        private static List<int> Triangulate(IReadOnlyList<Vector2> points)
        {
            List<int> remaining = new List<int>(points.Count);
            if (SignedArea(points) > 0f)
            {
                for (int index = 0; index < points.Count; index++)
                {
                    remaining.Add(index);
                }
            }
            else
            {
                for (int index = points.Count - 1; index >= 0; index--)
                {
                    remaining.Add(index);
                }
            }

            List<int> triangles = new List<int>((points.Count - 2) * 3);
            int safety = points.Count * points.Count;
            while (remaining.Count > 2 && safety-- > 0)
            {
                bool removedEar = false;
                for (int index = 0; index < remaining.Count; index++)
                {
                    int previous =
                        remaining[(index + remaining.Count - 1) %
                                  remaining.Count];
                    int current = remaining[index];
                    int next = remaining[(index + 1) % remaining.Count];
                    if (Cross(
                            points[previous],
                            points[current],
                            points[next]) <= 0.0001f)
                    {
                        continue;
                    }

                    bool containsPoint = false;
                    foreach (int candidate in remaining)
                    {
                        if (candidate == previous ||
                            candidate == current ||
                            candidate == next)
                        {
                            continue;
                        }
                        if (PointInTriangle(
                                points[candidate],
                                points[previous],
                                points[current],
                                points[next]))
                        {
                            containsPoint = true;
                            break;
                        }
                    }
                    if (containsPoint)
                    {
                        continue;
                    }

                    triangles.Add(previous);
                    triangles.Add(current);
                    triangles.Add(next);
                    remaining.RemoveAt(index);
                    removedEar = true;
                    break;
                }
                if (!removedEar)
                {
                    break;
                }
            }
            return triangles;
        }

        private static float SignedArea(IReadOnlyList<Vector2> points)
        {
            float area = 0f;
            Vector2 previous = points[points.Count - 1];
            foreach (Vector2 point in points)
            {
                area += previous.x * point.y - point.x * previous.y;
                previous = point;
            }
            return area * 0.5f;
        }

        private static bool PointInTriangle(
            Vector2 point,
            Vector2 first,
            Vector2 second,
            Vector2 third)
        {
            float firstCross = Cross(first, second, point);
            float secondCross = Cross(second, third, point);
            float thirdCross = Cross(third, first, point);
            const float tolerance = -0.0001f;
            return firstCross >= tolerance &&
                   secondCross >= tolerance &&
                   thirdCross >= tolerance;
        }

        private static float Cross(Vector2 first, Vector2 second, Vector2 third)
        {
            return (second.x - first.x) * (third.y - first.y) -
                   (second.y - first.y) * (third.x - first.x);
        }

        private static void AddTexturedTriangle(
            VertexHelper helper,
            Vector2 first,
            Vector2 second,
            Vector2 third,
            Color color,
            Rect rect)
        {
            int offset = helper.currentVertCount;
            helper.AddVert(first, color, PatternUv(first, rect));
            helper.AddVert(second, color, PatternUv(second, rect));
            helper.AddVert(third, color, PatternUv(third, rect));
            helper.AddTriangle(offset, offset + 1, offset + 2);
        }

        private static Vector2 PatternUv(Vector2 point, Rect rect)
        {
            const float patternSize = 18f;
            return new Vector2(
                (point.x - rect.xMin) / patternSize,
                (point.y - rect.yMin) / patternSize);
        }

        private static void AddLine(
            VertexHelper helper,
            Vector2 start,
            Vector2 end,
            float thickness,
            Color color)
        {
            Vector2 delta = end - start;
            if (delta.sqrMagnitude < 0.0001f)
            {
                return;
            }
            Vector2 perpendicular =
                new Vector2(-delta.y, delta.x).normalized * thickness * 0.5f;
            AddQuad(
                helper,
                start - perpendicular,
                start + perpendicular,
                end + perpendicular,
                end - perpendicular,
                color);
        }

        private static void AddDiamond(
            VertexHelper helper,
            Vector2 center,
            float radius,
            Color color)
        {
            AddQuad(
                helper,
                center + new Vector2(0f, radius),
                center + new Vector2(radius, 0f),
                center + new Vector2(0f, -radius),
                center + new Vector2(-radius, 0f),
                color);
        }

        private static void AddQuad(
            VertexHelper helper,
            Vector2 first,
            Vector2 second,
            Vector2 third,
            Vector2 fourth,
            Color color)
        {
            int offset = helper.currentVertCount;
            helper.AddVert(first, color, Vector2.zero);
            helper.AddVert(second, color, Vector2.zero);
            helper.AddVert(third, color, Vector2.zero);
            helper.AddVert(fourth, color, Vector2.zero);
            helper.AddTriangle(offset, offset + 1, offset + 2);
            helper.AddTriangle(offset, offset + 2, offset + 3);
        }
    }

    internal static class TerrainImageMorphotypePatterns
    {
        private const int PatternSize = 16;
        private static readonly Dictionary<string, Texture2D> Patterns =
            new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        public static Texture2D Get(string surface, Color baseColor)
        {
            if (string.IsNullOrWhiteSpace(surface) ||
                surface == "map_boundary" ||
                surface == "draft")
            {
                return null;
            }
            if (Patterns.TryGetValue(surface, out Texture2D pattern) &&
                pattern != null)
            {
                return pattern;
            }

            pattern = Create(surface, baseColor);
            Patterns[surface] = pattern;
            return pattern;
        }

        private static Texture2D Create(string surface, Color baseColor)
        {
            Texture2D texture = new Texture2D(
                PatternSize,
                PatternSize,
                TextureFormat.RGBA32,
                false)
            {
                name = "TerrainLabMorphotypePattern_" + surface,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };
            Color32 normal = Shade(baseColor, 0.92f);
            Color32 light = Shade(baseColor, 1.22f);
            Color32 dark = Shade(baseColor, 0.58f);
            Color32[] pixels = new Color32[PatternSize * PatternSize];
            for (int y = 0; y < PatternSize; y++)
            {
                for (int x = 0; x < PatternSize; x++)
                {
                    bool first;
                    bool second;
                    switch (surface)
                    {
                        case "deep_ocean":
                        case "shelf":
                        case "shallow_water":
                        case "river_lake":
                            first = y % 5 == 1 && (x + y) % 4 != 0;
                            second = y % 5 == 2 && (x + y) % 4 == 0;
                            break;
                        case "sand":
                            first = (x * 7 + y * 11) % 19 == 0;
                            second = (x * 5 + y * 3) % 23 == 0;
                            break;
                        case "plain":
                        case "lowland":
                            first = y % 7 == 5 && x % 6 == 2;
                            second = y % 7 == 4 && x % 6 == 2;
                            break;
                        case "upland":
                        case "hills":
                            first = (x + y) % 8 == 0;
                            second = (x - y + 32) % 11 == 0;
                            break;
                        case "rocks":
                        case "summit":
                            first = (x / 3 + y / 3) % 3 == 0;
                            second = (x + y * 2) % 7 == 0;
                            break;
                        case "depression":
                            int dx = x - 8;
                            int dy = y - 8;
                            int radius = dx * dx + dy * dy;
                            first = radius >= 24 && radius <= 38;
                            second = radius <= 5;
                            break;
                        default:
                            first = (x + y) % 6 == 0;
                            second = false;
                            break;
                    }
                    pixels[y * PatternSize + x] =
                        second ? dark : first ? light : normal;
                }
            }

            // Solid UI geometry samples UV 0,0. Keep that texel white so
            // contours and DEM vertices are not multiplied by the pattern.
            pixels[0] = new Color32(255, 255, 255, 255);
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Color32 Shade(Color color, float multiplier)
        {
            return new Color32(
                (byte)Mathf.RoundToInt(
                    Mathf.Clamp01(color.r * multiplier) * 255f),
                (byte)Mathf.RoundToInt(
                    Mathf.Clamp01(color.g * multiplier) * 255f),
                (byte)Mathf.RoundToInt(
                    Mathf.Clamp01(color.b * multiplier) * 255f),
                255);
        }
    }
}
