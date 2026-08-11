using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2Cpp;
using Il2CppCMS.Containers;
using Il2CppCMS.Helpers;
using Il2CppCMS.SceneLoaders;
using Il2CppCMS.UI.Windows;
#else
using CMS;
using CMS.Containers;
using CMS.Helpers;
using CMS.SceneLoaders;
using CMS.UI.Windows;
#endif

namespace Cms21GameplayPlus
{
    /// <summary>
    /// Makes parts stored in every unlocked warehouse available to separately
    /// configurable normal-assembly, wheel-machine and spring-clamp selection paths.
    /// Cached objects are the original warehouse references; native item matching uses
    /// temporary combined lists or short-lived machine-specific Inventory references.
    /// </summary>
    [HarmonyPatch]
    public static class WarehouseMountSourceFeature
    {
        private const int MaximumGarageLoaderWaitFrames = 3600;

        private const string AllEnginesGroupQuery = "engine-all";
        private const string WheelConnectStationMode = "WheelConnect";
        private const string WheelSeparateStationMode = "WheelSeparate";
        private const string SpringConnectStationMode = "SpringConnect";
        private const string SpringSeparateStationMode = "SpringSeparate";

        private enum WarehouseMountSubgroup
        {
            None,
            General,
            WheelStations,
            SpringClamp
        }

        private sealed class WarehouseItemEntry
        {
            public Item Item;
            public Il2CppSystem.Collections.Generic.List<Item> Container;
        }

        private sealed class WarehouseGroupEntry
        {
            public GroupItem Group;
            public Il2CppSystem.Collections.Generic.List<GroupItem> Container;
        }

        internal sealed class StationInventoryInjectionState
        {
            public Inventory Inventory;
            public readonly List<long> InjectedItemUids =
                new List<long>();
            public readonly List<long> InjectedGroupUids =
                new List<long>();
        }

        private static readonly List<WarehouseItemEntry> CachedItems =
            new List<WarehouseItemEntry>();
        private static readonly List<WarehouseGroupEntry> CachedGroups =
            new List<WarehouseGroupEntry>();
        private static readonly Dictionary<long, WarehouseItemEntry> ItemsByUid =
            new Dictionary<long, WarehouseItemEntry>();
        private static readonly Dictionary<long, WarehouseGroupEntry> GroupsByUid =
            new Dictionary<long, WarehouseGroupEntry>();
        private static readonly HashSet<long> OfferedItemUids =
            new HashSet<long>();
        private static readonly HashSet<long> OfferedGroupUids =
            new HashSet<long>();
        private static readonly HashSet<long> EligibleItemUids =
            new HashSet<long>();
        private static readonly HashSet<long> EligibleGroupUids =
            new HashSet<long>();
        private static readonly HashSet<long> StationItemUids =
            new HashSet<long>();
        private static readonly HashSet<long> StationGroupUids =
            new HashSet<long>();

        private static bool cacheReady;
        private static bool cacheDirty = true;
        private static bool warehouseWindowOpen;
        private static bool rebuildAfterCloseScheduled;
        private static bool garageLoadRefreshRunning;
        private static StationInventoryInjectionState
            wheelTireSelectionInjection;
        private static bool wheelConnectSelectionArmed;
        private static long pendingWheelConnectRimUid;
        private static WarehouseMountSubgroup activePrepareMountSubgroup;
        private static bool springConnectSelectionArmed;
        private static long pendingSpringConnectBaseUid;
        private static int springFollowUpLookupsRemaining;
        private static int lifecycleGeneration;

        private static bool IsGeneralMountEnabled {
            get {
                return GlobalState.IsGarageSceneActive &&
                    Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.includeWarehousePartsInMountSelection;
            }
        }

        private static bool IsWheelStationsEnabled {
            get {
                return GlobalState.IsGarageSceneActive &&
                    Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.includeWarehousePartsInWheelStations;
            }
        }

        private static bool IsSpringClampEnabled {
            get {
                return GlobalState.IsGarageSceneActive &&
                    Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.includeWarehousePartsInSpringClamp;
            }
        }

        private static bool IsAnySubgroupEnabled {
            get {
                return IsGeneralMountEnabled || IsWheelStationsEnabled ||
                    IsSpringClampEnabled;
            }
        }

        private static bool IsSubgroupEnabled(WarehouseMountSubgroup subgroup)
        {
            switch (subgroup) {
                case WarehouseMountSubgroup.General:
                    return IsGeneralMountEnabled;
                case WarehouseMountSubgroup.WheelStations:
                    return IsWheelStationsEnabled;
                case WarehouseMountSubgroup.SpringClamp:
                    return IsSpringClampEnabled;
                default:
                    return false;
            }
        }

        public static void BeginRefreshAfterGarageLoad()
        {
            Reset();
            if (!IsAnySubgroupEnabled || garageLoadRefreshRunning)
                return;

            garageLoadRefreshRunning = true;
            int generation = lifecycleGeneration;
            MelonCoroutines.Start(RefreshAfterGarageLoad(generation));
        }

        public static void Reset()
        {
            EndWheelTireSelectionInjection();
            EndSpringSelectionContext();
            CachedItems.Clear();
            CachedGroups.Clear();
            ItemsByUid.Clear();
            GroupsByUid.Clear();
            OfferedItemUids.Clear();
            OfferedGroupUids.Clear();
            EligibleItemUids.Clear();
            EligibleGroupUids.Clear();
            StationItemUids.Clear();
            StationGroupUids.Clear();
            cacheReady = false;
            cacheDirty = true;
            warehouseWindowOpen = false;
            rebuildAfterCloseScheduled = false;
            garageLoadRefreshRunning = false;
            wheelConnectSelectionArmed = false;
            pendingWheelConnectRimUid = 0;
            activePrepareMountSubgroup = WarehouseMountSubgroup.None;
            springConnectSelectionArmed = false;
            pendingSpringConnectBaseUid = 0;
            springFollowUpLookupsRemaining = 0;
            lifecycleGeneration++;
        }

