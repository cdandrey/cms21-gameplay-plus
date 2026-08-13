using System;
using System.Collections.Generic;
using System.IO;
using Tomlet;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.Containers;
#else
using CMS;
using CMS.Containers;
#endif

namespace Cms21GameplayPlus
{
    public sealed class RepairabilityFileConfig
    {
        public bool enabled = true;
        public RepairabilityFileGroup[] Group;
    }

    public sealed class RepairabilityFileGroup
    {
        public string name = "";
        public int repairGroup = 0;
        public string[] partIDs;
    }

    public static class RepairabilityManager
    {
        private const int MaximumSupportedRepairGroup = 7;
        private const string NonRepairableGroupName = "NonRepairable";
        private const string ForcedRepairabilityGroupPrefix = "Repairability";
        private static bool applied;
        private static Dictionary<string, int> defaultBrakeLatheRepairGroups;

        public static bool CustomRepairabilityActive { get; private set; }
        public static bool BrakeDrumLatheAvailable { get; private set; } = true;

        public static bool ApplyConfiguredRepairGroups() {
            if (applied)
                return true;

            if (Main.SettingsEntry == null)
                return false;

            GameInventory inventory = Singleton<GameInventory>.Instance;
            if (inventory == null || inventory.partPropertyList == null) {
                ModLogger.Log("[Repairability] GameInventory is not ready. Repair groups will be retried.", Types.LoggingLevels.Warning);
                return false;
            }

            EnsureDefaultBrakeLatheRepairGroups(inventory);

            if (!Main.SettingsEntry.Value.modifyRepairGroups) {
                CustomRepairabilityActive = false;
                SynchronizeBrakeLatheAvailability(inventory);
                applied = true;
                return true;
            }

            if (!File.Exists(GlobalConfig.cfgRepairability)) {
                ModLogger.Log("[Repairability] Config file not found: " + GlobalConfig.cfgRepairability, Types.LoggingLevels.Warning);
                CustomRepairabilityActive = false;
                SynchronizeBrakeLatheAvailability(inventory);
                applied = true;
                return true;
            }

            try {
                RepairabilityFileConfig config = TomletMain.To<RepairabilityFileConfig>(TomlParser.ParseFile(GlobalConfig.cfgRepairability));
                if (config == null || config.enabled == false) {
                    ModLogger.Log("[Repairability] Custom repairability is disabled.");
                    CustomRepairabilityActive = false;
                    SynchronizeBrakeLatheAvailability(inventory);
                    applied = true;
                    return true;
                }

                CustomRepairabilityActive = true;
                RepairabilityFileGroup[] groups = config.Group ?? new RepairabilityFileGroup[0];
                HashSet<string> nonRepairablePartIDs = new HashSet<string>(StringComparer.Ordinal);
                Dictionary<string, int> forcedRepairGroups = new Dictionary<string, int>(StringComparer.Ordinal);
                Dictionary<string, string> forcedRepairSources = new Dictionary<string, string>(StringComparer.Ordinal);
                List<RepairabilityFileGroup> specializedGroups = new List<RepairabilityFileGroup>();
                List<string> missingExamples = new List<string>();
                int priorityDuplicates = 0;
                int priorityConflicts = 0;
                int invalidPriorityGroups = 0;

                foreach (RepairabilityFileGroup group in groups) {
                    int forcedRepairGroup;
                    if (!TryGetForcedRepairGroup(group?.name, out forcedRepairGroup)) {
                        specializedGroups.Add(group);
                        continue;
                    }

                    if (group == null)
                        continue;
                    if (group.repairGroup != forcedRepairGroup) {
                        invalidPriorityGroups++;
                        ModLogger.Log("[Repairability] Reserved group '" + group.name + "' uses fixed RepairGroup " + forcedRepairGroup + "; configured value " + group.repairGroup + " is ignored.", Types.LoggingLevels.Warning);
                    }

                    string[] partIDs = group.partIDs ?? new string[0];
                    foreach (string configuredID in partIDs) {
                        string partID = configuredID?.Trim();
                        if (string.IsNullOrEmpty(partID))
                            continue;

                        if (forcedRepairGroup == 0) {
                            if (!nonRepairablePartIDs.Add(partID)) {
                                priorityDuplicates++;
                                continue;
                            }
                            if (forcedRepairGroups.Remove(partID)) {
                                forcedRepairSources.Remove(partID);
                                priorityConflicts++;
                            }
                            continue;
                        }

                        if (nonRepairablePartIDs.Contains(partID)) {
                            priorityConflicts++;
                            continue;
                        }
                        if (forcedRepairGroups.ContainsKey(partID)) {
                            priorityConflicts++;
                            ModLogger.Log("[Repairability] Part '" + partID + "' is listed in both '" + forcedRepairSources[partID] + "' and '" + group.name + "'. The first Repairability1..5 group wins.", Types.LoggingLevels.Warning);
                            continue;
                        }

                        forcedRepairGroups.Add(partID, forcedRepairGroup);
                        forcedRepairSources.Add(partID, group.name);
                    }
                }

                int forcedDisabled = 0;
                int forcedAssigned = 0;
                int forcedUnchanged = 0;
                int missing = 0;

                foreach (string partID in nonRepairablePartIDs) {
                    PartProperty part;
                    if (!TryGetPartProperty(inventory, partID, missingExamples, out part)) {
                        missing++;
                        continue;
                    }
                    if (part.RepairGroup == 0) {
                        forcedUnchanged++;
                        continue;
                    }
                    part.RepairGroup = 0;
                    forcedDisabled++;
                }

                foreach (KeyValuePair<string, int> forcedPart in forcedRepairGroups) {
                    if (nonRepairablePartIDs.Contains(forcedPart.Key))
                        continue;

                    PartProperty part;
                    if (!TryGetPartProperty(inventory, forcedPart.Key, missingExamples, out part)) {
                        missing++;
                        continue;
                    }
                    if (part.RepairGroup == forcedPart.Value) {
                        forcedUnchanged++;
                        continue;
                    }
                    part.RepairGroup = forcedPart.Value;
                    forcedAssigned++;
                }

                HashSet<string> specializedPartIDs = new HashSet<string>(StringComparer.Ordinal);
                int activeSpecializedGroups = 0;
                int assigned = 0;
                int unchanged = 0;
                int duplicate = 0;
                int skippedByPriority = 0;
                int invalidGroups = 0;

                foreach (RepairabilityFileGroup group in specializedGroups) {
                    if (group == null)
                        continue;
                    if (group.repairGroup < -1 || group.repairGroup > MaximumSupportedRepairGroup) {
                        invalidGroups++;
                        ModLogger.Log("[Repairability] Group '" + (group?.name ?? "") + "' has invalid repairGroup " + group?.repairGroup + ". Allowed values are -1 and 0..7.", Types.LoggingLevels.Warning);
                        continue;
                    }
                    if (group.repairGroup == -1)
                        continue;

                    activeSpecializedGroups++;
                    int groupAssigned = 0;
                    int groupUnchanged = 0;
                    int groupMissing = 0;
                    int groupSkippedByPriority = 0;
                    string[] partIDs = group.partIDs ?? new string[0];

                    foreach (string configuredID in partIDs) {
                        string partID = configuredID?.Trim();
                        if (string.IsNullOrEmpty(partID))
                            continue;
                        if (nonRepairablePartIDs.Contains(partID) || forcedRepairGroups.ContainsKey(partID)) {
                            skippedByPriority++;
                            groupSkippedByPriority++;
                            continue;
                        }
                        if (!specializedPartIDs.Add(partID)) {
                            duplicate++;
                            continue;
                        }

                        PartProperty part;
                        if (!TryGetPartProperty(inventory, partID, missingExamples, out part)) {
                            missing++;
                            groupMissing++;
                            continue;
                        }

                        if (part.RepairGroup == group.repairGroup) {
                            unchanged++;
                            groupUnchanged++;
                            continue;
                        }

                        part.RepairGroup = group.repairGroup;
                        assigned++;
                        groupAssigned++;
                    }

                    ModLogger.Log("[Repairability] " + group.name + ": mode=" + group.repairGroup + ", assigned=" + groupAssigned + ", unchanged=" + groupUnchanged + ", skippedByPriority=" + groupSkippedByPriority + ", missing=" + groupMissing);
                }

                SynchronizeBrakeLatheAvailability(inventory);

                ModLogger.Log("[Repairability] Applied once. Priority: nonRepairable=" + nonRepairablePartIDs.Count + ", forced=" + forcedRepairGroups.Count + ", forcedDisabled=" + forcedDisabled + ", forcedAssigned=" + forcedAssigned + ", forcedUnchanged=" + forcedUnchanged + ", priorityDuplicates=" + priorityDuplicates + ", priorityConflicts=" + priorityConflicts + ", invalidPriorityGroups=" + invalidPriorityGroups + ". Specialized: groups=" + specializedGroups.Count + ", active=" + activeSpecializedGroups + ", assigned=" + assigned + ", unchanged=" + unchanged + ", skippedByPriority=" + skippedByPriority + ", duplicates=" + duplicate + ", invalidGroups=" + invalidGroups + ", missing=" + missing + ".");
                if (missingExamples.Count > 0)
                    ModLogger.Log("[Repairability] First missing IDs: " + string.Join(", ", missingExamples.ToArray()), Types.LoggingLevels.Warning);

                applied = true;
                return true;
            } catch (Exception exception) {
                CustomRepairabilityActive = false;
                SynchronizeBrakeLatheAvailability(inventory);
                ModLogger.Log("[Repairability] Failed to load or apply " + GlobalConfig.cfgRepairability + Environment.NewLine + exception, Types.LoggingLevels.Error);
                return false;
            }
        }

