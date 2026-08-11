using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Cms21GameplayPlus
{
    internal static class PartsTravelFeature
    {
        [ThreadStatic]
        private static bool allowCurrentPanel;
        [ThreadStatic]
        private static bool storageCapacityProblem;

        private static bool IsEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value
                        .allowPartsTravelWhenVehicleStorageIsFull;
            }
        }

        internal static void BeginPanelFill(object[] arguments)
        {
            allowCurrentPanel = false;
            storageCapacityProblem = false;
            if (!IsEnabled || arguments == null)
                return;
            foreach (object argument in arguments) {
                if (argument != null && argument.GetType().IsEnum &&
                    IsSupportedDestination(argument.ToString())) {
                    allowCurrentPanel = true;
                    break;
                }
            }
        }

        internal static void CompletePanelFill()
        {
            allowCurrentPanel = false;
            storageCapacityProblem = false;
        }

        internal static void OverrideDriveButtonState(ref bool canUse)
        {
            if (IsEnabled && allowCurrentPanel && storageCapacityProblem &&
                !canUse)
                canUse = true;
        }

        internal static void OverrideProblemDescription(ref string text)
        {
            if (!IsEnabled || !allowCurrentPanel ||
                !IsStorageCapacityMessage(text))
                return;
            storageCapacityProblem = true;
            text = string.Empty;
        }

        private static bool IsSupportedDestination(string destination)
        {
            return string.Equals(destination, "Junkyard",
                       StringComparison.Ordinal) ||
                string.Equals(destination, "Barn", StringComparison.Ordinal);
        }

        private static bool IsStorageCapacityMessage(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            string value = text.ToLowerInvariant();
            return (value.IndexOf("свободн", StringComparison.Ordinal) >= 0 &&
                    value.IndexOf("мест", StringComparison.Ordinal) >= 0 &&
                    value.IndexOf("автомоб", StringComparison.Ordinal) >= 0) ||
                (value.IndexOf("free", StringComparison.Ordinal) >= 0 &&
                 value.IndexOf("car", StringComparison.Ordinal) >= 0 &&
                 (value.IndexOf("space", StringComparison.Ordinal) >= 0 ||
                  value.IndexOf("slot", StringComparison.Ordinal) >= 0));
        }
    }

    [HarmonyPatch]
    internal static class MapDestinationPanelPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type type = AccessTools.TypeByName("CMS.UI.Windows.MapWindow");
            if (type == null)
                yield break;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type)) {
                if (method.Name == "FillPanelForDestination")
                    yield return method;
            }
        }

        private static void Prefix(object[] __args)
        {
            PartsTravelFeature.BeginPanelFill(__args);
        }

        private static void Postfix()
        {
            PartsTravelFeature.CompletePanelFill();
        }
    }

    [HarmonyPatch]
    internal static class MapStorageDriveButtonPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type type = AccessTools.TypeByName(
                "CMS.UI.Logic.Map.Info.MapInfoPanel");
            if (type == null)
                yield break;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type)) {
                if (method.Name == "SetCanUseThumb")
                    yield return method;
            }
        }

        private static void Prefix(ref bool __0)
        {
            PartsTravelFeature.OverrideDriveButtonState(ref __0);
        }
    }

    [HarmonyPatch]
    internal static class MapStorageProblemDescriptionPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName(
                "CMS.UI.Logic.Map.Info.MapInfoPanel");
            return type != null ? AccessTools.Method(type,
                "SetProblemDescription", new Type[] { typeof(string) }) : null;
        }

        private static void Prefix(ref string __0)
        {
            PartsTravelFeature.OverrideProblemDescription(ref __0);
        }
    }
}
