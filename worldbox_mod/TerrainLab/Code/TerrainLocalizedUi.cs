using System;
using NeoModLoader.General;
using UnityEngine.UI;

namespace TerrainLab
{
    internal static class TerrainLocalizedUi
    {
        public static Text Bind(Text text, string localizationKey)
        {
            if (text == null || string.IsNullOrWhiteSpace(localizationKey))
            {
                return text;
            }

            LocalizedText localized =
                text.GetComponent<LocalizedText>() ??
                text.gameObject.AddComponent<LocalizedText>();
            localized.autoField = false;
            localized.setKeyAndUpdate(localizationKey);
            return text;
        }

        public static bool Matches(
            string localizedValue,
            string localizationKey)
        {
            return !string.IsNullOrWhiteSpace(localizationKey) &&
                   string.Equals(
                       localizedValue,
                       LM.Get(localizationKey),
                       StringComparison.Ordinal);
        }
    }
}