        private static void SynchronizeBrakeLatheAvailability(
            GameInventory inventory)
        {
            BrakeLatheRepairabilityStatus drumStatus;
            BrakeLatheRepairabilityStatus gearsStatus;
            BrakeLatheRepairabilityStatus clutchDiscsStatus;
            BrakeLatheRepairabilityStatus pulleysStatus;
            bool settingsChanged = BrakeLatheExtensionsFeature
                .SynchronizeRepairabilitySettings(inventory,
                    out drumStatus, out gearsStatus,
                    out clutchDiscsStatus, out pulleysStatus);

            BrakeLatheRepairabilityStatus defaultDrumStatus;
            BrakeLatheRepairabilityStatus defaultGearsStatus;
            BrakeLatheRepairabilityStatus defaultClutchDiscsStatus;
            BrakeLatheRepairabilityStatus defaultPulleysStatus;
            BrakeLatheExtensionsFeature.GetRepairabilityStatuses(inventory,
                defaultBrakeLatheRepairGroups, true,
                out defaultDrumStatus, out defaultGearsStatus,
                out defaultClutchDiscsStatus, out defaultPulleysStatus);

            BrakeLatheRepairabilityStatus modifiedDrumStatus = drumStatus;
            BrakeLatheRepairabilityStatus modifiedGearsStatus = gearsStatus;
            BrakeLatheRepairabilityStatus modifiedClutchDiscsStatus =
                clutchDiscsStatus;
            BrakeLatheRepairabilityStatus modifiedPulleysStatus =
                pulleysStatus;
            if (Main.SettingsEntry != null &&
                !Main.SettingsEntry.Value.modifyRepairGroups) {
                Dictionary<string, int> configuredRepairGroups =
                    GetConfiguredBrakeLatheRepairGroups();
                BrakeLatheExtensionsFeature.GetRepairabilityStatuses(
                    inventory, configuredRepairGroups, false,
                    out modifiedDrumStatus, out modifiedGearsStatus,
                    out modifiedClutchDiscsStatus,
                    out modifiedPulleysStatus);
            }

            PublishBrakeLatheAvailability(drumStatus, gearsStatus,
                clutchDiscsStatus, pulleysStatus, modifiedDrumStatus,
                modifiedGearsStatus, modifiedClutchDiscsStatus,
                modifiedPulleysStatus, defaultDrumStatus,
                defaultGearsStatus, defaultClutchDiscsStatus,
                defaultPulleysStatus);
            if (settingsChanged)
                Main.SaveSettings();
        }

