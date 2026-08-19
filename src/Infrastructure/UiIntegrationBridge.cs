using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

#if NET6_0_OR_GREATER
using Il2CppCMS.Containers;
#else
using CMS.Containers;
#endif

namespace Cms21GameplayPlus
{
    internal static class UiIntegrationBridge
    {
        internal sealed class NativeHintHandle
        {
            public object Row;
            public object Hint;
            public RectTransform Rect;
            public Bounds LocalVisualBounds;
            public bool HasLocalVisualBounds;
        }

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
        private static MethodInfo createNativeFooterHint;
        private static MethodInfo updateFooterHint;
        private static MethodInfo setFooterHintActive;
        private static MethodInfo tryGetControlHintVisualBounds;
        private static MethodInfo destroyControlHintRow;
        private static bool cacheMethodsResolved;
        private static bool repairabilityMethodResolved;
        private static bool dependencyMethodResolved;
        private static bool nativeHintMethodsResolved;

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

        public static NativeHintHandle CreateNativeFooterHint(
            object source, string name, string[] keys, string text,
            bool canHold, float timeToHold)
        {
            if (source == null)
                return null;
            EnsureNativeHintMethods();
            if (createNativeFooterHint == null)
                return null;

            try {
                ParameterInfo[] parameters =
                    createNativeFooterHint.GetParameters();
                if (parameters == null || parameters.Length != 10 ||
                    !parameters[0].ParameterType.IsInstanceOfType(source))
                    return null;
                object inputHandlingMethod;
                try {
                    inputHandlingMethod = Enum.Parse(
                        parameters[6].ParameterType, "ButtonDown");
                } catch {
                    inputHandlingMethod = Activator.CreateInstance(
                        parameters[6].ParameterType);
                }
                object row = createNativeFooterHint.Invoke(null,
                    new object[] { source, name, keys, text, null, null,
                        inputHandlingMethod, canHold, timeToHold, true });
                if (row == null)
                    return null;

                object hintsValue = ReadMember(row, "Hints");
                IList hints = hintsValue as IList;
                if (hints == null || hints.Count == 0) {
                    InvokeSafely(destroyControlHintRow, row);
                    return null;
                }
                NativeHintHandle handle = new NativeHintHandle {
                    Row = row,
                    Hint = hints[0],
                    Rect = ReadMember(hints[0], "Rect") as RectTransform,
                };
                RefreshNativeFooterHintVisualBounds(handle);
                return handle;
            } catch (Exception exception) {
                ModLogger.Log("[UIIntegration] Native hint creation failed." +
                    Environment.NewLine + exception,
                    Types.LoggingLevels.Warning);
                return null;
            }
        }

        public static bool ReparentNativeFooterHint(
            NativeHintHandle handle, Transform parent)
        {
            if (handle == null || handle.Rect == null || parent == null)
                return false;
            try {
                handle.Rect.SetParent(parent, true);
                handle.Rect.SetAsLastSibling();
                WriteMember(handle.Row, "Parent", parent);
                return handle.Rect.parent == parent;
            } catch (Exception exception) {
                ModLogger.Log("[UIIntegration] Native hint reparent failed." +
                    Environment.NewLine + exception,
                    Types.LoggingLevels.Warning);
                return false;
            }
        }

        public static void UpdateNativeFooterHint(NativeHintHandle handle,
            string text, bool active)
        {
            if (handle == null || handle.Hint == null)
                return;
            EnsureNativeHintMethods();
            InvokeSafely(updateFooterHint, handle.Hint, text, active);
            InvokeSafely(setFooterHintActive, handle.Hint, active);
            object description = ReadMember(handle.Hint, "Description");
            WriteMember(description, "canRunUpdate", false);
            WriteMember(description, "blockInput", true);
            WriteMember(description, "blockMouseInput", true);
            WriteMember(description, "blockKeyboardInput", true);
            RefreshNativeFooterHintVisualBounds(handle);
        }

