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

            MapBox world = World.world;
            Renderer rootRenderer = world?.GetComponent<Renderer>();
            WorldTilemap worldTilemap = world?
                .GetComponentInChildren<WorldTilemap>(true);
            Renderer gameplayRenderer = worldTilemap?
                .GetComponentInChildren<Renderer>(true);
            WorldLayer worldLayer = world?
                .GetComponentInChildren<WorldLayer>(true);
            Renderer overviewRenderer = worldLayer?
                .GetComponentInChildren<Renderer>(true);

            Renderer sortingReference = SelectTopmost(
                SelectTopmost(rootRenderer, gameplayRenderer),
                overviewRenderer);
            Renderer visibilityReference = overviewRenderer ??
                gameplayRenderer ?? rootRenderer;
            if (visibilityReference != null)
            {
                // WorldBox disables WorldTilemap and enables WorldLayer when the
                // camera enters overview mode. Use the overview object's camera
                // layer so the GIS overlay survives that renderer transition.
                chunk.layer = visibilityReference.gameObject.layer;
            }
            else if (worldTilemap != null)
            {
                chunk.layer = worldTilemap.gameObject.layer;
            }

            if (sortingReference != null)
            {
                renderer.sortingLayerID = sortingReference.sortingLayerID;
                sortingOrder = Mathf.Max(
                    sortingOrder,
                    sortingReference.sortingOrder + 1);
            }

            renderer.sortingOrder = sortingOrder;
            renderer.allowOcclusionWhenDynamic = false;
        }

        private static Renderer SelectTopmost(
            Renderer current,
            Renderer candidate)
        {
            if (candidate == null)
            {
                return current;
            }

            if (current == null)
            {
                return candidate;
            }

            int currentLayer = SortingLayer.GetLayerValueFromID(
                current.sortingLayerID);
            int candidateLayer = SortingLayer.GetLayerValueFromID(
                candidate.sortingLayerID);
            if (candidateLayer != currentLayer)
            {
                return candidateLayer > currentLayer ? candidate : current;
            }

            return candidate.sortingOrder > current.sortingOrder
                ? candidate
                : current;
        }
    }
}