        internal static StationInventoryInjectionState
            BeginStationInventoryInjection(string mode)
        {
            OfferedItemUids.Clear();
            OfferedGroupUids.Clear();
            EligibleItemUids.Clear();
            EligibleGroupUids.Clear();
            WarehouseMountSubgroup subgroup = GetStationSubgroup(mode);
            activePrepareMountSubgroup = subgroup;
            bool isWheelConnect = string.Equals(mode,
                WheelConnectStationMode, StringComparison.Ordinal);
            bool isSpringConnect = string.Equals(mode,
                SpringConnectStationMode, StringComparison.Ordinal);

            if (subgroup == WarehouseMountSubgroup.WheelStations) {
                EndSpringSelectionContext();
                if (isWheelConnect && wheelTireSelectionInjection != null) {
                    return null;
                }

                if (isWheelConnect && IsWheelStationsEnabled) {
                    wheelConnectSelectionArmed = true;
                    pendingWheelConnectRimUid = 0;
                } else {
                    EndWheelTireSelectionInjection();
                    wheelConnectSelectionArmed = false;
                    pendingWheelConnectRimUid = 0;
                }
            } else if (subgroup == WarehouseMountSubgroup.SpringClamp) {
                EndWheelTireSelectionInjection();
                wheelConnectSelectionArmed = false;
                pendingWheelConnectRimUid = 0;
                if (isSpringConnect && IsAnySubgroupEnabled) {
                    springConnectSelectionArmed = true;
                    pendingSpringConnectBaseUid = 0;
                    springFollowUpLookupsRemaining = 0;
                } else {
                    EndSpringSelectionContext();
                }
            } else {
                EndWheelTireSelectionInjection();
                EndSpringSelectionContext();
                wheelConnectSelectionArmed = false;
                pendingWheelConnectRimUid = 0;
            }

            bool injectItems = isWheelConnect || isSpringConnect;
            bool injectGroups = string.Equals(mode, WheelSeparateStationMode,
                    StringComparison.Ordinal) ||
                string.Equals(mode, SpringSeparateStationMode,
                    StringComparison.Ordinal);
            return BeginInventoryInjection(subgroup, injectItems,
                injectGroups);
        }

        internal static void EndStationPrepareMount(string mode)
        {
            WarehouseMountSubgroup subgroup = GetStationSubgroup(mode);
            if (activePrepareMountSubgroup == subgroup)
                activePrepareMountSubgroup = WarehouseMountSubgroup.None;
        }

        internal static StationInventoryInjectionState
            BeginNativeInventoryLookupInjection(string method,
                bool injectItems, bool injectGroups)
        {
            WarehouseMountSubgroup subgroup =
                string.Equals(method, nameof(Inventory.GetAbsorbers),
                    StringComparison.Ordinal)
                    ? WarehouseMountSubgroup.SpringClamp
                    : WarehouseMountSubgroup.WheelStations;

            if (wheelTireSelectionInjection != null &&
                string.Equals(method, nameof(Inventory.GetTiresWithSize),
                    StringComparison.Ordinal))
                return null;

            return BeginInventoryInjection(subgroup, injectItems,
                injectGroups);
        }

        private static WarehouseMountSubgroup GetStationSubgroup(string mode)
        {
            if (string.Equals(mode, WheelConnectStationMode,
                    StringComparison.Ordinal) ||
                string.Equals(mode, WheelSeparateStationMode,
                    StringComparison.Ordinal))
                return WarehouseMountSubgroup.WheelStations;
            if (string.Equals(mode, SpringConnectStationMode,
                    StringComparison.Ordinal) ||
                string.Equals(mode, SpringSeparateStationMode,
                    StringComparison.Ordinal))
                return WarehouseMountSubgroup.SpringClamp;
            return WarehouseMountSubgroup.None;
        }

        private static StationInventoryInjectionState
            BeginInventoryInjection(WarehouseMountSubgroup subgroup,
                bool injectItems, bool injectGroups)
        {
            StationItemUids.Clear();
            StationGroupUids.Clear();

            if (!injectItems && !injectGroups)
                return null;
            if (!IsSubgroupEnabled(subgroup) || warehouseWindowOpen ||
                !EnsureCacheAvailable())
                return null;

            Inventory inventory = Singleton<Inventory>.Instance;
            if (inventory == null ||
                (injectItems && inventory.items == null) ||
                (injectGroups && inventory.groups == null))
                return null;

            StationInventoryInjectionState state =
                new StationInventoryInjectionState {
                    Inventory = inventory
                };

            try {
                if (injectItems)
                    InjectWarehouseItemsForStation(state);
                if (injectGroups)
                    InjectWarehouseGroupsForStation(state);

                return state;
            } catch (Exception) {
                EndStationInventoryInjection(state);
                StationItemUids.Clear();
                StationGroupUids.Clear();
                return null;
            }
        }

        internal static void EndStationInventoryInjection(
            StationInventoryInjectionState state)
        {
            if (state == null || state.Inventory == null)
                return;

            RemoveInjectedStationItems(state);
            RemoveInjectedStationGroups(state);
        }

        internal static void ObserveInventoryItemDeleting(Item item)
        {
            if (!IsAnySubgroupEnabled || warehouseWindowOpen || item == null)
                return;

            if (IsWheelStationsEnabled && wheelConnectSelectionArmed &&
                IsRimItem(item)) {
                EndWheelTireSelectionInjection();
                wheelConnectSelectionArmed = false;
                pendingWheelConnectRimUid = item.UID;
                BeginWheelTireSelectionInjection(item);
            }

            if (springConnectSelectionArmed &&
                IsShockAbsorberItem(item)) {
                springConnectSelectionArmed = false;
                pendingSpringConnectBaseUid = item.UID;
                springFollowUpLookupsRemaining = 2;
            }
        }

