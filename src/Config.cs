using System;

#if NET6_0_OR_GREATER
using Il2Cpp;
#else
using CMS;
#endif

namespace Cms21GameplayPlus
{
    public sealed class Settings
    {
        [Tomlet.Attributes.TomlInlineComment("Skip supported repair, tuning, auction, balance and alignment minigames")]
        public bool bypassMinigames = false;
        [Tomlet.Attributes.TomlInlineComment("Allow repairing drum brakes on the brake lathe")]
        public bool allowBrakeLatheFixDrumBrake = true;
        [Tomlet.Attributes.TomlInlineComment("Force the junkyard generator to use its maximum car percentage")]
        public bool junkyardVehicleMaximumCars = false;
        [Tomlet.Attributes.TomlInlineComment("Expand normal and salvage auction pools")]
        public bool expandedAuctionCarPool = false;
        [Tomlet.Attributes.TomlInlineComment("Remember the seven vanilla garage, paint-shop and office doors")]
        public bool rememberGarageDoorState = true;
        [Tomlet.Attributes.TomlInlineComment("Include warehouse parts during normal vehicle and engine assembly")]
        public bool includeWarehousePartsInMountSelection = true;
        [Tomlet.Attributes.TomlInlineComment("Include warehouse parts on the tire changer and wheel balancer")]
        public bool includeWarehousePartsInWheelStations = true;
        [Tomlet.Attributes.TomlInlineComment("Include warehouse parts on the spring clamp")]
        public bool includeWarehousePartsInSpringClamp = true;
        [Tomlet.Attributes.TomlInlineComment("Allow travel to the junkyard and barns when vehicle storage is full")]
        public bool allowPartsTravelWhenVehicleStorageIsFull = true;
        [Tomlet.Attributes.TomlInlineComment("Relocate the engine hoist next to the engine stand")]
        public bool relocateEngineHoistNearStand = false;
    }

    public static class GlobalConfig
    {
        public static readonly string cfgFile = @"Mods\CMS21GameplayPlus\CMS21GameplayPlus.cfg";
        public static readonly string cfgProfile = @"Mods\CMS21GameplayPlus\ProfileMemory.dat";
        public static readonly string cfgRepairability = @"Mods\CMS21GameplayPlus\Repairability.cfg";
    }

    public static class GlobalState
    {
        public static bool IsGarageSceneActive;
        public static int LoadedProfileId;
        public static GameManager GameManager;
    }

    public static class Types
    {
        public enum LoggingLevels { Normal, NormalClean, Debug, PlayerLog, Warning, Error }

        public sealed class ProfileMemoryData
        {
            [Tomlet.Attributes.TomlInlineComment("There should be no reason to edit this file manually")]
            public string lastCMS21GameplayPlusVersion = string.Empty;
            public ProfileState[] profileStates;
        }

        public sealed class ProfileState
        {
            public bool garageDoorLeftWasOpen;
            public bool garageDoorRightWasOpen;
            public bool paintShopDoorRightWasOpen;
            public bool paintShopDoorLeftWasOpen;
            public bool paintShopDoorSmallWasOpen;
            public bool officeDoorLeftWasOpen;
            public bool officeDoorRightWasOpen;
        }
    }
}
