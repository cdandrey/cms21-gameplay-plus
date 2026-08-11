using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MelonLoader;
using UnityEngine;

namespace Cms21GameplayPlus
{
    /// <summary>Central console logging and optional Unity Player.log interception.</summary>
    public static class ModLogger
    {
        private static readonly Action<string, string, LogType> UnityLogHandler =
            new Action<string, string, LogType>(HandleUnityLog);

        private static readonly string[] IgnoredUnityMessages = {
            "[CarLoader] -> SetRustMap() Cannot load rustmap",
            "[CarLoader] -> SetRustMap() Cannot load tuning rustmap.",
            "within the maximum polygons limit",
            "BoxColliders does not support negative scale or size.",
            "CarLoader.SetCarPaintType (CarPart part, PaintType paintType)",
            "CarBundleManager:TryRemoveCarBundle(String)",
            "Rewired: [WARNING] The Action \"UIOrbit\" does not exist. You can create Actions in",
            "Rewired: [WARNING] The Action \"CameraScroll\" does not exist. You can create Actions in",
            "[WindowManager] SetWindowActive(Showroom, true) Window is currently active",
            "Max shadow requests count reached",
            "[SteamAchievements] -> UpdateStatistics() Failed to get stat value ",
            "[SteamAchievements] -> UpdateStatistics() Failed to get sandbox stat value ",
            "CMS.Platforms.Steam.SteamWorkshopUploader:UpdateItemData()",
            "CMS.Platforms.XboxGameCore.XboxGameCoreUserManager:set_CurrentState(State)",
            "CMS.Platforms.XboxGameCore.XboxGameCoreConnectedStorageManager:Load(String)",
            "CMS.Platforms.XboxGameCore.<CoProcessAddUser>d__24:MoveNext()",
            "No item with name EMPTY",
            "[WindowManager] SetWindowActive(SaveDetails, true) Window is currently active",
            "Graphics.CopyTexture source and destination have different master texture limits.",
            "Particle System is trying to spawn on a mesh with zero surface area",
            "requires Read/Write Enabled to be set in the importer to work on the particle system shape module",
            "Invalid key name:",
            "[Inventory] -> GetBaseItem() Not found"
        };

        private static string lastUnityApplicationLogMessage = string.Empty;
        private static bool unityLogForwardingEnabled;
        private static bool unityLogListenerRegistered;

        public static void ConfigureUnityLogForwarding(bool enabled)
        {
            unityLogForwardingEnabled = enabled;
            try {
                if (enabled && !unityLogListenerRegistered) {
                    Application.add_logMessageReceived(UnityLogHandler);
                    unityLogListenerRegistered = true;
                } else if (!enabled && unityLogListenerRegistered) {
                    Application.remove_logMessageReceived(UnityLogHandler);
                    unityLogListenerRegistered = false;
                }
            } catch (Exception exception) {
                Log("[Startup] Unity log listener state could not be changed." +
                    Environment.NewLine + exception, Types.LoggingLevels.Warning);
            }
        }

        public static void Shutdown()
        {
            unityLogForwardingEnabled = false;
            if (!unityLogListenerRegistered)
                return;

            try {
                Application.remove_logMessageReceived(UnityLogHandler);
            } catch (Exception exception) {
                Log("[Shutdown] Unity log listener was not removed." +
                    Environment.NewLine + exception, Types.LoggingLevels.Warning);
            } finally {
                unityLogListenerRegistered = false;
                lastUnityApplicationLogMessage = string.Empty;
            }
        }

        public static void Log(string msg = "",
            Types.LoggingLevels loggingLevel = Types.LoggingLevels.Debug,
            [CallerMemberName] string callerName = "",
            [CallerLineNumber] int lineNumber = 0)
        {
            msg = (msg ?? string.Empty).Replace("\r\n", "\n");
            if (loggingLevel == Types.LoggingLevels.Debug)
                return;

#if NET6_0_OR_GREATER
            MelonLogger.Instance loggerInstance = Melon<Cms21GameplayPlus.Main>.Logger;
            System.Drawing.Color modColor = System.Drawing.Color.FromArgb(4, 163, 204);
            switch (loggingLevel) {
                case Types.LoggingLevels.Normal:
                    loggerInstance.Msg(modColor,
                        string.Format("[{0}:{1}] {2}", callerName, lineNumber, msg));
                    break;
                case Types.LoggingLevels.NormalClean:
                    MelonLogger.Msg(modColor, msg);
                    break;
#else
            switch (loggingLevel) {
                case Types.LoggingLevels.Normal:
                    MelonLogger.Msg(string.Format("[{0}:{1}] {2}",
                        callerName, lineNumber, msg));
                    break;
                case Types.LoggingLevels.NormalClean:
                    MelonLogger.Msg(msg);
                    break;
#endif
                case Types.LoggingLevels.PlayerLog:
                    Debug.Log(string.Format("CMS21GameplayPlus[{0}():{1}] {2}",
                        callerName, lineNumber, msg));
                    break;
                case Types.LoggingLevels.Warning:
                    MelonLogger.Warning(string.Format("[{0}():{1}] {2}",
                        callerName, lineNumber, msg));
                    break;
                case Types.LoggingLevels.Error:
                    MelonLogger.Error(string.Format("[{0}():{1}] {2}",
                        callerName, lineNumber, msg));
                    break;
            }
        }

        private static void HandleUnityLog(string condition, string stackTrace,
            LogType type)
        {
            if (condition == null || stackTrace == null || !unityLogForwardingEnabled)
                return;

            string dedupeKey = condition + stackTrace + type;
            if (lastUnityApplicationLogMessage == dedupeKey)
                return;
            lastUnityApplicationLogMessage = dedupeKey;

            condition = condition.Replace("\r\n", "\n");
            stackTrace = stackTrace.Replace(
                "(at <00000000000000000000000000000000>:0)", string.Empty)
                .Replace("\r\n", "\n");

            List<string> stackLines = new List<string>((stackTrace + "\n\n")
                .Split(new[] { '\n' }));
            RemoveKnownWrapper(stackLines, "UnityEngine.Logger:Log(LogType, Object)");
            RemoveKnownWrapper(stackLines, "UnityEngine.Debug:Log(Object)");
            RemoveKnownWrapper(stackLines, "UnityEngine.Debug:LogError(Object)");
            RemoveKnownWrapper(stackLines, "UnityEngine.Debug:LogWarning(Object)");
            RemoveKnownWrapper(stackLines,
                "CMS.Platforms.Steam.SteamSettings:AutoLowerSettingsOnLowSpec()");
            RemoveKnownWrapper(stackLines,
                "CMS.Platforms.XboxGameCore.PCGameCoreSettings:AutoLowerSettingsOnLowSpec()");

            string firstStackLine = stackLines.Count > 0
                ? stackLines[0]
                : string.Empty;
            foreach (string ignored in IgnoredUnityMessages) {
                if (condition.Contains(ignored) || firstStackLine.Contains(ignored))
                    return;
            }

            MelonDebug.Msg("Player.log[" + type + ":" + firstStackLine + "] " +
                condition);
        }

        private static void RemoveKnownWrapper(List<string> stackLines, string prefix)
        {
            if (stackLines.Count > 0 && stackLines[0].StartsWith(prefix))
                stackLines.RemoveAt(0);
        }
    }
}