        private static void EnsureDefaultBrakeLatheRepairGroups(
            GameInventory inventory)
        {
            if (defaultBrakeLatheRepairGroups != null)
                return;

            defaultBrakeLatheRepairGroups =
                new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string partId in BrakeLatheExtensionsFeature
                .GetSupportedRepairabilityPartIds()) {
                if (!inventory.ExistsInPartProperty(partId))
                    continue;
                PartProperty property = inventory.GetItemProperty(partId);
                if (property != null)
                    defaultBrakeLatheRepairGroups[partId] =
                        property.RepairGroup;
            }
        }

        private static Dictionary<string, int>
            GetConfiguredBrakeLatheRepairGroups()
        {
            Dictionary<string, int> result = defaultBrakeLatheRepairGroups !=
                null
                    ? new Dictionary<string, int>(
                        defaultBrakeLatheRepairGroups, StringComparer.Ordinal)
                    : new Dictionary<string, int>(StringComparer.Ordinal);
            if (!File.Exists(GlobalConfig.cfgRepairability))
                return result;

            try {
                RepairabilityFileConfig config =
                    TomletMain.To<RepairabilityFileConfig>(
                        TomlParser.ParseFile(GlobalConfig.cfgRepairability));
                if (config == null || !config.enabled)
                    return result;

                HashSet<string> nonRepairable =
                    new HashSet<string>(StringComparer.Ordinal);
                Dictionary<string, int> forced =
                    new Dictionary<string, int>(StringComparer.Ordinal);
                List<RepairabilityFileGroup> specialized =
                    new List<RepairabilityFileGroup>();
                RepairabilityFileGroup[] groups = config.Group ??
                    new RepairabilityFileGroup[0];

                foreach (RepairabilityFileGroup group in groups) {
                    int forcedRepairGroup;
                    if (!TryGetForcedRepairGroup(group?.name,
                        out forcedRepairGroup)) {
                        specialized.Add(group);
                        continue;
                    }
                    if (group == null)
                        continue;
                    foreach (string configuredId in group.partIDs ??
                        new string[0]) {
                        string partId = configuredId?.Trim();
                        if (string.IsNullOrEmpty(partId) ||
                            !result.ContainsKey(partId))
                            continue;
                        if (forcedRepairGroup == 0) {
                            nonRepairable.Add(partId);
                            forced.Remove(partId);
                            continue;
                        }
                        if (nonRepairable.Contains(partId) ||
                            forced.ContainsKey(partId))
                            continue;
                        forced.Add(partId, forcedRepairGroup);
                    }
                }

                HashSet<string> specializedPartIds =
                    new HashSet<string>(StringComparer.Ordinal);
                foreach (RepairabilityFileGroup group in specialized) {
                    if (group == null || group.repairGroup < -1 ||
                        group.repairGroup > MaximumSupportedRepairGroup ||
                        group.repairGroup == -1)
                        continue;
                    foreach (string configuredId in group.partIDs ??
                        new string[0]) {
                        string partId = configuredId?.Trim();
                        if (string.IsNullOrEmpty(partId) ||
                            !result.ContainsKey(partId) ||
                            nonRepairable.Contains(partId) ||
                            forced.ContainsKey(partId) ||
                            !specializedPartIds.Add(partId))
                            continue;
                        result[partId] = group.repairGroup;
                    }
                }

                foreach (string partId in nonRepairable)
                    result[partId] = 0;
                foreach (KeyValuePair<string, int> item in forced)
                    result[item.Key] = item.Value;
            } catch (Exception exception) {
                ModLogger.Log("[Repairability] Failed to evaluate configured " +
                    "brake-lathe dependencies." + Environment.NewLine +
                    exception, Types.LoggingLevels.Warning);
            }
            return result;
        }

