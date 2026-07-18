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
        private Color _fillColor = new Color(1f, 1f, 1f, 0.18f);
        private Color _lineColor = Color.white;

        public void Configure(
            IEnumerable<TerrainImageClassificationVertex> vertices,
            int sourceWidth,
            int sourceHeight,
            Color surfaceColor,
            bool closed,
            bool showVertices,
            float inverseZoom,
            float closedFillAlpha = 0.22f)
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
            _fillColor = new Color(
                surfaceColor.r,
                surfaceColor.g,
                surfaceColor.b,
                closed ? Mathf.Clamp01(closedFillAlpha) : 0.08f);
            _lineColor = new Color(
                surfaceColor.r,
                surfaceColor.g,
                surfaceColor.b,
                0.98f);
            raycastTarget = false;
            SetVerticesDirty();
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
                AddFill(helper, points);
            }

            int segmentCount = _closed
                ? points.Count
                : Math.Max(0, points.Count - 1);
            float thickness = 2.2f * _inverseZoom;
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
                    AddDiamond(helper, point, radius, _lineColor);
                }
            }
        }

        private void AddFill(VertexHelper helper, IReadOnlyList<Vector2> points)
        {
            List<int> triangles = Triangulate(points);
            for (int index = 0; index + 2 < triangles.Count; index += 3)
            {
                AddTriangle(
                    helper,
                    points[triangles[index]],
                    points[triangles[index + 1]],
                    points[triangles[index + 2]],
                    _fillColor);
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

        private static void AddTriangle(
            VertexHelper helper,
            Vector2 first,
            Vector2 second,
            Vector2 third,
            Color color)
        {
            int offset = helper.currentVertCount;
            helper.AddVert(first, color, Vector2.zero);
            helper.AddVert(second, color, Vector2.zero);
            helper.AddVert(third, color, Vector2.zero);
            helper.AddTriangle(offset, offset + 1, offset + 2);
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
}