        internal static void ObserveInventoryItemDeleted(Item item)
        {
            if (item == null || wheelTireSelectionInjection == null ||
                !IsTireItem(item))
                return;

            EndWheelTireSelectionInjection();
        }

        internal static void ObserveInventoryItemAdded(Item item)
        {
            if (item == null)
                return;

            if (wheelTireSelectionInjection != null &&
                pendingWheelConnectRimUid > 0 &&
                item.UID == pendingWheelConnectRimUid && IsRimItem(item)) {
                EndWheelTireSelectionInjection();
            }

            if (pendingSpringConnectBaseUid > 0 &&
                item.UID == pendingSpringConnectBaseUid &&
                IsShockAbsorberItem(item)) {
                EndSpringSelectionContext();
            }
        }

        internal static void FinishWheelTireSelection()
        {
            EndWheelTireSelectionInjection();
        }

        internal static void FinishSpringSelection()
        {
            EndSpringSelectionContext();
        }

        internal static void BeginGeneralMountSelection()
        {
            OfferedItemUids.Clear();
            OfferedGroupUids.Clear();
            EligibleItemUids.Clear();
            EligibleGroupUids.Clear();
            activePrepareMountSubgroup = WarehouseMountSubgroup.None;
            EndWheelTireSelectionInjection();
            EndSpringSelectionContext();
            StationItemUids.Clear();
            StationGroupUids.Clear();
        }

        private static void BeginWheelTireSelectionInjection(Item rim)
        {
            if (!IsWheelStationsEnabled || warehouseWindowOpen ||
                !EnsureCacheAvailable())
                return;

            Inventory inventory = Singleton<Inventory>.Instance;
            if (inventory == null || inventory.items == null)
                return;

            StationInventoryInjectionState state =
                new StationInventoryInjectionState {
                    Inventory = inventory
                };

            StationItemUids.Clear();
            StationGroupUids.Clear();
            if (rim != null && ItemsByUid.ContainsKey(rim.UID))
                StationItemUids.Add(rim.UID);
            HashSet<long> presentUids = new HashSet<long>();
            foreach (Item item in inventory.items) {
                if (item != null)
                    presentUids.Add(item.UID);
            }

            try {
                foreach (WarehouseItemEntry entry in CachedItems) {
                    Item tire = entry.Item;
                    if (!IsTireItem(tire))
                        continue;

                    StationItemUids.Add(tire.UID);
                    if (!presentUids.Add(tire.UID))
                        continue;

                    inventory.items.Add(tire);
                    state.InjectedItemUids.Add(tire.UID);
                }

                wheelTireSelectionInjection = state;
            } catch (Exception) {
                wheelTireSelectionInjection = state;
                EndWheelTireSelectionInjection();
            }
        }

        private static void EndWheelTireSelectionInjection()
        {
            StationInventoryInjectionState state =
                wheelTireSelectionInjection;
            wheelTireSelectionInjection = null;
            if (state != null) {
                try {
                    EndStationInventoryInjection(state);
                } catch (Exception) {
                }
            }

            StationItemUids.Clear();
            wheelConnectSelectionArmed = false;
            pendingWheelConnectRimUid = 0;
        }

        private static void EndSpringSelectionContext()
        {
            springConnectSelectionArmed = false;
            pendingSpringConnectBaseUid = 0;
            springFollowUpLookupsRemaining = 0;
        }