        public static void RefreshNativeFooterHintVisualBounds(
            NativeHintHandle handle)
        {
            if (handle == null || handle.Hint == null ||
                handle.Rect == null)
                return;
            EnsureNativeHintMethods();
            if (tryGetControlHintVisualBounds == null) {
                handle.HasLocalVisualBounds = false;
                return;
            }

            try {
                object[] arguments = new object[] {
                    handle.Hint, handle.Rect, new Bounds()
                };
                object result = tryGetControlHintVisualBounds.Invoke(
                    null, arguments);
                if (!(result is bool) || !(bool)result ||
                    !(arguments[2] is Bounds)) {
                    handle.HasLocalVisualBounds = false;
                    return;
                }
                handle.LocalVisualBounds = (Bounds)arguments[2];
                handle.HasLocalVisualBounds = true;
            } catch {
                handle.HasLocalVisualBounds = false;
            }
        }

        public static void SetNativeFooterHintHoldProgress(
            NativeHintHandle handle, float progress)
        {
            if (handle == null || handle.Hint == null)
                return;
            object description = ReadMember(handle.Hint, "Description");
            if (description == null)
                return;

            float fill = Mathf.Clamp01(progress);
            UnityEngine.UI.Image buttonFill =
                ReadMember(description, "buttonFill") as UnityEngine.UI.Image;
            if (buttonFill != null)
                buttonFill.fillAmount = fill;
            WriteMember(description, "holdTime", 0f);
            WriteMember(description, "eventInvoked", false);
            WriteMember(description, "eventInvoking", false);
            WriteMember(description, "mouseDown", false);
        }

        public static void DestroyNativeFooterHint(NativeHintHandle handle)
        {
            if (handle == null)
                return;
            EnsureNativeHintMethods();
            if (handle.Row != null)
                InvokeSafely(destroyControlHintRow, handle.Row);
            handle.Row = null;
            handle.Hint = null;
            handle.Rect = null;
            handle.HasLocalVisualBounds = false;
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

        private static void EnsureNativeHintMethods()
        {
            if (nativeHintMethodsResolved)
                return;
            nativeHintMethodsResolved = true;

            Type factoryType = Type.GetType(
                "Cms21UiPlus.NativeUiFactory, " + UiAssemblyName, false);
            if (factoryType == null)
                return;

            MethodInfo[] methods = factoryType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static);
            for (int index = 0; index < methods.Length; index++) {
                MethodInfo method = methods[index];
                if (method == null)
                    continue;
                int parameterCount = method.GetParameters().Length;
                if (string.Equals(method.Name, "CreateNativeFooterHint",
                        StringComparison.Ordinal) && parameterCount == 10)
                    createNativeFooterHint = method;
                else if (string.Equals(method.Name, "UpdateFooterHint",
                        StringComparison.Ordinal) && parameterCount == 3)
                    updateFooterHint = method;
                else if (string.Equals(method.Name, "SetFooterHintActive",
                        StringComparison.Ordinal) && parameterCount == 2)
                    setFooterHintActive = method;
                else if (string.Equals(method.Name,
                        "TryGetControlHintVisualBounds",
                        StringComparison.Ordinal) && parameterCount == 3)
                    tryGetControlHintVisualBounds = method;
                else if (string.Equals(method.Name,
                        "DestroyControlHintRow", StringComparison.Ordinal) &&
                    parameterCount == 1)
                    destroyControlHintRow = method;
            }
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

        private static object ReadMember(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name))
                return null;
            Type type = target.GetType();
            try {
                PropertyInfo property = type.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic);
                if (property != null)
                    return property.GetValue(target, null);
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic);
                return field != null ? field.GetValue(target) : null;
            } catch {
                return null;
            }
        }

        private static void WriteMember(object target, string name,
            object value)
        {
            if (target == null || string.IsNullOrEmpty(name))
                return;
            Type type = target.GetType();
            try {
                PropertyInfo property = type.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic);
                if (property != null && property.CanWrite) {
                    property.SetValue(target, value, null);
                    return;
                }
                FieldInfo field = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic);
                if (field != null)
                    field.SetValue(target, value);
            } catch {
            }
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
