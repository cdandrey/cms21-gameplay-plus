using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Cms21GameplayPlus
{
    internal sealed class ModLocalizationCatalog
    {
        private const string EnglishResourceName =
            "Cms21GameplayPlus.Localization.en.json";
        private const string RussianResourceName =
            "Cms21GameplayPlus.Localization.ru.json";

        private readonly Dictionary<string, string> activeValues;

        private ModLocalizationCatalog(Dictionary<string, string> english,
            Dictionary<string, string> russian)
        {
            activeValues = ModLocalization.IsRussian
                ? russian ?? NewMap()
                : english ?? NewMap();
        }

        public static ModLocalizationCatalog LoadEmbedded()
        {
            return new ModLocalizationCatalog(
                ReadEmbeddedMap(EnglishResourceName),
                ReadEmbeddedMap(RussianResourceName));
        }

        public string Get(string key, string fallback)
        {
            string value;
            if (string.IsNullOrEmpty(key))
                return fallback;
            return activeValues.TryGetValue(key, out value)
                ? value : fallback;
        }

        private static Dictionary<string, string> ReadEmbeddedMap(
            string resourceName)
        {
            using (Stream stream = typeof(ModLocalizationCatalog).Assembly
                .GetManifestResourceStream(resourceName)) {
                if (stream == null)
                    throw new InvalidOperationException(
                        "Embedded localization resource was not found: " +
                        resourceName);
                using (StreamReader reader = new StreamReader(stream,
                    Encoding.UTF8, true)) {
                    Dictionary<string, string> values;
                    string error;
                    if (!LocalizationJsonReader.TryReadStringMap(
                            reader.ReadToEnd(), out values, out error))
                        throw new InvalidOperationException(
                            "Embedded localization resource could not be read: " +
                            resourceName + ": " + error);
                    return values;
                }
            }
        }

        private static Dictionary<string, string> NewMap()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    internal static class LocalizationJsonReader
    {
        public static bool TryReadStringMap(string json,
            out Dictionary<string, string> result, out string error)
        {
            result = new Dictionary<string, string>(StringComparer.Ordinal);
            error = string.Empty;
            int index = 0;
            SkipWhitespace(json, ref index);
            if (!Consume(json, ref index, '{')) {
                error = "Expected a JSON object.";
                return false;
            }
            SkipWhitespace(json, ref index);
            while (index < json.Length && json[index] != '}') {
                string key;
                if (!TryReadString(json, ref index, out key, out error))
                    return false;
                SkipWhitespace(json, ref index);
                if (!Consume(json, ref index, ':')) {
                    error = "Expected ':' after localization key.";
                    return false;
                }
                SkipWhitespace(json, ref index);
                string value;
                if (!TryReadString(json, ref index, out value, out error))
                    return false;
                result[key] = value;
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') {
                    index++;
                    SkipWhitespace(json, ref index);
                } else {
                    break;
                }
            }
            if (!Consume(json, ref index, '}')) {
                error = "Expected the end of a localization object.";
                return false;
            }
            return true;
        }

        private static bool TryReadString(string json, ref int index,
            out string value, out string error)
        {
            value = null;
            error = string.Empty;
            SkipWhitespace(json, ref index);
            if (!Consume(json, ref index, '"')) {
                error = "Expected a JSON string.";
                return false;
            }
            StringBuilder builder = new StringBuilder();
            while (index < json.Length) {
                char current = json[index++];
                if (current == '"') {
                    value = builder.ToString();
                    return true;
                }
                if (current != '\\') {
                    builder.Append(current);
                    continue;
                }
                if (index >= json.Length) {
                    error = "Invalid JSON escape sequence.";
                    return false;
                }
                char escaped = json[index++];
                switch (escaped) {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > json.Length) {
                            error = "Invalid Unicode escape sequence.";
                            return false;
                        }
                        int code;
                        if (!int.TryParse(json.Substring(index, 4),
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out code)) {
                            error = "Invalid Unicode escape sequence.";
                            return false;
                        }
                        builder.Append((char)code);
                        index += 4;
                        break;
                    default:
                        error = "Unsupported JSON escape sequence.";
                        return false;
                }
            }
            error = "Unterminated JSON string.";
            return false;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
                index++;
        }

        private static bool Consume(string json, ref int index,
            char expected)
        {
            if (index >= json.Length || json[index] != expected)
                return false;
            index++;
            return true;
        }
    }

    internal static class ModLocalization
    {
        private static ModLocalizationCatalog catalog;
        private static bool? isRussian;

        public static bool IsRussian
        {
            get {
                if (!isRussian.HasValue)
                    isRussian = DetectRussianLanguage();
                return isRussian.Value;
            }
        }

        public static string Get(string key)
        {
            if (catalog == null)
                catalog = ModLocalizationCatalog.LoadEmbedded();
            return catalog.Get(key, key);
        }

        private static bool DetectRussianLanguage()
        {
            try {
                string language = GameSettings.LanguageSettings;
                if (!string.IsNullOrWhiteSpace(language))
                    return IsRussianLanguageName(language);
            } catch {
            }
            return Application.systemLanguage == SystemLanguage.Russian;
        }

        private static bool IsRussianLanguageName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            return value.IndexOf("russian",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("рус", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(value, "ru", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ru-", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ru_", StringComparison.OrdinalIgnoreCase);
        }

        internal static void SetGameLanguage(string language)
        {
            catalog = null;
            isRussian = string.IsNullOrWhiteSpace(language)
                ? (bool?)null : IsRussianLanguageName(language);
        }
    }

    [HarmonyPatch(typeof(Localization), nameof(Localization.SetLanguage))]
    internal static class ModLocalizationLanguagePatch
    {
        [HarmonyPostfix]
        private static void Postfix(string __0)
        {
            ModLocalization.SetGameLanguage(__0);
        }
    }
}