        private static bool IsShockAbsorberItem(Item item)
        {
            return item != null && !string.IsNullOrEmpty(item.ID) &&
                item.ID.StartsWith("amortyzator",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRimItem(Item item)
        {
            return item != null && !string.IsNullOrEmpty(item.ID) &&
                item.ID.StartsWith("rim_",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTireItem(Item item)
        {
            return item != null && !string.IsNullOrEmpty(item.ID) &&
                item.ID.StartsWith("tire_",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void InjectWarehouseItemsForStation(
            StationInventoryInjectionState state)
        {
            HashSet<long> presentUids = new HashSet<long>();
            foreach (Item item in state.Inventory.items) {
                if (item != null)
                    presentUids.Add(item.UID);
            }

            foreach (WarehouseItemEntry entry in CachedItems) {
                Item item = entry.Item;
                if (item == null)
                    continue;

                StationItemUids.Add(item.UID);
                if (!presentUids.Add(item.UID))
                    continue;

                state.Inventory.items.Add(item);
                state.InjectedItemUids.Add(item.UID);
            }
        }

        private static void InjectWarehouseGroupsForStation(
            StationInventoryInjectionState state)
        {
            HashSet<long> presentUids = new HashSet<long>();
            foreach (GroupItem group in state.Inventory.groups) {
                if (group != null)
                    presentUids.Add(group.UID);
            }

            foreach (WarehouseGroupEntry entry in CachedGroups) {
                GroupItem group = entry.Group;
                if (group == null)
                    continue;

                StationGroupUids.Add(group.UID);
                if (!presentUids.Add(group.UID))
                    continue;

                state.Inventory.groups.Add(group);
                state.InjectedGroupUids.Add(group.UID);
            }
        }

        private static int RemoveInjectedStationItems(
            StationInventoryInjectionState state)
        {
            if (state.Inventory.items == null)
                return 0;

            int removed = 0;
            for (int uidIndex = state.InjectedItemUids.Count - 1;
                uidIndex >= 0; uidIndex--) {
                long uid = state.InjectedItemUids[uidIndex];
                int lastIndex = state.Inventory.items.Count - 1;
                if (lastIndex >= 0) {
                    Item last = state.Inventory.items[lastIndex];
                    if (last != null && last.UID == uid) {
                        state.Inventory.items.RemoveAt(lastIndex);
                        removed++;
                        continue;
                    }
                }

                for (int index = state.Inventory.items.Count - 1;
                    index >= 0; index--) {
                    Item candidate = state.Inventory.items[index];
                    if (candidate == null || candidate.UID != uid)
                        continue;
                    state.Inventory.items.RemoveAt(index);
                    removed++;
                    break;
                }
            }
            return removed;
        }

        private static int RemoveInjectedStationGroups(
            StationInventoryInjectionState state)
        {
            if (state.Inventory.groups == null)
                return 0;

            int removed = 0;
            for (int uidIndex = state.InjectedGroupUids.Count - 1;
                uidIndex >= 0; uidIndex--) {
                long uid = state.InjectedGroupUids[uidIndex];
                int lastIndex = state.Inventory.groups.Count - 1;
                if (lastIndex >= 0) {
                    GroupItem last = state.Inventory.groups[lastIndex];
                    if (last != null && last.UID == uid) {
                        state.Inventory.groups.RemoveAt(lastIndex);
                        removed++;
                        continue;
                    }
                }

                for (int index = state.Inventory.groups.Count - 1;
                    index >= 0; index--) {
                    GroupItem candidate = state.Inventory.groups[index];
                    if (candidate == null || candidate.UID != uid)
                        continue;
                    state.Inventory.groups.RemoveAt(index);
                    removed++;
                    break;
                }
            }
            return removed;
        }

        private static WarehouseMountSubgroup GetItemLookupSubgroup(
            out bool consumesSpringFollowUp)
        {
            consumesSpringFollowUp = false;
            if (activePrepareMountSubgroup != WarehouseMountSubgroup.None)
                return activePrepareMountSubgroup;
            if (springFollowUpLookupsRemaining > 0) {
                consumesSpringFollowUp = true;
                return WarehouseMountSubgroup.SpringClamp;
            }
            return WarehouseMountSubgroup.General;
        }

        internal static void MergeWarehouseItemMatches(string query,
            ref Il2CppSystem.Collections.Generic.List<BaseItem> result)
        {
            OfferedItemUids.Clear();
            if (string.IsNullOrEmpty(query))
                return;

            bool consumesSpringFollowUp;
            WarehouseMountSubgroup subgroup = GetItemLookupSubgroup(
                out consumesSpringFollowUp);
            try {
                if (!IsSubgroupEnabled(subgroup) || warehouseWindowOpen) {
                    return;
                }
                if (!EnsureCacheAvailable())
                    return;

                Inventory inventory = Singleton<Inventory>.Instance;
                Il2CppSystem.Collections.Generic.List<Item> combinedItems =
                    new Il2CppSystem.Collections.Generic.List<Item>(
                        (inventory != null && inventory.items != null
                            ? inventory.items.Count : 0) + CachedItems.Count);
                HashSet<long> includedUids = new HashSet<long>();

                if (inventory != null && inventory.items != null) {
                    foreach (Item item in inventory.items) {
                        if (item != null && includedUids.Add(item.UID))
                            combinedItems.Add(item);
                    }
                }

                foreach (WarehouseItemEntry entry in CachedItems) {
                    Item item = entry.Item;
                    if (item != null && includedUids.Add(item.UID))
                        combinedItems.Add(item);
                }

                Il2CppSystem.Collections.Generic.List<BaseItem> merged = null;
                try {
                    merged = UIHelper.GetItemsForID(combinedItems, query);
                } catch (Exception) {
                }

                if (merged == null) {
                    merged = CopyResult(result);
                    AppendExactWarehouseItemMatches(merged, query);
                }

                TrackWarehouseItemResults(merged);
                result = merged;
            } finally {
                if (consumesSpringFollowUp &&
                    springFollowUpLookupsRemaining > 0) {
                    springFollowUpLookupsRemaining--;
                }
            }
        }

        internal static void MergeWarehouseGroupMatches(string query,
            ref Il2CppSystem.Collections.Generic.List<GroupItem> result)
        {
            if (!IsGeneralMountEnabled || warehouseWindowOpen ||
                string.IsNullOrEmpty(query) ||
                !EnsureCacheAvailable())
                return;

            if (result == null)
                result = new Il2CppSystem.Collections.Generic.List<GroupItem>();

            HashSet<long> presentUids = new HashSet<long>();
            foreach (GroupItem group in result) {
                if (group != null)
                    presentUids.Add(group.UID);
            }

            foreach (WarehouseGroupEntry entry in CachedGroups) {
                GroupItem group = entry.Group;
                if (!IsWarehouseGroupMatch(group, query) ||
                    !presentUids.Add(group.UID))
                    continue;

                result.Add(group);
                OfferedGroupUids.Add(group.UID);
            }

        }

        private static bool IsWarehouseGroupMatch(GroupItem group,
            string query)
        {
            if (group == null || string.IsNullOrEmpty(group.ID))
                return false;

            if (string.Equals(query, AllEnginesGroupQuery,
                StringComparison.OrdinalIgnoreCase))
                return group.ID.StartsWith("engine_",
                    StringComparison.OrdinalIgnoreCase);

            return string.Equals(group.ID, query,
                StringComparison.OrdinalIgnoreCase);
        }

        private static Il2CppSystem.Collections.Generic.List<BaseItem>
            CopyResult(Il2CppSystem.Collections.Generic.List<BaseItem> source)
        {
            int count = source != null ? source.Count : 0;
            Il2CppSystem.Collections.Generic.List<BaseItem> copy =
                new Il2CppSystem.Collections.Generic.List<BaseItem>(count);
            if (source != null) {
                foreach (BaseItem item in source) {
                    if (item != null)
                        copy.Add(item);
                }
            }
            return copy;
        }

        private static void AppendExactWarehouseItemMatches(
            Il2CppSystem.Collections.Generic.List<BaseItem> result,
            string query)
        {
            if (result == null)
                return;

            HashSet<long> presentUids = new HashSet<long>();
            foreach (BaseItem baseItem in result) {
                Item item = baseItem != null ? baseItem.TryCast<Item>() : null;
                if (item != null)
                    presentUids.Add(item.UID);
            }

            foreach (WarehouseItemEntry entry in CachedItems) {
                Item item = entry.Item;
                if (item == null || !presentUids.Add(item.UID))
                    continue;
                if (string.Equals(item.ID, query,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.NormalID, query,
                        StringComparison.OrdinalIgnoreCase))
                    result.Add(item);
            }
        }

        private static void TrackWarehouseItemResults(
            Il2CppSystem.Collections.Generic.List<BaseItem> result)
        {
            if (result == null)
                return;

            foreach (BaseItem baseItem in result) {
                Item item = baseItem != null ? baseItem.TryCast<Item>() : null;
                if (item != null && ItemsByUid.ContainsKey(item.UID))
                    OfferedItemUids.Add(item.UID);
            }
        }

        internal static BaseItem ResolveBaseItemFallback(long uid)
        {
            if (!IsAnySubgroupEnabled || uid <= 0 || warehouseWindowOpen ||
                !EnsureCacheAvailable())
                return null;

            WarehouseItemEntry itemEntry;
            if ((OfferedItemUids.Contains(uid) ||
                    StationItemUids.Contains(uid)) &&
                ItemsByUid.TryGetValue(uid, out itemEntry) &&
                itemEntry.Item != null) {
                EligibleItemUids.Add(uid);
                return itemEntry.Item;
            }

            WarehouseGroupEntry groupEntry;
            if ((OfferedGroupUids.Contains(uid) ||
                    StationGroupUids.Contains(uid)) &&
                GroupsByUid.TryGetValue(uid, out groupEntry) &&
                groupEntry.Group != null) {
                EligibleGroupUids.Add(uid);
                return groupEntry.Group;
            }

            return null;
        }

        internal static GroupItem ResolveGroupFallback(long uid)
        {
            if (!IsAnySubgroupEnabled || uid <= 0 || warehouseWindowOpen ||
                !EnsureCacheAvailable())
                return null;

            WarehouseGroupEntry entry;
            if ((!OfferedGroupUids.Contains(uid) &&
                    !StationGroupUids.Contains(uid)) ||
                !GroupsByUid.TryGetValue(uid, out entry) ||
                entry.Group == null)
                return null;

            EligibleGroupUids.Add(uid);
            return entry.Group;
        }

        private static WarehouseMountSubgroup GetSelectionSubgroup(
            string source)
        {
            if (string.Equals(source, "spring-clamp",
                    StringComparison.Ordinal))
                return WarehouseMountSubgroup.SpringClamp;
            if (string.Equals(source, "tire-changer",
                    StringComparison.Ordinal) ||
                string.Equals(source, "wheel-balancer",
                    StringComparison.Ordinal))
                return WarehouseMountSubgroup.WheelStations;
            return WarehouseMountSubgroup.General;
        }

        internal static void MarkWarehouseGroupSelected(GroupItem group,
            string source)
        {
            WarehouseMountSubgroup subgroup = GetSelectionSubgroup(source);
            if (!IsSubgroupEnabled(subgroup) || warehouseWindowOpen ||
                group == null ||
                !EnsureCacheAvailable() ||
                !GroupsByUid.ContainsKey(group.UID))
                return;

            EligibleGroupUids.Clear();
            EligibleGroupUids.Add(group.UID);
        }

        internal static bool TryDeleteWarehouseItem(Item item)
        {
            if (!IsAnySubgroupEnabled || warehouseWindowOpen || item == null ||
                !ItemsByUid.ContainsKey(item.UID))
                return false;
            if (!EligibleItemUids.Contains(item.UID) &&
                !StationItemUids.Contains(item.UID)) {
                return false;
            }

            Item removedItem;
            if (!RemoveWarehouseItem(item, out removedItem))
                return false;

            RemoveCachedItem(item.UID);
            UiIntegrationBridge.NotifyItemRemoved(removedItem ?? item);
            return true;
        }

        internal static bool TryDeleteWarehouseGroup(long uid)
        {
            if (!IsAnySubgroupEnabled || warehouseWindowOpen || uid <= 0 ||
                !GroupsByUid.ContainsKey(uid))
                return false;
            if (!EligibleGroupUids.Contains(uid) &&
                !StationGroupUids.Contains(uid)) {
                return false;
            }

            GroupItem removedGroup;
            if (!RemoveWarehouseGroup(uid, out removedGroup))
                return false;

            RemoveCachedGroup(uid);
            if (removedGroup != null)
                UiIntegrationBridge.NotifyGroupRemoved(removedGroup);
            return true;
        }


        private static IEnumerator RefreshAfterGarageLoad(int generation)
        {
            int waitedFrames = 0;
            GarageLoader garageLoader = null;
            try {
                while (generation == lifecycleGeneration &&
                    GlobalState.IsGarageSceneActive && IsAnySubgroupEnabled &&
                    waitedFrames < MaximumGarageLoaderWaitFrames) {
                    if (garageLoader == null)
                        garageLoader =
                            UnityEngine.Object.FindObjectOfType<GarageLoader>();
                    if (garageLoader != null && garageLoader.isReady)
                        break;

                    waitedFrames++;
                    yield return new WaitForFixedUpdate();
                }

                if (generation != lifecycleGeneration ||
                    !GlobalState.IsGarageSceneActive || !IsAnySubgroupEnabled)
                    yield break;

                yield return new WaitForSeconds(1f);
                if (!warehouseWindowOpen &&
                    RebuildCache())
                    yield break;

                for (int attempt = 1; attempt <= 10; attempt++) {
                    yield return new WaitForSeconds(1f);
                    if (generation != lifecycleGeneration ||
                        !GlobalState.IsGarageSceneActive || !IsAnySubgroupEnabled)
                        yield break;
                    if (!warehouseWindowOpen &&
                        RebuildCache())
                        yield break;
                }

            } finally {
                if (generation == lifecycleGeneration)
                    garageLoadRefreshRunning = false;
            }
        }

        private static IEnumerator RebuildAfterWarehouseClose()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            rebuildAfterCloseScheduled = false;
            if (IsAnySubgroupEnabled && !warehouseWindowOpen)
                RebuildCache();
        }

        private static bool EnsureCacheAvailable()
        {
            if (cacheReady && !cacheDirty)
                return true;
            if (!IsAnySubgroupEnabled || warehouseWindowOpen)
                return false;
            return RebuildCache();
        }

        private static bool RebuildCache()
        {
            if (!IsAnySubgroupEnabled || warehouseWindowOpen)
                return false;

            Warehouse warehouse = GetWarehouse();
            if (warehouse == null || warehouse.warehouseList == null ||
                warehouse.warehouseGroupList == null)
                return false;

            int unlockedCount = Math.Max(0,
                Warehouse.amountOfUnlockedWarehouses);
            if (warehouse.warehouseList.Count < unlockedCount ||
                warehouse.warehouseGroupList.Count < unlockedCount)
                return false;

            List<WarehouseItemEntry> refreshedItems =
                new List<WarehouseItemEntry>();
            List<WarehouseGroupEntry> refreshedGroups =
                new List<WarehouseGroupEntry>();
            Dictionary<long, WarehouseItemEntry> refreshedItemsByUid =
                new Dictionary<long, WarehouseItemEntry>();
            Dictionary<long, WarehouseGroupEntry> refreshedGroupsByUid =
                new Dictionary<long, WarehouseGroupEntry>();

            try {
                for (int warehouseIndex = 0;
                    warehouseIndex < unlockedCount; warehouseIndex++) {
                    Il2CppSystem.Collections.Generic.List<Item> itemContainer =
                        warehouse.warehouseList[warehouseIndex];
                    if (itemContainer != null) {
                        for (int index = 0; index < itemContainer.Count; index++) {
                            Item item = itemContainer[index];
                            if (item == null)
                                continue;

                            WarehouseItemEntry entry = new WarehouseItemEntry {
                                Item = item,
                                Container = itemContainer
                            };
                            if (refreshedItemsByUid.ContainsKey(item.UID)) {
                                continue;
                            }
                            refreshedItemsByUid.Add(item.UID, entry);
                            refreshedItems.Add(entry);
                        }
                    }

                    Il2CppSystem.Collections.Generic.List<GroupItem>
                        groupContainer =
                            warehouse.warehouseGroupList[warehouseIndex];
                    if (groupContainer == null)
                        continue;

                    for (int index = 0; index < groupContainer.Count; index++) {
                        GroupItem group = groupContainer[index];
                        if (group == null)
                            continue;

                        WarehouseGroupEntry entry = new WarehouseGroupEntry {
                            Group = group,
                            Container = groupContainer
                        };
                        if (refreshedGroupsByUid.ContainsKey(group.UID)) {
                            continue;
                        }
                        refreshedGroupsByUid.Add(group.UID, entry);
                        refreshedGroups.Add(entry);
                    }
                }
            } catch (Exception) {
                return false;
            }

            CachedItems.Clear();
            CachedItems.AddRange(refreshedItems);
            CachedGroups.Clear();
            CachedGroups.AddRange(refreshedGroups);
            ItemsByUid.Clear();
            foreach (KeyValuePair<long, WarehouseItemEntry> entry in
                refreshedItemsByUid)
                ItemsByUid.Add(entry.Key, entry.Value);
            GroupsByUid.Clear();
            foreach (KeyValuePair<long, WarehouseGroupEntry> entry in
                refreshedGroupsByUid)
                GroupsByUid.Add(entry.Key, entry.Value);

            OfferedItemUids.Clear();
            OfferedGroupUids.Clear();
            EligibleItemUids.Clear();
            EligibleGroupUids.Clear();
            StationItemUids.Clear();
            StationGroupUids.Clear();
            cacheReady = true;
            cacheDirty = false;
            return true;
        }

        private static void InvalidateForWarehouseWindowOpen()
        {
            if (!IsAnySubgroupEnabled)
                return;

            EndWheelTireSelectionInjection();
            EndSpringSelectionContext();
            activePrepareMountSubgroup = WarehouseMountSubgroup.None;
            CachedItems.Clear();
            CachedGroups.Clear();
            ItemsByUid.Clear();
            GroupsByUid.Clear();
            OfferedItemUids.Clear();
            OfferedGroupUids.Clear();
            EligibleItemUids.Clear();
            EligibleGroupUids.Clear();
            StationItemUids.Clear();
            StationGroupUids.Clear();
            cacheReady = false;
            cacheDirty = true;
            warehouseWindowOpen = true;
        }

        private static void ScheduleRebuildAfterWarehouseClose()
        {
            if (!IsAnySubgroupEnabled)
                return;

            warehouseWindowOpen = false;
            cacheReady = false;
            cacheDirty = true;
            if (rebuildAfterCloseScheduled)
                return;

            rebuildAfterCloseScheduled = true;
            MelonCoroutines.Start(RebuildAfterWarehouseClose());
        }

        private static bool RemoveWarehouseItem(Item requested,
            out Item removedItem)
        {
            removedItem = null;
            WarehouseItemEntry entry;
            if (ItemsByUid.TryGetValue(requested.UID, out entry) &&
                RemoveItemFromContainer(entry.Container, requested.UID,
                    out removedItem)) {
                return true;
            }

            Warehouse warehouse = GetWarehouse();
            if (warehouse == null || warehouse.warehouseList == null)
                return false;

            int count = Math.Min(Math.Max(0,
                Warehouse.amountOfUnlockedWarehouses),
                warehouse.warehouseList.Count);
            for (int index = 0; index < count; index++) {
                if (!RemoveItemFromContainer(warehouse.warehouseList[index],
                    requested.UID, out removedItem))
                    continue;
                return true;
            }
            return false;
        }

        private static bool RemoveWarehouseGroup(long uid,
            out GroupItem removedGroup)
        {
            removedGroup = null;
            WarehouseGroupEntry entry;
            if (GroupsByUid.TryGetValue(uid, out entry) &&
                RemoveGroupFromContainer(entry.Container, uid,
                    out removedGroup)) {
                return true;
            }

            Warehouse warehouse = GetWarehouse();
            if (warehouse == null || warehouse.warehouseGroupList == null)
                return false;

            int count = Math.Min(Math.Max(0,
                Warehouse.amountOfUnlockedWarehouses),
                warehouse.warehouseGroupList.Count);
            for (int index = 0; index < count; index++) {
                if (!RemoveGroupFromContainer(
                    warehouse.warehouseGroupList[index], uid,
                    out removedGroup))
                    continue;
                return true;
            }
            return false;
        }

        private static bool RemoveItemFromContainer(
            Il2CppSystem.Collections.Generic.List<Item> container, long uid,
            out Item removedItem)
        {
            removedItem = null;
            if (container == null)
                return false;

            for (int index = container.Count - 1; index >= 0; index--) {
                Item candidate = container[index];
                if (candidate == null || candidate.UID != uid)
                    continue;
                removedItem = candidate;
                container.RemoveAt(index);
                return true;
            }
            return false;
        }

        private static bool RemoveGroupFromContainer(
            Il2CppSystem.Collections.Generic.List<GroupItem> container,
            long uid, out GroupItem removedGroup)
        {
            removedGroup = null;
            if (container == null)
                return false;

            for (int index = container.Count - 1; index >= 0; index--) {
                GroupItem candidate = container[index];
                if (candidate == null || candidate.UID != uid)
                    continue;
                removedGroup = candidate;
                container.RemoveAt(index);
                return true;
            }
            return false;
        }

        private static void RemoveCachedItem(long uid)
        {
            OfferedItemUids.Remove(uid);
            EligibleItemUids.Remove(uid);
            StationItemUids.Remove(uid);
            if (!ItemsByUid.ContainsKey(uid))
                return;

            ItemsByUid.Remove(uid);
            for (int index = CachedItems.Count - 1; index >= 0; index--) {
                if (CachedItems[index].Item != null &&
                    CachedItems[index].Item.UID == uid) {
                    CachedItems.RemoveAt(index);
                    break;
                }
            }
        }

        private static void RemoveCachedGroup(long uid)
        {
            OfferedGroupUids.Remove(uid);
            EligibleGroupUids.Remove(uid);
            StationGroupUids.Remove(uid);
            if (!GroupsByUid.ContainsKey(uid))
                return;

            GroupsByUid.Remove(uid);
            for (int index = CachedGroups.Count - 1; index >= 0; index--) {
                if (CachedGroups[index].Group != null &&
                    CachedGroups[index].Group.UID == uid) {
                    CachedGroups.RemoveAt(index);
                    break;
                }
            }
        }

        private static Warehouse GetWarehouse()
        {
            if (GlobalState.GameManager == null)
                GlobalState.GameManager = Singleton<GameManager>.Instance;
            return GlobalState.GameManager != null
                ? GlobalState.GameManager.Warehouse : null;
        }


        [HarmonyPatch(typeof(WarehouseWindow), nameof(WarehouseWindow.Show))]
        [HarmonyPostfix]
        private static void WarehouseWindowShowPostfix()
        {
            InvalidateForWarehouseWindowOpen();
        }

        [HarmonyPatch(typeof(WarehouseWindow), nameof(WarehouseWindow.Hide))]
        [HarmonyPostfix]
        private static void WarehouseWindowHidePostfix()
        {
            ScheduleRebuildAfterWarehouseClose();
        }
    }

    [HarmonyPatch]
    internal static class WarehouseMountGeneralSelectionBoundaryPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PartScript),
                nameof(PartScript.ActionMount),
                new Type[] { typeof(bool) });
            yield return AccessTools.Method(typeof(GameScript),
                nameof(GameScript.BodyMount), Type.EmptyTypes);
            yield return AccessTools.Method(typeof(GameScript),
                nameof(GameScript.ShowSelectPartMount),
                new Type[] { typeof(string) });
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            WarehouseMountSourceFeature.BeginGeneralMountSelection();
        }
    }

