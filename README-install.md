# Build and install — CMS21 Gameplay+

## Build
1. Restore local game/MelonLoader DLL references with `scripts\restore-libs.ps1 -GamePath <game>`.
2. Run the VS Code task `CMS21 Gameplay+: Build Release` or `scripts\build.ps1 -Target Build -Configuration Release`.
3. `scripts\build-install.ps1` prepares the release payload and can install it into a detected/explicit CMS 2021 directory.

## Runtime layout
```text
Mods\
├─ CMS21GameplayPlus.dll
└─ CMS21GameplayPlus\
   ├─ CMS21GameplayPlus.cfg
   └─ ...
```

The package also includes `Repairability.cfg` and the UI-settings manifest. User-generated profile/config state is not source-controlled.