        public static bool HasRepairGroup(GameInventory inventory,
            string partID)
        {
            if (inventory == null || string.IsNullOrEmpty(partID) ||
                !inventory.ExistsInPartProperty(partID))
                return false;
            PartProperty property = inventory.GetItemProperty(partID);
            return property != null && property.RepairGroup != 0;
        }

        private static void PublishBrakeLatheAvailability(
            BrakeLatheRepairabilityStatus drum,
            BrakeLatheRepairabilityStatus gears,
            BrakeLatheRepairabilityStatus clutchDiscs,
            BrakeLatheRepairabilityStatus pulleys,
            BrakeLatheRepairabilityStatus modifiedDrum,
            BrakeLatheRepairabilityStatus modifiedGears,
            BrakeLatheRepairabilityStatus modifiedClutchDiscs,
            BrakeLatheRepairabilityStatus modifiedPulleys,
            BrakeLatheRepairabilityStatus defaultDrum,
            BrakeLatheRepairabilityStatus defaultGears,
            BrakeLatheRepairabilityStatus defaultClutchDiscs,
            BrakeLatheRepairabilityStatus defaultPulleys)
        {
            BrakeDrumLatheAvailable =
                BrakeLatheExtensionsFeature.IsAvailable(drum);
            UiIntegrationBridge.SyncBrakeLatheRepairabilityDependencies(
                modifiedDrum, modifiedGears, modifiedClutchDiscs,
                modifiedPulleys, defaultDrum, defaultGears,
                defaultClutchDiscs, defaultPulleys);
            if (Main.SettingsEntry != null)
                UiIntegrationBridge.SyncBrakeDrumRepairability(
                    Main.SettingsEntry.Value.allowBrakeLatheFixDrumBrake &&
                    BrakeLatheExtensionsFeature.IsAvailable(drum));
        }

