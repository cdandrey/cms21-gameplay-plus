using System;
using System.Reflection;

#if NET6_0_OR_GREATER
using Il2CppCMS.Containers;
#else
using CMS.Containers;
#endif

namespace Cms21GameplayPlus
{
    internal static class UiIntegrationBridge
    {
        private const string UiAssemblyName = "CMS21UIPlus";
        private static MethodInfo notifyItemRemoved;
        private static MethodInfo notifyGroupRemoved;
        private const string BrakeLatheDrumDependency =
            "BrakeLatheDrumRepairable";
        private const string BrakeLatheGearsDependency =
            "BrakeLatheGearsRepairable";
        private const string BrakeLatheClutchDiscsDependency =
            "BrakeLatheClutchDiscsRepairable";
        private const string BrakeLathePulleysDependency =
            "BrakeLathePulleysRepairable";
        private const string BrakeLatheDrumDefaultDependency =
            "BrakeLatheDrumRepairableDefault";
        private const string BrakeLatheGearsDefaultDependency =
            "BrakeLatheGearsRepairableDefault";
        private const string BrakeLatheClutchDiscsDefaultDependency =
            "BrakeLatheClutchDiscsRepairableDefault";
        private const string BrakeLathePulleysDefaultDependency =
            "BrakeLathePulleysRepairableDefault";

        private static MethodInfo setBrakeDrumRepairable;
        private static MethodInfo setSettingDependencyStatus;
        private static MethodInfo setSettingDependencyAvailable;
        private static bool cacheMethodsResolved;
        private static bool repairabilityMethodResolved;
        private static bool dependencyMethodResolved;

        public static void NotifyItemRemoved(Item item)
        {
            if (item == null)
                return;
            EnsureCacheMethods();
            InvokeSafely(notifyItemRemoved, item);
        }

        public static void NotifyGroupRemoved(GroupItem group)
        {
            if (group == null)
                return;
            EnsureCacheMethods();
            InvokeSafely(notifyGroupRemoved, group);
        }

        public static void SyncBrakeDrumRepairability(bool repairable)
        {
            EnsureRepairabilityMethod();
            InvokeSafely(setBrakeDrumRepairable, repairable);
        }

        public static void SyncBrakeLatheRepairabilityDependencies(
            BrakeLatheRepairabilityStatus drum,
            BrakeLatheRepairabilityStatus gears,
            BrakeLatheRepairabilityStatus clutchDiscs,
            BrakeLatheRepairabilityStatus pulleys,
            BrakeLatheRepairabilityStatus defaultDrum,
            BrakeLatheRepairabilityStatus defaultGears,
            BrakeLatheRepairabilityStatus defaultClutchDiscs,
            BrakeLatheRepairabilityStatus defaultPulleys)
        {
            EnsureDependencyMethod();
            SetDependency(BrakeLatheDrumDependency, drum);
            SetDependency(BrakeLatheGearsDependency, gears);
            SetDependency(BrakeLatheClutchDiscsDependency, clutchDiscs);
            SetDependency(BrakeLathePulleysDependency, pulleys);
            SetDependency(BrakeLatheDrumDefaultDependency, defaultDrum);
            SetDependency(BrakeLatheGearsDefaultDependency, defaultGears);
            SetDependency(BrakeLatheClutchDiscsDefaultDependency,
                defaultClutchDiscs);
            SetDependency(BrakeLathePulleysDefaultDependency,
                defaultPulleys);
        }

        private static void EnsureCacheMethods()
        {
            if (cacheMethodsResolved)
                return;

            Type cacheType = Type.GetType(
                "Cms21UiPlus.OwnedPartCache, " + UiAssemblyName, false);
            if (cacheType == null)
                return;

            notifyItemRemoved = cacheType.GetMethod("NotifyItemRemoved",
                BindingFlags.Public | BindingFlags.Static);
            notifyGroupRemoved = cacheType.GetMethod("NotifyGroupRemoved",
                BindingFlags.Public | BindingFlags.Static);
            cacheMethodsResolved = true;
        }

        private static void EnsureRepairabilityMethod()
        {
            if (repairabilityMethodResolved)
                return;

            Type rulesType = Type.GetType(
                "Cms21UiPlus.PartRepairabilityRules, " + UiAssemblyName, false);
            if (rulesType == null)
                return;

            setBrakeDrumRepairable = rulesType.GetMethod("SetBrakeDrumRepairable",
                BindingFlags.Public | BindingFlags.Static);
            repairabilityMethodResolved = true;
        }

        private static void EnsureDependencyMethod()
        {
            if (dependencyMethodResolved)
                return;

            Type registryType = Type.GetType(
                "Cms21UiPlus.ModSettingDependencyRegistry, " +
                UiAssemblyName, false);
            if (registryType == null)
                return;

            setSettingDependencyStatus = registryType.GetMethod(
                "SetStatus", BindingFlags.Public | BindingFlags.Static);
            setSettingDependencyAvailable = registryType.GetMethod(
                "SetAvailable", BindingFlags.Public | BindingFlags.Static);
            dependencyMethodResolved = true;
        }

        private static void SetDependency(string dependencyId,
            BrakeLatheRepairabilityStatus status)
        {
            if (setSettingDependencyStatus != null) {
                InvokeSafely(setSettingDependencyStatus,
                    BuildInfo.TechnicalName, dependencyId, status.ToString());
                return;
            }

            InvokeSafely(setSettingDependencyAvailable,
                BuildInfo.TechnicalName, dependencyId,
                BrakeLatheExtensionsFeature.IsAvailable(status));
        }

        private static void InvokeSafely(MethodInfo method,
            params object[] values)
        {
            if (method == null)
                return;
            try {
                method.Invoke(null, values);
            } catch (Exception exception) {
                ModLogger.Log("[UIIntegration] Optional UI integration call failed." +
                    Environment.NewLine + exception, Types.LoggingLevels.Warning);
            }
        }
    }
}