    [HarmonyPatch(typeof(ChoosePartUpWindow), "PrepareMount",
        new Type[] { typeof(string) })]
    internal static class WarehouseMountStationInventoryInjectionPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(string __0, out
            WarehouseMountSourceFeature.StationInventoryInjectionState
                __state)
        {
            __state = WarehouseMountSourceFeature
                .BeginStationInventoryInjection(__0);
        }

        [HarmonyFinalizer]
        [HarmonyPriority(Priority.Last)]
        private static Exception Finalizer(Exception __exception,
            string __0,
            WarehouseMountSourceFeature.StationInventoryInjectionState
                __state)
        {
            WarehouseMountSourceFeature.EndStationInventoryInjection(
                __state);
            WarehouseMountSourceFeature.EndStationPrepareMount(__0);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class WarehouseMountNativeStationLookupInjectionPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Inventory),
                nameof(Inventory.GetRims), Type.EmptyTypes);
            yield return AccessTools.Method(typeof(Inventory),
                nameof(Inventory.GetTiresWithSize),
                new Type[] { typeof(int) });
            yield return AccessTools.Method(typeof(Inventory),
                nameof(Inventory.GetAbsorbers), Type.EmptyTypes);
            yield return AccessTools.Method(typeof(Inventory),
                nameof(Inventory.GetUnbalancedWheels), Type.EmptyTypes);
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix(MethodBase __originalMethod, out
            WarehouseMountSourceFeature.StationInventoryInjectionState
                __state)
        {
            string method = __originalMethod != null
                ? __originalMethod.Name : "unknown";
            bool injectItems = string.Equals(method,
                    nameof(Inventory.GetRims), StringComparison.Ordinal) ||
                string.Equals(method, nameof(Inventory.GetTiresWithSize),
                    StringComparison.Ordinal);
            bool injectGroups = string.Equals(method,
                    nameof(Inventory.GetAbsorbers), StringComparison.Ordinal) ||
                string.Equals(method, nameof(Inventory.GetUnbalancedWheels),
                    StringComparison.Ordinal);
            __state = WarehouseMountSourceFeature
                .BeginNativeInventoryLookupInjection(method, injectItems,
                    injectGroups);
        }