        public static bool IsRepairable(Item item) {
            if (item == null)
                return false;

            GameInventory inventory = Singleton<GameInventory>.Instance;
            if (inventory == null || !inventory.ExistsInPartProperty(item.ID))
                return false;

            PartProperty property = inventory.GetItemProperty(item.ID);
            if (property != null && property.RepairGroup != 0)
                return true;

            switch (item.ID) {
                case "tarczaHamulcowa_1":
                case "tarczaWentylowana_1":
                case "tarczaWentylowana_1B":
                case "tarczaWentylowana_2":
                case "tarczaWentylowana_2B":
                case "tarczaWentylowana_3":
                    return true;
                case "pokrywaBeben_1":
                    return Main.SettingsEntry != null &&
                        Main.SettingsEntry.Value.allowBrakeLatheFixDrumBrake;
                default:
                    return false;
            }
        }

        private static bool TryGetForcedRepairGroup(string groupName, out int repairGroup) {
            repairGroup = -1;
            if (string.Equals(groupName, NonRepairableGroupName, StringComparison.OrdinalIgnoreCase)) {
                repairGroup = 0;
                return true;
            }
            if (string.IsNullOrWhiteSpace(groupName) || !groupName.StartsWith(ForcedRepairabilityGroupPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string suffix = groupName.Substring(ForcedRepairabilityGroupPrefix.Length);
            int parsedGroup;
            if (!int.TryParse(suffix, out parsedGroup) || parsedGroup < 1 || parsedGroup > 5)
                return false;

            repairGroup = parsedGroup;
            return true;
        }

        private static bool TryGetPartProperty(GameInventory inventory, string partID, List<string> missingExamples, out PartProperty part) {
            part = null;
            if (!inventory.ExistsInPartProperty(partID)) {
                if (missingExamples.Count < 10)
                    missingExamples.Add(partID);
                return false;
            }

            part = inventory.GetItemProperty(partID);
            if (part != null)
                return true;

            if (missingExamples.Count < 10)
                missingExamples.Add(partID);
            return false;
        }
    }
}
