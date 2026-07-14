using System;

namespace TerrainLab
{
    public static class TerrainMapLimits
    {
        public const int WorldBoxBlockSize = 64;
        public const int BaselineBlocksPerAxis = 20;
        public const int TolerancePercent = 15;

        public const long BaselineCellCount =
            (long)BaselineBlocksPerAxis * WorldBoxBlockSize *
            BaselineBlocksPerAxis * WorldBoxBlockSize;

        public const long MaximumCellCount =
            BaselineCellCount * (100 + TolerancePercent) / 100;

        public static long CountCells(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return 0;
            }

            return (long)width * height;
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
    }
}
