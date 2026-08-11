using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;

[assembly: AssemblyTitle(Cms21GameplayPlus.BuildInfo.Name)]
[assembly: AssemblyDescription(Cms21GameplayPlus.BuildInfo.Description)]
[assembly: AssemblyCompany(Cms21GameplayPlus.BuildInfo.Company)]
[assembly: AssemblyProduct(Cms21GameplayPlus.BuildInfo.Name)]
[assembly: AssemblyCopyright("CMS21 Gameplay+ contributors; based in part on QoLmod by Meitzi")]
[assembly: AssemblyVersion(Cms21GameplayPlus.BuildInfo.Version)]
[assembly: AssemblyFileVersion(Cms21GameplayPlus.BuildInfo.Version)]
[assembly: AssemblyCulture("")]
[assembly: MelonInfo(typeof(Cms21GameplayPlus.Main), Cms21GameplayPlus.BuildInfo.ShortName,
    Cms21GameplayPlus.BuildInfo.Version, Cms21GameplayPlus.BuildInfo.Author, Cms21GameplayPlus.BuildInfo.DownloadLink)]
#if NET6_0_OR_GREATER
[assembly: MelonColor(255, 4, 163, 204)]
#else
[assembly: MelonColor()]
#endif
[assembly: MelonGame(Cms21GameplayPlus.BuildInfo.MelonGameCompany, Cms21GameplayPlus.BuildInfo.MelonGameName)]
[assembly: HarmonyDontPatchAll]
[assembly: ComVisible(false)]
[assembly: Guid("7AE6D7AE-9B37-4023-A715-76CECA9E3CC4")]
