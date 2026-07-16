using UnityEngine;

namespace TerrainLab
{
    internal static class TerrainOverlayRendering
    {
        public static void Configure(
            GameObject chunk,
            SpriteRenderer renderer,
            int sortingOrder)
        {
            if (chunk == null || renderer == null)
            {
                return;
            }

            WorldTilemap worldTilemap = World.world?
                .GetComponentInChildren<WorldTilemap>(true);
            Renderer worldRenderer = worldTilemap?
                .GetComponentInChildren<Renderer>(true);
            if (worldRenderer != null)
            {
                chunk.layer = worldRenderer.gameObject.layer;
                renderer.sortingLayerID = worldRenderer.sortingLayerID;
            }
            else if (worldTilemap != null)
            {
                chunk.layer = worldTilemap.gameObject.layer;
            }

            renderer.sortingOrder = sortingOrder;
        }
    }
}
