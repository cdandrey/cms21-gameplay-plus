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
        private static MethodInfo setBrakeDrumRepairable;
        private static bool cacheMethodsResolved;
        private static bool repairabilityMethodResolved;

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

        private static void InvokeSafely(MethodInfo method, object value)
        {
            if (method == null)
                return;
            try {
                method.Invoke(null, new object[] { value });
            } catch (Exception exception) {
                ModLogger.Log("[UIIntegration] Optional UI integration call failed." +
                    Environment.NewLine + exception, Types.LoggingLevels.Warning);
            }
        }
    }
}
