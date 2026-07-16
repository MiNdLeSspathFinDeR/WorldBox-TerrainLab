using System;
using System.Collections.Generic;

namespace TerrainLab
{
    public interface ITerrainLabEdit
    {
        string ProjectId { get; }

        int ChangedCellCount { get; }

        long EstimatedBytes { get; }

        void Apply(TerrainWorldState state, bool useAfterValues);
    }

    public readonly struct TerrainGridPoint : IEquatable<TerrainGridPoint>
    {
        public TerrainGridPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }

        public int Y { get; }

        public bool Equals(TerrainGridPoint other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            return obj is TerrainGridPoint other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Y;
            }
        }
    }

    public readonly struct TerrainBoundarySegment
    {
        public TerrainBoundarySegment(TerrainGridPoint start, TerrainGridPoint end)
        {
            Start = start;
            End = end;
        }

        public TerrainGridPoint Start { get; }

        public TerrainGridPoint End { get; }
    }

    public readonly struct TerrainSurfaceStamp : IEquatable<TerrainSurfaceStamp>
    {
        private static readonly string[] UnsafeIdTokens =
        {
            "grey_goo",
            "tnt",
            "landmine",
            "mine",
            "bomb",
            "firework",
            "tumor",
            "biomass",
            "cybertile",
            "super_pumpkin",
            "acid",
            "lava",
            "napalm",
            "nuke",
            "atomic"
        };

        public TerrainSurfaceStamp(
            string mainTypeId,
            string topTypeId,
            bool frozen)
        {
            MainTypeId = mainTypeId ?? string.Empty;
            TopTypeId = topTypeId ?? string.Empty;
            Frozen = frozen;
        }

        public string MainTypeId { get; }

        public string TopTypeId { get; }

        public bool Frozen { get; }

        public string DisplayName
        {
            get
            {
                string value = string.IsNullOrEmpty(TopTypeId)
                    ? MainTypeId
                    : TopTypeId;
                return Frozen ? value + " [frozen]" : value;
            }
        }

        public static TerrainSurfaceStamp Capture(WorldTile tile)
        {
            if (tile == null)
            {
                return default(TerrainSurfaceStamp);
            }

            return new TerrainSurfaceStamp(
                tile.main_type?.id,
                tile.top_type?.id,
                tile.data != null && tile.data.frozen);
        }

        public static bool TryCaptureSafe(
            WorldTile tile,
            out TerrainSurfaceStamp stamp,
            out string error)
        {
            stamp = Capture(tile);
            if (tile?.main_type == null)
            {
                error = "The tile has no base surface type.";
                return false;
            }

            if (!IsGameplaySafe(tile.main_type, out error) ||
                !IsGameplaySafe(tile.top_type, out error))
            {
                return false;
            }

            error = null;
            return true;
        }

        internal bool TryResolve(
            out TileType mainType,
            out TopTileType topType,
            out string error,
            bool requireGameplaySafe = true)
        {
            mainType = string.IsNullOrEmpty(MainTypeId)
                ? null
                : AssetManager.tiles.get(MainTypeId);
            topType = string.IsNullOrEmpty(TopTypeId)
                ? null
                : AssetManager.top_tiles.get(TopTypeId);

            if (mainType == null)
            {
                error = "Unknown base surface: " + MainTypeId;
                return false;
            }

            if (!string.IsNullOrEmpty(TopTypeId) && topType == null)
            {
                error = "Unknown top surface: " + TopTypeId;
                return false;
            }

            if (requireGameplaySafe &&
                (!IsGameplaySafe(mainType, out error) ||
                 !IsGameplaySafe(topType, out error)))
            {
                return false;
            }

            error = null;
            return true;
        }

        public bool Equals(TerrainSurfaceStamp other)
        {
            return Frozen == other.Frozen &&
                   string.Equals(MainTypeId, other.MainTypeId, StringComparison.Ordinal) &&
                   string.Equals(TopTypeId, other.TopTypeId, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is TerrainSurfaceStamp other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(MainTypeId ?? string.Empty);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(TopTypeId ?? string.Empty);
                return (hash * 397) ^ Frozen.GetHashCode();
            }
        }

        private static bool IsGameplaySafe(TileTypeBase type, out string error)
        {
            if (type == null)
            {
                error = null;
                return true;
            }

            string id = type.id ?? string.Empty;
            string normalized = id.ToLowerInvariant();
            bool unsafeFlags = !type.allowed_to_be_finger_copied ||
                               type.creep ||
                               type.grey_goo ||
                               type.lava ||
                               type.damage_units ||
                               type.explodable ||
                               type.explodable_delayed ||
                               type.explodable_timed;
            bool unsafeId = false;
            for (int index = 0; index < UnsafeIdTokens.Length; index++)
            {
                if (normalized.Contains(UnsafeIdTokens[index]))
                {
                    unsafeId = true;
                    break;
                }
            }

            if (unsafeFlags || unsafeId)
            {
                error = "Unsafe gameplay surface cannot be sampled: " + id;
                return false;
            }

            error = null;
            return true;
        }
    }

    public sealed class TerrainSurfaceEdit : ITerrainLabEdit
    {
        internal TerrainSurfaceEdit(
            string projectId,
            int[] indices,
            TerrainSurfaceStamp[] beforePalette,
            ushort[] beforeCodes,
            TerrainSurfaceStamp after)
        {
            ProjectId = projectId;
            Indices = indices ?? Array.Empty<int>();
            BeforePalette = beforePalette ?? Array.Empty<TerrainSurfaceStamp>();
            BeforeCodes = beforeCodes;
            After = after;
        }

        public string ProjectId { get; }

        public int ChangedCellCount => Indices.Length;

        public long EstimatedBytes =>
            (long)Indices.Length * sizeof(int) +
            (long)(BeforeCodes?.Length ?? 0) * sizeof(ushort) +
            (long)BeforePalette.Length * 48L + 96L;

        internal int[] Indices { get; }

        internal TerrainSurfaceStamp[] BeforePalette { get; }

        internal ushort[] BeforeCodes { get; }

        internal TerrainSurfaceStamp After { get; }

        internal TerrainSurfaceStamp GetBefore(int offset)
        {
            int paletteIndex = BeforeCodes == null ? 0 : BeforeCodes[offset];
            return BeforePalette[paletteIndex];
        }

        public void Apply(TerrainWorldState state, bool useAfterValues)
        {
            state.ApplySurfaceEdit(this, useAfterValues);
        }
    }

    public static class TerrainDigitizingRaster
    {
        public static int[] CollectConnectedRegion(
            int startIndex,
            int width,
            int height,
            Func<int, bool> belongsToRegion)
        {
            ValidateGrid(width, height);
            if (belongsToRegion == null)
            {
                throw new ArgumentNullException(nameof(belongsToRegion));
            }

            int cellCount = checked(width * height);
            if (startIndex < 0 || startIndex >= cellCount || !belongsToRegion(startIndex))
            {
                return Array.Empty<int>();
            }

            bool[] visited = new bool[cellCount];
            int[] queue = new int[Math.Min(cellCount, 4096)];
            int readOffset = 0;
            int writeOffset = 0;
            visited[startIndex] = true;
            queue[writeOffset++] = startIndex;

            while (readOffset < writeOffset)
            {
                int index = queue[readOffset++];
                int x = index % width;
                int y = index / width;
                if (x > 0)
                {
                    TryEnqueue(index - 1, belongsToRegion, visited, ref queue, ref writeOffset);
                }

                if (x + 1 < width)
                {
                    TryEnqueue(index + 1, belongsToRegion, visited, ref queue, ref writeOffset);
                }

                if (y > 0)
                {
                    TryEnqueue(index - width, belongsToRegion, visited, ref queue, ref writeOffset);
                }

                if (y + 1 < height)
                {
                    TryEnqueue(index + width, belongsToRegion, visited, ref queue, ref writeOffset);
                }
            }

            if (writeOffset != queue.Length)
            {
                Array.Resize(ref queue, writeOffset);
            }

            return queue;
        }

        public static int[] RasterizePolyline(
            IList<TerrainGridPoint> vertices,
            int width,
            int height,
            int radius)
        {
            ValidateGrid(width, height);
            if (vertices == null || vertices.Count == 0)
            {
                return Array.Empty<int>();
            }

            if (radius < 0 || radius > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(radius));
            }

            bool[] cells = new bool[checked(width * height)];
            int cellCount = 0;
            if (vertices.Count == 1)
            {
                AddDisc(
                    cells,
                    ref cellCount,
                    vertices[0].X,
                    vertices[0].Y,
                    radius,
                    width,
                    height);
            }
            else
            {
                for (int index = 1; index < vertices.Count; index++)
                {
                    RasterizeSegment(
                        vertices[index - 1],
                        vertices[index],
                        radius,
                        width,
                        height,
                        cells,
                        ref cellCount);
                }
            }

            return ToSortedArray(cells, cellCount);
        }

        public static int[] RasterizePolygon(
            IList<TerrainGridPoint> vertices,
            int width,
            int height)
        {
            ValidateGrid(width, height);
            if (vertices == null || vertices.Count < 3)
            {
                return Array.Empty<int>();
            }

            int minX = width - 1;
            int maxX = 0;
            int minY = height - 1;
            int maxY = 0;
            for (int index = 0; index < vertices.Count; index++)
            {
                minX = Math.Min(minX, vertices[index].X);
                maxX = Math.Max(maxX, vertices[index].X);
                minY = Math.Min(minY, vertices[index].Y);
                maxY = Math.Max(maxY, vertices[index].Y);
            }

            minX = Math.Max(0, minX);
            maxX = Math.Min(width - 1, maxX);
            minY = Math.Max(0, minY);
            maxY = Math.Min(height - 1, maxY);
            bool[] cells = new bool[checked(width * height)];
            int cellCount = 0;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (ContainsPoint(vertices, x + 0.5, y + 0.5))
                    {
                        AddCell(cells, ref cellCount, checked(y * width + x));
                    }
                }
            }

            for (int index = 0; index < vertices.Count; index++)
            {
                RasterizeSegment(
                    vertices[index],
                    vertices[(index + 1) % vertices.Count],
                    0,
                    width,
                    height,
                    cells,
                    ref cellCount);
            }

            return ToSortedArray(cells, cellCount);
        }

        public static int[] RasterizeRectangle(
            TerrainGridPoint first,
            TerrainGridPoint second,
            int width,
            int height)
        {
            ValidateGrid(width, height);
            int minX = Math.Max(0, Math.Min(first.X, second.X));
            int maxX = Math.Min(width - 1, Math.Max(first.X, second.X));
            int minY = Math.Max(0, Math.Min(first.Y, second.Y));
            int maxY = Math.Min(height - 1, Math.Max(first.Y, second.Y));
            if (minX > maxX || minY > maxY)
            {
                return Array.Empty<int>();
            }

            int[] cells = new int[checked((maxX - minX + 1) * (maxY - minY + 1))];
            int offset = 0;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    cells[offset++] = checked(y * width + x);
                }
            }

            return cells;
        }

        public static TerrainBoundarySegment[] BuildBoundary(
            int[] indices,
            int width,
            int height,
            int maximumSegments = 200000)
        {
            ValidateGrid(width, height);
            if (indices == null || indices.Length == 0)
            {
                return Array.Empty<TerrainBoundarySegment>();
            }

            bool[] selected = new bool[checked(width * height)];
            for (int offset = 0; offset < indices.Length; offset++)
            {
                int index = indices[offset];
                if (index >= 0 && index < selected.Length)
                {
                    selected[index] = true;
                }
            }

            List<TerrainBoundarySegment> result = new List<TerrainBoundarySegment>();
            for (int offset = 0; offset < indices.Length && result.Count < maximumSegments; offset++)
            {
                int index = indices[offset];
                if (index < 0 || index >= selected.Length)
                {
                    continue;
                }

                int x = index % width;
                int y = index / width;
                if (y == 0 || !selected[index - width])
                {
                    result.Add(new TerrainBoundarySegment(
                        new TerrainGridPoint(x, y),
                        new TerrainGridPoint(x + 1, y)));
                }

                if (x + 1 == width || !selected[index + 1])
                {
                    result.Add(new TerrainBoundarySegment(
                        new TerrainGridPoint(x + 1, y),
                        new TerrainGridPoint(x + 1, y + 1)));
                }

                if (y + 1 == height || !selected[index + width])
                {
                    result.Add(new TerrainBoundarySegment(
                        new TerrainGridPoint(x + 1, y + 1),
                        new TerrainGridPoint(x, y + 1)));
                }

                if (x == 0 || !selected[index - 1])
                {
                    result.Add(new TerrainBoundarySegment(
                        new TerrainGridPoint(x, y + 1),
                        new TerrainGridPoint(x, y)));
                }
            }

            return result.ToArray();
        }

        private static void ValidateGrid(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            checked
            {
                _ = width * height;
            }
        }

        private static void TryEnqueue(
            int index,
            Func<int, bool> belongsToRegion,
            bool[] visited,
            ref int[] queue,
            ref int writeOffset)
        {
            if (visited[index])
            {
                return;
            }

            visited[index] = true;
            if (belongsToRegion(index))
            {
                if (writeOffset == queue.Length)
                {
                    int nextLength = Math.Min(
                        visited.Length,
                        Math.Max(queue.Length + 1, queue.Length * 2));
                    Array.Resize(ref queue, nextLength);
                }

                queue[writeOffset++] = index;
            }
        }

        private static void RasterizeSegment(
            TerrainGridPoint first,
            TerrainGridPoint second,
            int radius,
            int width,
            int height,
            bool[] result,
            ref int resultCount)
        {
            int x = first.X;
            int y = first.Y;
            int dx = Math.Abs(second.X - x);
            int stepX = x < second.X ? 1 : -1;
            int dy = -Math.Abs(second.Y - y);
            int stepY = y < second.Y ? 1 : -1;
            int error = dx + dy;

            while (true)
            {
                AddDisc(
                    result,
                    ref resultCount,
                    x,
                    y,
                    radius,
                    width,
                    height);
                if (x == second.X && y == second.Y)
                {
                    break;
                }

                int doubled = error * 2;
                if (doubled >= dy)
                {
                    error += dy;
                    x += stepX;
                }

                if (doubled <= dx)
                {
                    error += dx;
                    y += stepY;
                }
            }
        }

        private static void AddDisc(
            bool[] result,
            ref int resultCount,
            int centerX,
            int centerY,
            int radius,
            int width,
            int height)
        {
            int radiusSquared = radius * radius;
            for (int offsetY = -radius; offsetY <= radius; offsetY++)
            {
                int y = centerY + offsetY;
                if (y < 0 || y >= height)
                {
                    continue;
                }

                for (int offsetX = -radius; offsetX <= radius; offsetX++)
                {
                    int x = centerX + offsetX;
                    if (x >= 0 && x < width &&
                        offsetX * offsetX + offsetY * offsetY <= radiusSquared)
                    {
                        AddCell(
                            result,
                            ref resultCount,
                            checked(y * width + x));
                    }
                }
            }
        }

        private static bool ContainsPoint(
            IList<TerrainGridPoint> polygon,
            double x,
            double y)
        {
            bool inside = false;
            int previous = polygon.Count - 1;
            for (int current = 0; current < polygon.Count; current++)
            {
                TerrainGridPoint a = polygon[current];
                TerrainGridPoint b = polygon[previous];
                bool crosses = (a.Y > y) != (b.Y > y) &&
                               x < (b.X - a.X) * (y - a.Y) /
                               (double)(b.Y - a.Y) + a.X;
                if (crosses)
                {
                    inside = !inside;
                }

                previous = current;
            }

            return inside;
        }

        private static void AddCell(bool[] cells, ref int cellCount, int index)
        {
            if (!cells[index])
            {
                cells[index] = true;
                cellCount++;
            }
        }

        private static int[] ToSortedArray(bool[] cells, int cellCount)
        {
            int[] result = new int[cellCount];
            int offset = 0;
            for (int index = 0; index < cells.Length; index++)
            {
                if (cells[index])
                {
                    result[offset++] = index;
                }
            }

            return result;
        }
    }
}
