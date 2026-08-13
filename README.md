# CMS21 Gameplay+

**CMS21 Gameplay+** is a Car Mechanic Simulator 2021 mod focused on gameplay rules,
repairability, warehouse-backed assembly and small gameplay improvements.

- Display name: **CMS21 Gameplay+**
- Short name: **CMS21 Gameplay+**
- Technical name and assembly: `CMS21GameplayPlus`
- DLL: `CMS21GameplayPlus.dll`
- Runtime directory: `Mods\CMS21GameplayPlus\`
- Main configuration: `Mods\CMS21GameplayPlus\CMS21GameplayPlus.cfg`
- Repository name: `cms21-gameplay-plus`
- Questions: `cdandrey@gmail.com` — include `CMS21Gameplay+` in the subject line

## Relationship to QoLmod

CMS21 Gameplay+ is a substantially refactored and reduced derivative of **QoLmod** by
**Meitzi**, originally published at <https://www.nexusmods.com/carmechanicsimulator2021/mods/105>
and licensed under GNU GPL v3. Selected QoLmod feature ideas are retained; the retained and
removed feature lists are summarized in [QoLmod origin](#qolmod-origin).

## Features and settings

All switches below are stored under `[CMS21GameplayPlus.Settings]` in
`CMS21GameplayPlus.cfg`. Displayed names and apply modes are taken from the in-game settings
manifest. The seven minigame bypass switches, `allowPartsTravelWhenVehicleStorageIsFull` and
`brakeLatheProcessingDuration` use `immediate`; the remaining listed switches use `restartGame`.

### Inventory

| In-game setting | Config flag | Default | Detailed behavior |
|---|---|---:|---|
| **Warehouse parts during normal assembly** | `includeWarehousePartsInMountSelection` | `true` | Makes compatible loose parts and complete assemblies from every unlocked warehouse available during normal vehicle and engine assembly, including interior parts, tuned alternatives, engine groups and supported native mount paths. The tire changer, wheel balancer and spring clamp are excluded and controlled by their own switches. |
| **Warehouse parts on wheel machines** | `includeWarehousePartsInWheelStations` | `true` | Makes warehouse rims and compatible tires available while assembling a wheel, complete wheels available while disassembling on the tire changer, and unbalanced complete wheels available on the wheel balancer. The selected warehouse object is removed from its original warehouse only after the machine accepts it. |
| **Warehouse parts on spring clamp** | `includeWarehousePartsInSpringClamp` | `true` | Makes warehouse shock absorbers, springs and caps available while assembling on the spring clamp, and complete supported shock-absorber groups available while disassembling. Multi-stage selections remain tied to the spring-clamp context so this switch can be disabled independently from normal assembly. |

#### Warehouse-backed selection lifecycle

The three warehouse assembly switches share one cache of references to the original objects in
all unlocked warehouses. The cache is built after the garage finishes loading, discarded when
the warehouse window opens and rebuilt after that window closes. When all three switches are
`false`, the cache is not built, scanned or updated.

Normal vehicle and engine lookups use temporary combined result lists. Machine-specific paths
expose only the required references for the duration of the native station operation. Cancelling
a selection leaves warehouse contents unchanged; a successful operation redirects the game's
normal `Inventory.Delete` or `DeleteGroup` call to the exact source warehouse and removes the
same object from the cache.

CMS21 Gameplay+ has no required dependency on CMS21 UI+. When UI+ is present, an optional
integration notification keeps UI+'s owned-part cache synchronized after a warehouse object is
consumed; absence of UI+ is a no-op.

#### Repairability configuration

`modifyRepairGroups` controls whether `Repairability.cfg` is applied at all. It defaults to `true`
and requires a game restart when changed. When disabled, CMS21 Gameplay+ leaves the game's
repair-group values unchanged.

When `modifyRepairGroups` is enabled and the repairability file itself is enabled,
`Repairability.cfg` can:

- assign exact reserved repair groups;
- mark listed parts as non-repairable;
- apply one repair-group modifier to every non-priority part in a specialized group;
- leave a specialized group on its vanilla repair groups by setting its modifier to `-1`.

For specialized groups, `repairGroup = -1` disables that group's modifier and keeps each part's
default game value, `0` forces the whole group to non-repairable, and `1..7` forces the whole
group to the selected repair group. `NonRepairable` and exact `Repairability1..Repairability5`
entries have higher priority and are therefore not changed by a specialized-group modifier.

The file is applied after game part data becomes available. Because it updates the effective game
repair groups, other systems that read those groups can observe the resulting repairability
without requiring a direct dependency on CMS21 Gameplay+.

#### Repairability classification policy

The custom table is intended to make repairability follow the physical role of a part rather than
to make the game uniformly easier. The classification uses these rules:

- **Consumables and normal replacement items stay non-repairable.** Parts whose real service
  procedure is replacement rather than restoration are treated as consumables. This includes
  friction and sealing elements, filters, belts, gaskets and seals, bearings, glass, lamps, small
  rubber parts, clips and clamps, fuses and relays, and other comparable wear or one-time-use
  items. A part does not become repairable merely because it is made of metal.
- **Sealed or non-serviceable assemblies are non-repairable when replacement is the plausible
  workshop action.** Selected electronic, electrical, hydraulic and accessory assemblies are
  explicitly forced to group `0` when the configured rules treat the complete unit as something
  that should be replaced rather than rebuilt at the game's repair bench.
- **Substantial rigid components may be made repairable when restoration is plausible.** Cast,
  forged, machined or fabricated parts such as engine structural components, suspension arms and
  hubs, serviceable covers and brackets, manifolds and exhaust components can be assigned to the
  repair group that best matches the game's repair mechanics. Metal water-pump and fan pulleys
  are treated as serviceable rigid components and are explicitly assigned to repair group `4`.
- **Material and function matter more than the wording of the part name.** Two parts both called
  a cover, housing or bracket do not have to share the same result when one is a serviceable metal
  component and the other is a disposable, plastic, decorative or protective piece. The same
  distinction is used for hoses, reservoirs, tensioners and similar mixed categories.
- **Equivalent parts are kept consistent across engine and vehicle variants.** When several IDs
  represent the same physical type of component, they should normally receive the same treatment.
  Exceptions are deliberate and should reflect a real difference in construction or function,
  not the vehicle on which the part happens to appear.
- **Vanilla repairability is preserved when a specialized modifier is disabled.** A specialized
  group with `repairGroup = -1` leaves every non-priority part on its default game value. Values
  `0..7` are deliberate whole-group overrides, so already repairable parts are reassigned as well.
  Exact `Repairability1..Repairability5` entries and `NonRepairable` remain higher-priority
  per-part overrides. The leaf-spring pad and U-bolt are kept in `Repairability1`, preserving their
  vanilla repair group `1` even though `SuspensionArmsAndHubs` uses modifier `3`.
- **Repair-group numbers are implementation categories, not a repairability score.** `0` means
  non-repairable. Non-zero values select the game's corresponding repair behavior; a larger number
  does not mean that a part is more repairable or more durable.

The practical default for an uncertain specialized group is therefore `repairGroup = -1`: keep
the game's original behavior until the parts can be classified confidently by construction,
service role and consistency with equivalent parts.

### Jobs and controls

| In-game setting | Config flag | Default | Detailed behavior |
|---|---|---:|---|
| **Bypass part repair minigame** | `bypassPartRepairMinigame` | `false` | Automatically completes the part-repair minigame after it starts. |
| **Bypass wheel balancing minigame** | `bypassWheelBalanceMinigame` | `false` | Marks the wheel as balanced and closes the balancing minigame. |
| **Bypass wheel alignment minigame** | `bypassWheelAlignmentMinigame` | `false` | Sets all four wheel-alignment values to the successful position and closes the minigame. |
| **Bypass headlamp alignment minigame** | `bypassHeadlampAlignmentMinigame` | `false` | Sets both headlamp-alignment values to the successful position and closes the minigame. |
| **Bypass auction minigame** | `bypassAuctionMinigame` | `false` | Automatically performs bidding until the auction finishes. |
| **Bypass carburetor tuning minigame** | `bypassCarburetorTuningMinigame` | `false` | Completes carburetor tuning after the first tuning adjustment. |
| **Bypass ECU tuning minigame** | `bypassEcuTuningMinigame` | `false` | Completes ECU tuning after the first tuning adjustment. |
| **Enable part repairability modifiers** | `modifyRepairGroups` | `true` | Applies `Repairability.cfg` to the game part data. Disabling it leaves the game repair groups unchanged and skips the custom repairability rules. |
| **Repair brake drums on the brake lathe** | `allowBrakeLatheFixDrumBrake` | `true` | Adds supported brake drums to the brake-lathe selection and allows them to be repaired there. When CMS21 UI+ is installed, the optional integration also exposes the same effective repairable status to its filters and wrench badges. Disabling this switch removes the additional brake-lathe eligibility. |
| **Repair gears on the brake lathe** | `allowBrakeLatheFixGears` | `true` | Adds supported cam gears, timing sprockets, timing gears and drivetrain gears to the brake-lathe selection and allows them to be repaired there. Chain guides, tensioners and chains are not included. |
| **Repair flywheels and pressure plates on the brake lathe** | `allowBrakeLatheFixFlywheel` | `true` | Adds supported flywheels and clutch pressure plates to the brake-lathe selection and allows them to be repaired there. |
| **Repair pulleys on the brake lathe** | `allowBrakeLatheFixPulleys` | `true` | Adds supported water-pump, fan and crankshaft pulleys to the brake-lathe selection and allows them to be repaired there. |
| **Brake lathe processing duration** | `brakeLatheProcessingDuration` | `Medium` | Selects the processing duration: Off (immediate repair), Fast (4×1 s), Medium (4×2 s), Slow (4×3 s), or Default (native 4×5 s). |

For animated brake-lathe modes, the loop sound starts fading during the final second. Extra gear, pulley and flywheel models are aligned to the spindle axis and scaled for brake-lathe display, with cutter travel and the processing ring matched to their diameter. The processing ring is hidden when the repair cycle finishes. `Off` completes the repair immediately without running the processing animation or loop sound.

Brake-lathe availability always follows each part's effective `RepairGroup`. With `modifyRepairGroups = false`, the original game repair groups are used; with it enabled, availability is evaluated after the configured repairability rules are applied. If no supported part in a brake-lathe category has a non-zero effective `RepairGroup`, the corresponding `allowBrakeLatheFix...` switch is automatically reset to `false` and saved. If only some supported parts are repairable, the switch remains enabled and the station lists only those repairable parts. With CMS21 UI+ installed, dependency warnings follow the pending `modifyRepairGroups` value immediately: the UI compares both the original game repairability and the current `Repairability.cfg` rules without applying either mode live. Enabled partial categories warn that the switch does not affect non-repairable parts; unavailable categories show the appropriate disabled-modification or configured-repairability warning.

### Interface and state

| In-game setting | Config flag | Default | Detailed behavior |
|---|---|---:|---|
| **Remember garage doors** | `rememberGarageDoorState` | `true` | Stores and restores the open/closed state of the seven vanilla garage, paint-shop and office doors for the active profile. It intentionally does not restore the removed `Gate_fun` teleport or `busDoorHelper` behavior. |

### Locations and garage

| In-game setting | Config flag | Default | Detailed behavior |
|---|---|---:|---|
| **Maximum junkyard cars** | `junkyardVehicleMaximumCars` | `false` | Forces the junkyard generator's vehicle percentage to 100% when a junkyard visit is generated, producing the game's maximum intended car count for that location. |
| **Expanded auction pools** | `expandedAuctionCarPool` | `false` | Expands the normal and salvage auction generation ranges to 300–450 vehicles. This can materially increase loading time and memory use, so the setting is disabled by default. |
| **Allow parts travel with full vehicle storage** | `allowPartsTravelWhenVehicleStorageIsFull` | `true` | Allows travel to the Junkyard and Barn when the only blocking map problem is the lack of a free vehicle-storage slot. For those two destinations the storage-capacity warning is suppressed and the normal drive action remains available. Other destination restrictions and other map problems are left unchanged. |
| **Relocate engine hoist** | `relocateEngineHoistNearStand` | `false` | In the normal garage, moves the vanilla engine hoist beside the engine stand after the relevant scene object becomes available. Other locations and custom hoists are not changed. |

## In-game mod settings

The mod provides `configs/CMS21GameplayPlus.ui-settings.json` for the shared in-game mod
settings interface supplied by CMS21 UI+. Integration is declarative: CMS21 Gameplay+ does not
reference `CMS21UIPlus.dll`, implement an interface or expose a provider class. CMS21 UI+ reads
the manifest and the declared TOML configuration file without calling into the Gameplay+ DLL.

The manifest contains its own English and Russian setting names and descriptions. If CMS21 UI+
is not installed, CMS21 Gameplay+ continues to use `CMS21GameplayPlus.cfg` normally.

`applyMode` is descriptive: CMS21 UI+ writes the configuration but does not notify, reload or
invoke CMS21 Gameplay+.

## Configuration and runtime files

Current templates and UI manifest:

- `configs/CMS21GameplayPlus.cfg` — primary feature switches;
- `configs/CMS21GameplayPlus.ui-settings.json` — in-game settings groups, labels and metadata;
- `configs/Repairability.cfg` — custom repairability groups and overrides.

At runtime they are installed under:

```text
<Game>\Mods\CMS21GameplayPlus\
```

`ProfileMemory.dat` is generated in that directory and stores only profile-specific garage-door
state. `CMS21GameplayPlus.cfg.bak` can exist temporarily when the configuration is saved through
the CMS21 UI+ Mods menu and contains the previous configuration. It is deleted when the Mods
menu closes, but can remain after an abnormal termination. Do not commit or package either
generated file.

## QoLmod origin

CMS21 Gameplay+ retains the following feature concepts from QoLmod by **Meitzi**:

- minigame bypasses: `bypassPartRepairMinigame`, `bypassWheelBalanceMinigame`,
  `bypassWheelAlignmentMinigame`, `bypassHeadlampAlignmentMinigame`,
  `bypassAuctionMinigame`, `bypassCarburetorTuningMinigame`,
  `bypassEcuTuningMinigame`;
- brake-drum repair on the brake lathe: `allowBrakeLatheFixDrumBrake`;
- maximum junkyard vehicle generation and expanded auction pools:
  `junkyardVehicleMaximumCars`, `expandedAuctionCarPool`;
- garage-door state persistence: `rememberGarageDoorState`;
- engine-hoist relocation: `relocateEngineHoistNearStand`.

Warehouse-backed assembly, `Repairability.cfg` rules and travel to parts locations with full
vehicle storage are CMS21 Gameplay+ features rather than retained QoLmod features.

## Repository layout

```text
cms21-gameplay-plus/
├─ configs/
│  ├─ CMS21GameplayPlus.cfg
│  ├─ CMS21GameplayPlus.ui-settings.json
│  └─ Repairability.cfg
├─ libs/                    # local reference DLLs, not tracked by Git
├─ scripts/
│  ├─ build.ps1
│  ├─ build-install.ps1
│  └─ restore-libs.ps1
├─ src/
│  ├─ Features/
│  ├─ Infrastructure/
│  ├─ Config.cs
│  └─ Main.cs
├─ CMS21GameplayPlus.csproj
├─ LICENSE.md
├─ README-install.md
└─ README.md
```

## Build

Requirements:

- Windows;
- .NET Framework 4.7.2 Developer Pack;
- Visual Studio Build Tools/MSBuild;
- game, Unity, MelonLoader, Tomlet and Harmony assemblies in `libs`.

### Restoring reference libraries

The DLL files under `libs` are local development dependencies and are not tracked by Git.
Restore them from the installed game and MelonLoader directories:

```powershell
.\scripts\restore-libs.ps1 `
    -GamePath "D:\SteamLibrary\steamapps\common\Car Mechanic Simulator 2021"
```

The script reads the required `libs\*.dll` entries from `CMS21GameplayPlus.csproj`, creates the
`libs` directory when necessary and preserves existing DLLs unless `-Force` is used. It also
validates the game layout and reports the selected source path and assembly version for each
restored DLL.

### Compiling and installing

From the repository root:

```powershell
.\scripts\build.ps1 -Target Rebuild -Configuration Release
```

Build, create the explicit install payload and install it:

```powershell
.\scripts\build-install.ps1
```

A destination can be supplied directly:

```powershell
.\scripts\build-install.ps1 `
    -Destination "D:\SteamLibrary\steamapps\common\Car Mechanic Simulator 2021"
```

See `README-install.md` for accepted destination paths and installation behavior.

## Licence

GNU General Public License v3.0. See `LICENSE.md`.
