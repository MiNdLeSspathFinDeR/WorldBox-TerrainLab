using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TerrainLab
{
    internal static class TerrainImageUiVisuals
    {
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.FlattenHierarchy;

        private static readonly Dictionary<string, Sprite> BiotopeSprites =
            new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Sprite> SurfaceSprites =
            new Dictionary<string, Sprite>(StringComparer.Ordinal);

        private static Sprite _activityOnSprite;
        private static Sprite _activityOffSprite;
        private static bool _activitySpritesResolved;

        public static Sprite GetBiotopeSprite(string biotopeId)
        {
            if (string.IsNullOrWhiteSpace(biotopeId))
            {
                return null;
            }
            if (BiotopeSprites.TryGetValue(
                    biotopeId,
                    out Sprite cached) &&
                cached != null)
            {
                return cached;
            }

            Sprite sprite = FindTopTileSprite(
                biotopeId + "_low",
                biotopeId + "_high");
            if (sprite == null &&
                string.Equals(
                    biotopeId,
                    "wasteland",
                    StringComparison.Ordinal))
            {
                sprite = FindTopTileSprite("waste_low", "waste_high");
            }
            if (sprite == null)
            {
                sprite = FindBiomeAssetSprite(biotopeId);
            }
            if (sprite == null)
            {
                sprite = CreateFallbackSprite(
                    "TerrainLabBiotopeFallback_" + biotopeId,
                    GetBiotopeColor(biotopeId));
            }

            BiotopeSprites[biotopeId] = sprite;
            return sprite;
        }

        public static Sprite GetSurfaceSprite(string surfaceId)
        {
            if (string.IsNullOrWhiteSpace(surfaceId))
            {
                return null;
            }
            if (SurfaceSprites.TryGetValue(
                    surfaceId,
                    out Sprite cached) &&
                cached != null)
            {
                return cached;
            }

            string[] tileIds;
            switch (surfaceId)
            {
                case "deep_ocean":
                    tileIds = new[] {"deep_ocean"};
                    break;
                case "shelf":
                    tileIds = new[] {"close_ocean", "deep_ocean"};
                    break;
                case "shallow_water":
                case "river_lake":
                    tileIds = new[] {"shallow_waters", "close_ocean"};
                    break;
                case "sand":
                    tileIds = new[] {"sand"};
                    break;
                case "plain":
                case "lowland":
                case "depression":
                    tileIds = new[] {"soil_low"};
                    break;
                case "upland":
                    tileIds = new[] {"soil_high", "soil_low"};
                    break;
                case "hills":
                    tileIds = new[] {"hills", "soil_high"};
                    break;
                case "rocks":
                case "summit":
                    tileIds = new[] {"mountains", "hills"};
                    break;
                default:
                    tileIds = Array.Empty<string>();
                    break;
            }

            Sprite sprite = FindMainTileSprite(tileIds);
            if (sprite == null)
            {
                sprite = CreateFallbackSprite(
                    "TerrainLabSurfaceFallback_" + surfaceId,
                    GetSurfaceColor(surfaceId));
            }

            SurfaceSprites[surfaceId] = sprite;
            return sprite;
        }

        public static Sprite GetActivitySprite(bool active)
        {
            ResolveActivitySprites();
            return active
                ? _activityOnSprite
                : _activityOffSprite ?? _activityOnSprite;
        }

        public static Color GetSurfaceColor(string surface)
        {
            switch (surface)
            {
                case "deep_ocean":
                    return new Color(0.08f, 0.22f, 0.58f, 1f);
                case "shelf":
                    return new Color(0.12f, 0.47f, 0.74f, 1f);
                case "shallow_water":
                case "river_lake":
                    return new Color(0.2f, 0.75f, 0.9f, 1f);
                case "sand":
                    return new Color(0.94f, 0.79f, 0.35f, 1f);
                case "plain":
                    return new Color(0.48f, 0.78f, 0.28f, 1f);
                case "lowland":
                    return new Color(0.66f, 0.82f, 0.31f, 1f);
                case "upland":
                    return new Color(0.49f, 0.64f, 0.24f, 1f);
                case "hills":
                    return new Color(0.68f, 0.46f, 0.25f, 1f);
                case "rocks":
                    return new Color(0.46f, 0.47f, 0.45f, 1f);
                case "summit":
                    return Color.white;
                case "depression":
                    return new Color(0.73f, 0.28f, 0.7f, 1f);
                default:
                    return Color.magenta;
            }
        }

        private static Sprite FindTopTileSprite(params string[] ids)
        {
            foreach (string id in ids)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                try
                {
                    Sprite sprite = TryGetTypeSprite(
                        AssetManager.top_tiles.get(id));
                    if (sprite != null)
                    {
                        return sprite;
                    }
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        private static Sprite FindMainTileSprite(params string[] ids)
        {
            foreach (string id in ids)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }
                try
                {
                    Sprite sprite = TryGetTypeSprite(
                        AssetManager.tiles.get(id));
                    if (sprite != null)
                    {
                        return sprite;
                    }
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        private static Sprite FindBiomeAssetSprite(string biotopeId)
        {
            string expected = "biome_" + biotopeId;
            List<BiomeAsset> biomes = BiomeLibrary.pool_biomes;
            if (biomes == null)
            {
                return null;
            }
            foreach (BiomeAsset biome in biomes)
            {
                if (biome == null ||
                    !string.Equals(
                        biome.id,
                        expected,
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        biome.id,
                        biotopeId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string methodName in
                         new[] {"getTileLow", "getTileHigh"})
                {
                    try
                    {
                        MethodInfo method = biome.GetType().GetMethod(
                            methodName,
                            InstanceMembers);
                        Sprite sprite = TryGetTypeSprite(
                            method?.Invoke(biome, null));
                        if (sprite != null)
                        {
                            return sprite;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            return null;
        }

        private static Sprite TryGetTypeSprite(object tileType)
        {
            if (tileType == null)
            {
                return null;
            }
            try
            {
                object direct = GetMemberValue(tileType, "sprite");
                if (direct is Sprite directSprite)
                {
                    return directSprite;
                }

                object sprites = GetMemberValue(tileType, "sprites");
                object main = GetMemberValue(sprites, "main");
                return GetMemberValue(main, "sprite") as Sprite;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object GetMemberValue(object target, string name)
        {
            if (target == null)
            {
                return null;
            }
            Type type = target.GetType();
            FieldInfo field = type.GetField(name, InstanceMembers);
            if (field != null)
            {
                return field.GetValue(target);
            }
            PropertyInfo property = type.GetProperty(name, InstanceMembers);
            return property?.GetValue(target, null);
        }

        private static void ResolveActivitySprites()
        {
            if (_activitySpritesResolved)
            {
                return;
            }
            _activitySpritesResolved = true;

            ToggleIcon[] candidates =
                Resources.FindObjectsOfTypeAll<ToggleIcon>();
            ToggleIcon best = null;
            int bestScore = int.MinValue;
            foreach (ToggleIcon candidate in candidates)
            {
                if (candidate == null || candidate.spriteON == null)
                {
                    continue;
                }
                int score = candidate.gameObject.activeInHierarchy ? 8 : 0;
                if (candidate.GetComponentInParent<PowerButton>() != null)
                {
                    score += 8;
                }
                if (candidate.spriteOFF != null)
                {
                    score += 4;
                }
                if (score <= bestScore)
                {
                    continue;
                }
                best = candidate;
                bestScore = score;
            }

            if (best != null)
            {
                _activityOnSprite = best.spriteON;
                _activityOffSprite = best.spriteOFF;
            }
            if (_activityOnSprite == null)
            {
                _activityOnSprite = CreateFallbackSprite(
                    "TerrainLabSelectionLampOn",
                    new Color(0.2f, 0.95f, 0.26f, 1f),
                    5);
                _activityOffSprite = CreateFallbackSprite(
                    "TerrainLabSelectionLampOff",
                    new Color(0.1f, 0.12f, 0.09f, 1f),
                    5);
            }
        }

        private static Sprite CreateFallbackSprite(
            string name,
            Color color,
            int size = 12)
        {
            Texture2D texture = new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false)
            {
                name = name + "Texture",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            Color32 normal = color;
            Color32 light = Color.Lerp(color, Color.white, 0.28f);
            Color32 dark = Color.Lerp(color, Color.black, 0.32f);
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool border =
                        x == 0 || y == 0 || x == size - 1 || y == size - 1;
                    pixels[y * size + x] = border
                        ? dark
                        : (x + y) % 5 == 0 ? light : normal;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                size);
            sprite.name = name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        private static Color GetBiotopeColor(string id)
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(id);
                float hue = (hash & 0x7fffffff) % 360 / 360f;
                return Color.HSVToRGB(hue, 0.58f, 0.86f);
            }
        }
    }
}
