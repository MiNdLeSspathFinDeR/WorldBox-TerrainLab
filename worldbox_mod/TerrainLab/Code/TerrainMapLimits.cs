using System;
using System.Collections.Generic;

namespace TerrainLab
{
    public static class TerrainMapLimits
    {
        public const int WorldBoxRuntimeChunkSize = 16;
        public const int WorldBoxBlockSize = 64;
        public const int BaselineBlocksPerAxis = 20;
        public const int TolerancePercent = 15;

        public const long BaselineCellCount =
            (long)BaselineBlocksPerAxis * WorldBoxBlockSize *
            BaselineBlocksPerAxis * WorldBoxBlockSize;

        public const long MaximumCellCount =
            BaselineCellCount * (100 + TolerancePercent) / 100;

        public const int MaximumBlockCount =
            (int)(MaximumCellCount /
                  (WorldBoxBlockSize * WorldBoxBlockSize));

        public static long CountCells(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return 0;
            }

            return (long)width * height;
        }

        public static bool TryGetCellDimensionsFromBlocks(
            int widthBlocks,
            int heightBlocks,
            out int widthCells,
            out int heightCells)
        {
            widthCells = 0;
            heightCells = 0;
            if (widthBlocks <= 0 || heightBlocks <= 0)
            {
                return false;
            }

            long candidateWidth = (long)widthBlocks * WorldBoxBlockSize;
            long candidateHeight = (long)heightBlocks * WorldBoxBlockSize;
            if (candidateWidth > int.MaxValue || candidateHeight > int.MaxValue)
            {
                return false;
            }

            widthCells = (int)candidateWidth;
            heightCells = (int)candidateHeight;
            return true;
        }

        public static bool IsWithinBudget(int width, int height)
        {
            long cells = CountCells(width, height);
            return cells > 0 && cells <= MaximumCellCount;
        }

        public static bool TryValidate(int width, int height, out string error)
        {
            long cells = CountCells(width, height);
            if (cells == 0)
            {
                error = "Map dimensions must be positive.";
                return false;
            }

            if (cells > MaximumCellCount)
            {
                error = string.Format(
                    "Map has {0:N0} cells; TerrainLab limit is {1:N0} cells.",
                    cells,
                    MaximumCellCount);
                return false;
            }

            error = null;
            return true;
        }

        public static bool TryGetBlockDimensions(
            int aspectWidth,
            int aspectHeight,
            int longSideBlocks,
            out int widthBlocks,
            out int heightBlocks)
        {
            widthBlocks = 0;
            heightBlocks = 0;
            if (aspectWidth <= 0 || aspectHeight <= 0 ||
                longSideBlocks <= 0)
            {
                return false;
            }

            bool landscape = aspectWidth >= aspectHeight;
            double ratio = landscape
                ? aspectHeight / (double)aspectWidth
                : aspectWidth / (double)aspectHeight;
            int shortSideBlocks = Math.Max(
                1,
                (int)Math.Round(
                    longSideBlocks * ratio,
                    MidpointRounding.AwayFromZero));
            widthBlocks = landscape ? longSideBlocks : shortSideBlocks;
            heightBlocks = landscape ? shortSideBlocks : longSideBlocks;
            return (long)widthBlocks * heightBlocks <= MaximumBlockCount;
        }

        public static bool TryGetMaximumBlockDimensions(
            int aspectWidth,
            int aspectHeight,
            out int widthBlocks,
            out int heightBlocks)
        {
            widthBlocks = 0;
            heightBlocks = 0;
            if (aspectWidth <= 0 || aspectHeight <= 0)
            {
                return false;
            }

            for (int longSide = 1;
                 longSide <= MaximumBlockCount;
                 longSide++)
            {
                if (!TryGetBlockDimensions(
                        aspectWidth,
                        aspectHeight,
                        longSide,
                        out int candidateWidth,
                        out int candidateHeight))
                {
                    break;
                }

                widthBlocks = candidateWidth;
                heightBlocks = candidateHeight;
            }
            return widthBlocks > 0 && heightBlocks > 0;
        }

        public static void GetEffectiveAspectDimensions(
            int sourceWidth,
            int sourceHeight,
            IReadOnlyList<TerrainImageClassificationVertex> boundary,
            out int width,
            out int height)
        {
            width = Math.Max(1, sourceWidth);
            height = Math.Max(1, sourceHeight);
            if (boundary == null || boundary.Count < 3)
            {
                return;
            }

            int minimumX = int.MaxValue;
            int minimumY = int.MaxValue;
            int maximumX = int.MinValue;
            int maximumY = int.MinValue;
            int count = 0;
            foreach (TerrainImageClassificationVertex vertex in boundary)
            {
                if (vertex == null)
                {
                    continue;
                }
                minimumX = Math.Min(minimumX, vertex.X);
                minimumY = Math.Min(minimumY, vertex.Y);
                maximumX = Math.Max(maximumX, vertex.X);
                maximumY = Math.Max(maximumY, vertex.Y);
                count++;
            }
            if (count < 3)
            {
                return;
            }

            width = Math.Max(1, maximumX - minimumX + 1);
            height = Math.Max(1, maximumY - minimumY + 1);
        }
    }
}
