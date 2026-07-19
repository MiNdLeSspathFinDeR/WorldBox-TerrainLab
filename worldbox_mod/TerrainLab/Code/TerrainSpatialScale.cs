using System;

namespace TerrainLab
{
    public static class TerrainSpatialScale
    {
        public const double DefaultHorizontalMetresPerCell = 1000d;
        public const double MinimumHorizontalMetresPerCell = 1d;
        public const double MaximumHorizontalMetresPerCell = 1000000d;
        public const int GeneratedMaximumCardinalRiseMetres = 364;
        public const int GeneratedMaximumDiagonalRiseMetres = 515;
        public const int GeneratedMaximumCoastalMagnitudeMetres = 250;

        public static bool IsValid(double metresPerCell)
        {
            return !double.IsNaN(metresPerCell) &&
                   !double.IsInfinity(metresPerCell) &&
                   metresPerCell >= MinimumHorizontalMetresPerCell &&
                   metresPerCell <= MaximumHorizontalMetresPerCell;
        }

        public static double NormalizeOrDefault(double metresPerCell)
        {
            return IsValid(metresPerCell)
                ? metresPerCell
                : DefaultHorizontalMetresPerCell;
        }
    }
}
