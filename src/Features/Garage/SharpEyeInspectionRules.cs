using System;
using System.Collections.Generic;
using System.IO;
using Tomlet;

namespace Cms21GameplayPlus
{
    public sealed class SharpEyeInspectionFileConfig
    {
        public bool enabled = true;
        public SharpEyeInspectionFileGroup[] Group;
    }

    public sealed class SharpEyeInspectionFileGroup
    {
        public string name;
        public int inspectionLevel;
        public string[] partIDs;
    }

    internal static class SharpEyeInspectionRules
    {
        private static readonly Dictionary<string, int> PartLevels =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static bool loadAttempted;

        internal static void Load()
        {
            if (loadAttempted)
                return;
            loadAttempted = true;
            PartLevels.Clear();

            if (!File.Exists(GlobalConfig.cfgSharpEyeInspection)) {
                ModLogger.Log("[SharpEye] Inspection config not found: " +
                    GlobalConfig.cfgSharpEyeInspection,
                    Types.LoggingLevels.Warning);
                return;
            }

            try {
                SharpEyeInspectionFileConfig config =
                    TomletMain.To<SharpEyeInspectionFileConfig>(
                        TomlParser.ParseFile(GlobalConfig.cfgSharpEyeInspection));
                if (config == null || !config.enabled) {
                    ModLogger.Log("[SharpEye] Inspection config is disabled.");
                    return;
                }

                SharpEyeInspectionFileGroup[] groups = config.Group ??
                    new SharpEyeInspectionFileGroup[0];
                int invalidGroups = 0;
                int duplicateIds = 0;
                for (int groupIndex = 0; groupIndex < groups.Length;
                    groupIndex++) {
                    SharpEyeInspectionFileGroup group = groups[groupIndex];
                    if (group == null || group.inspectionLevel < 1 ||
                        group.inspectionLevel > 6) {
                        invalidGroups++;
                        continue;
                    }
                    string[] ids = group.partIDs ?? new string[0];
                    for (int idIndex = 0; idIndex < ids.Length; idIndex++) {
                        string id = ids[idIndex];
                        if (string.IsNullOrWhiteSpace(id))
                            continue;
                        id = id.Trim();
                        if (PartLevels.ContainsKey(id)) {
                            duplicateIds++;
                            continue;
                        }
                        PartLevels.Add(id, group.inspectionLevel);
                    }
                }
                ModLogger.Log("[SharpEye] Inspection config loaded: parts=" +
                    PartLevels.Count + ", invalidGroups=" + invalidGroups +
                    ", duplicates=" + duplicateIds + ".");
            } catch (Exception exception) {
                PartLevels.Clear();
                ModLogger.Log("[SharpEye] Failed to load " +
                    GlobalConfig.cfgSharpEyeInspection + Environment.NewLine +
                    exception, Types.LoggingLevels.Error);
            }
        }

        internal static bool TryGetLevel(string partId, out int level)
        {
            if (!loadAttempted)
                Load();
            if (string.IsNullOrWhiteSpace(partId)) {
                level = 0;
                return false;
            }
            return PartLevels.TryGetValue(partId.Trim(), out level);
        }
    }
}
