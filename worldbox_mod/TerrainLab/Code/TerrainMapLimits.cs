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

        public const long RecommendedMaximumCellCount =
            BaselineCellCount * (100 + TolerancePercent) / 100;

        public const int RecommendedMaximumBlockCount =
            (int)(RecommendedMaximumCellCount /
                  (WorldBoxBlockSize * WorldBoxBlockSize));

        // TerrainWorldState uses one-dimensional CLR arrays and checked int
        // indices. This is an implementation ceiling, not a gameplay budget.
        public const long MaximumAddressableCellCount = int.MaxValue;

        public const int MaximumAddressableBlockCount =
            (int)(MaximumAddressableCellCount /
                  (WorldBoxBlockSize * WorldBoxBlockSize));

        // Retain the old public names for package/API compatibility. New code
        // must distinguish the technical ceiling from the recommendation.
        public const long MaximumCellCount = MaximumAddressableCellCount;
        public const int MaximumBlockCount = MaximumAddressableBlockCount;

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

        public static bool IsWithinRecommendedBudget(int width, int height)
        {
            long cells = CountCells(width, height);
            return cells > 0 && cells <= RecommendedMaximumCellCount;
        }

        public static bool IsWithinBudget(int width, int height)
        {
            return IsWithinRecommendedBudget(width, height);
        }

        public static bool IsAboveRecommendedBudgetForBlocks(
            int widthBlocks,
            int heightBlocks)
        {
            if (!TryGetCellDimensionsFromBlocks(
                    widthBlocks,
                    heightBlocks,
                    out int widthCells,
                    out int heightCells))
            {
                return false;
            }
            return !IsWithinRecommendedBudget(widthCells, heightCells);
        }

        public static bool TryValidate(int width, int height, out string error)
        {
            long cells = CountCells(width, height);
            if (cells == 0)
            {
                error = "Map dimensions must be positive.";
                return false;
            }

            if (cells > MaximumAddressableCellCount)
            {
                error = string.Format(
                    "Map has {0:N0} cells; TerrainLab arrays can address at most {1:N0} cells.",
                    cells,
                    MaximumAddressableCellCount);
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
            return TryGetCellDimensionsFromBlocks(
                       widthBlocks,
                       heightBlocks,
                       out int widthCells,
                       out int heightCells) &&
                   TryValidate(widthCells, heightCells, out _);
        }

        public static bool TryGetRecommendedBlockDimensions(
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
                 longSide <= RecommendedMaximumBlockCount;
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

                if (IsAboveRecommendedBudgetForBlocks(
                        candidateWidth,
                        candidateHeight))
                {
                    break;
                }

                widthBlocks = candidateWidth;
                heightBlocks = candidateHeight;
            }
            return widthBlocks > 0 && heightBlocks > 0;
        }

        public static bool TryGetMaximumBlockDimensions(
            int aspectWidth,
            int aspectHeight,
            out int widthBlocks,
            out int heightBlocks)
        {
            return TryGetRecommendedBlockDimensions(
                aspectWidth,
                aspectHeight,
                out widthBlocks,
                out heightBlocks);
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
