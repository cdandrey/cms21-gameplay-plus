namespace Cms21GameplayPlus
{
    /// <summary>Activation gates for individual minigame bypass submodules.</summary>
    public static class MinigameBypassFeature
    {
        public static bool IsPartRepairEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.bypassPartRepairMinigame;
            }
        }

        public static bool IsWheelBalanceEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.bypassWheelBalanceMinigame;
            }
        }

        public static bool IsWheelAlignmentEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.bypassWheelAlignmentMinigame;
            }
        }

        public static bool IsHeadlampAlignmentEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.bypassHeadlampAlignmentMinigame;
            }
        }

        public static bool IsAuctionEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.bypassAuctionMinigame;
            }
        }

        public static bool IsCarburetorTuningEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.bypassCarburetorTuningMinigame;
            }
        }

        public static bool IsEcuTuningEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.bypassEcuTuningMinigame;
            }
        }
    }
}
