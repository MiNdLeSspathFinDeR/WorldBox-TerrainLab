using System;
using System.Collections.Generic;

namespace TerrainLab
{
    public static class TerrainWaterTopology
    {
        private static readonly int[] NeighborOffsetX = { -1, 1, 0, 0 };
        private static readonly int[] NeighborOffsetY = { 0, 0, -1, 1 };

        public static int[] CollectEnclosedDryIsland(
            int start,
            int width,
            int height,
            Func<int, bool> isWater,
            Func<int, bool> canErode,
            int maximumCells = 2)
        {
            int cellCount = checked(width * height);
            if (width <= 0 || height <= 0 || start < 0 ||
                start >= cellCount || isWater == null || canErode == null ||
                maximumCells <= 0 || isWater(start) || !canErode(start))
            {
                return Array.Empty<int>();
            }

            Queue<int> pending = new Queue<int>();
            HashSet<int> component = new HashSet<int>();
            component.Add(start);
            pending.Enqueue(start);
            while (pending.Count > 0)
            {
                int current = pending.Dequeue();
                int x = current % width;
                int y = current / width;
                for (int direction = 0;
                     direction < NeighborOffsetX.Length;
                     direction++)
                {
                    int neighborX = x + NeighborOffsetX[direction];
                    int neighborY = y + NeighborOffsetY[direction];
                    if (neighborX < 0 || neighborX >= width ||
                        neighborY < 0 || neighborY >= height)
                    {
                        return Array.Empty<int>();
                    }

                    int neighbor = neighborY * width + neighborX;
                    if (isWater(neighbor) || component.Contains(neighbor))
                    {
                        continue;
                    }

                    if (!canErode(neighbor) ||
                        component.Count >= maximumCells)
                    {
                        return Array.Empty<int>();
                    }

                    component.Add(neighbor);
                    pending.Enqueue(neighbor);
                }
            }

            foreach (int current in component)
            {
                int x = current % width;
                int y = current / width;
                for (int direction = 0;
                     direction < NeighborOffsetX.Length;
                     direction++)
                {
                    int neighborX = x + NeighborOffsetX[direction];
                    int neighborY = y + NeighborOffsetY[direction];
                    if (neighborX < 0 || neighborX >= width ||
                        neighborY < 0 || neighborY >= height)
                    {
                        return Array.Empty<int>();
                    }

                    int neighbor = neighborY * width + neighborX;
                    if (!component.Contains(neighbor) && !isWater(neighbor))
                    {
                        return Array.Empty<int>();
                    }
                }
            }

            int[] result = new int[component.Count];
            component.CopyTo(result);
            Array.Sort(result);
            return result;
        }
    }
}