        [HarmonyFinalizer]
        [HarmonyPriority(Priority.Last)]
        private static Exception Finalizer(Exception __exception,
            WarehouseMountSourceFeature.StationInventoryInjectionState
                __state)
        {
            WarehouseMountSourceFeature.EndStationInventoryInjection(
                __state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetItems),
        new Type[] { typeof(string) })]
    internal static class WarehouseMountItemsLookupPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        private static void Postfix(string __0,
            ref Il2CppSystem.Collections.Generic.List<BaseItem> __result)
        {
            WarehouseMountSourceFeature.MergeWarehouseItemMatches(__0,
                ref __result);
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetGroupInventory),
        new Type[] { typeof(string) })]
    internal static class WarehouseMountGroupsLookupPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        private static void Postfix(string __0,
            ref Il2CppSystem.Collections.Generic.List<GroupItem> __result)
        {
            WarehouseMountSourceFeature.MergeWarehouseGroupMatches(__0,
                ref __result);
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetBaseItem),
        new Type[] { typeof(string), typeof(long) })]
    internal static class WarehouseMountBaseItemFallbackPatch
    {
        [HarmonyPostfix]
        private static void Postfix(long __1, ref BaseItem __result)
        {
            if (__result == null)
                __result = WarehouseMountSourceFeature
                    .ResolveBaseItemFallback(__1);
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.GetGroup),
        new Type[] { typeof(long) })]
    internal static class WarehouseMountGroupFallbackPatch
    {
        [HarmonyPostfix]
        private static void Postfix(long __0, ref GroupItem __result)
        {
            if (__result == null)
                __result = WarehouseMountSourceFeature
                    .ResolveGroupFallback(__0);
        }
    }

    [HarmonyPatch(typeof(EngineStandLogic),
        nameof(EngineStandLogic.SetGroupOnEngineStand),
        new Type[] { typeof(GroupItem), typeof(bool) })]
    internal static class WarehouseMountEngineStandSelectionPatch
    {
        [HarmonyPrefix]
        private static void Prefix(GroupItem __0)
        {
            WarehouseMountSourceFeature.MarkWarehouseGroupSelected(__0,
                "engine-stand");
        }
    }

    [HarmonyPatch(typeof(SpringClampLogic),
        nameof(SpringClampLogic.SetGroupOnSpringClamp),
        new Type[] { typeof(GroupItem), typeof(bool), typeof(bool) })]
    internal static class WarehouseMountSpringClampSelectionPatch
    {
        [HarmonyPrefix]
        private static void Prefix(GroupItem __0)
        {
            WarehouseMountSourceFeature.MarkWarehouseGroupSelected(__0,
                "spring-clamp");
            WarehouseMountSourceFeature.FinishSpringSelection();
        }
    }

    [HarmonyPatch(typeof(WheelBalancerLogic),
        nameof(WheelBalancerLogic.SetGroupOnWheelBalancer),
        new Type[] { typeof(GroupItem), typeof(bool) })]
    internal static class WarehouseMountWheelBalancerSelectionPatch
    {
        [HarmonyPrefix]
        private static void Prefix(GroupItem __0)
        {
            WarehouseMountSourceFeature.MarkWarehouseGroupSelected(__0,
                "wheel-balancer");
        }
    }

    [HarmonyPatch(typeof(TireChangerLogic),
        nameof(TireChangerLogic.SetGroupOnTireChanger),
        new Type[] { typeof(GroupItem), typeof(bool), typeof(bool) })]
    internal static class WarehouseMountTireChangerSelectionPatch
    {
        [HarmonyPrefix]
        private static void Prefix(GroupItem __0)
        {
            WarehouseMountSourceFeature.FinishWheelTireSelection();
            WarehouseMountSourceFeature.MarkWarehouseGroupSelected(__0,
                "tire-changer");
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.Add),
        new Type[] { typeof(Item), typeof(bool) })]
    internal static class WarehouseMountItemAddPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Item __0)
        {
            WarehouseMountSourceFeature.ObserveInventoryItemAdded(__0);
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.Delete),
        new Type[] { typeof(Item) })]
    internal static class WarehouseMountItemDeletePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Item __0)
        {
            WarehouseMountSourceFeature.ObserveInventoryItemDeleting(__0);
            bool redirected = WarehouseMountSourceFeature
                .TryDeleteWarehouseItem(__0);
            WarehouseMountSourceFeature.ObserveInventoryItemDeleted(__0);
            return !redirected;
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.DeleteGroup),
        new Type[] { typeof(long) })]
    internal static class WarehouseMountGroupDeletePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(long __0)
        {
            return !WarehouseMountSourceFeature
                .TryDeleteWarehouseGroup(__0);
        }
    }
}
