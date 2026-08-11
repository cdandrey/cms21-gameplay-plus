namespace Cms21GameplayPlus
{
    /// <summary>Shared activation gate for all minigame bypass submodules.</summary>
    public static class MinigameBypassFeature
    {
        public static bool IsEnabled {
            get {
                return Main.SettingsEntry != null &&
                    Main.SettingsEntry.Value.bypassMinigames;
            }
        }
    }
}
