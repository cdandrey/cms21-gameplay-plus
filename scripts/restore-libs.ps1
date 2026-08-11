[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$GamePath,

    [string]$RepositoryRoot,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepositoryRoot {
    param([string]$ExplicitRoot)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitRoot)) {
        $resolved = (Resolve-Path -LiteralPath $ExplicitRoot).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
            throw "Repository root is not a directory: $resolved"
        }
        return $resolved
    }

    $candidates = @(
        $PSScriptRoot,
        (Split-Path -Parent $PSScriptRoot),
        (Get-Location).Path
    )

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if (Test-Path -LiteralPath (Join-Path $candidate "CMS21GameplayPlus.csproj") -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "Repository root was not found. Place this script in repo\scripts or pass -RepositoryRoot."
}

function Get-AssemblyDescription {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $assemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($Path)
        return "$($assemblyName.Name), version $($assemblyName.Version)"
    } catch {
        return "version unavailable"
    }
}

function Add-DllCandidates {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][int]$Priority,
        [Parameter(Mandatory = $true)][System.Collections.Generic.HashSet[string]]$RequiredNames,
        [Parameter(Mandatory = $true)][hashtable]$Lookup
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return
    }

    foreach ($file in Get-ChildItem -LiteralPath $Root -Filter "*.dll" -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName) {
        if (-not $RequiredNames.Contains($file.Name)) {
            continue
        }

        if (-not $Lookup.ContainsKey($file.Name)) {
            $Lookup[$file.Name] = New-Object 'System.Collections.Generic.List[object]'
        }

        $Lookup[$file.Name].Add([PSCustomObject]@{
            Priority = $Priority
            Path = $file.FullName
            Version = Get-AssemblyDescription -Path $file.FullName
        })
    }
}

$repoRoot = Resolve-RepositoryRoot -ExplicitRoot $RepositoryRoot
if (-not (Test-Path -LiteralPath $GamePath -PathType Container)) {
    throw "Game directory was not found: $GamePath"
}
$resolvedGamePath = (Resolve-Path -LiteralPath $GamePath).Path
$projectFile = Join-Path $repoRoot "CMS21GameplayPlus.csproj"
$libsDirectory = Join-Path $repoRoot "libs"
$managedDirectory = Join-Path $resolvedGamePath "Car Mechanic Simulator 2021_Data\Managed"
$melonDirectory = Join-Path $resolvedGamePath "MelonLoader"

if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "CMS21GameplayPlus.csproj was not found: $projectFile"
}
if (-not (Test-Path -LiteralPath $managedDirectory -PathType Container) -and
    -not (Test-Path -LiteralPath $melonDirectory -PathType Container)) {
    throw "The selected directory does not contain the expected game Managed or MelonLoader directories: $resolvedGamePath"
}

[xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw
$namespaceManager = New-Object System.Xml.XmlNamespaceManager($projectXml.NameTable)
$namespaceManager.AddNamespace("msb", "http://schemas.microsoft.com/developer/msbuild/2003")

$requiredReferences = New-Object 'System.Collections.Generic.List[object]'
$requiredNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

foreach ($hintPathNode in $projectXml.SelectNodes("//msb:Reference/msb:HintPath", $namespaceManager)) {
    $hintPath = $hintPathNode.InnerText.Trim()
    if (-not $hintPath.StartsWith("libs\", [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }

    $fileName = [System.IO.Path]::GetFileName($hintPath)
    if ([string]::IsNullOrWhiteSpace($fileName)) {
        continue
    }

    $requiredReferences.Add([pscustomobject]@{
        HintPath = $hintPath
        FileName = $fileName
    })
    [void]$requiredNames.Add($fileName)
}

if ($requiredReferences.Count -eq 0) {
    throw "No libs DLL references were found in $projectFile"
}

New-Item -ItemType Directory -Path $libsDirectory -Force | Out-Null

$lookup = @{}
$preferredRoots = @(
    (Join-Path $resolvedGamePath "MelonLoader\Il2CppAssemblies"),
    (Join-Path $resolvedGamePath "MelonLoader\net35"),
    (Join-Path $resolvedGamePath "MelonLoader\Managed"),
    (Join-Path $resolvedGamePath "MelonLoader\Dependencies"),
    $managedDirectory,
    $melonDirectory
)

for ($index = 0; $index -lt $preferredRoots.Count; $index++) {
    Add-DllCandidates -Root $preferredRoots[$index] -Priority $index `
        -RequiredNames $requiredNames -Lookup $lookup
}

# Fallback only for non-standard layouts and only for assemblies not found above.
if ($lookup.Count -lt $requiredNames.Count) {
    Add-DllCandidates -Root $resolvedGamePath -Priority 100 `
        -RequiredNames $requiredNames -Lookup $lookup
}

$restored = New-Object 'System.Collections.Generic.List[object]'
$kept = New-Object 'System.Collections.Generic.List[object]'
$missing = New-Object 'System.Collections.Generic.List[string]'

foreach ($reference in $requiredReferences) {
    $destination = Join-Path $repoRoot $reference.HintPath

    if ((Test-Path -LiteralPath $destination -PathType Leaf) -and -not $Force) {
        $kept.Add([PSCustomObject]@{
            Name = $reference.FileName
            Source = $destination
            Version = Get-AssemblyDescription -Path $destination
        })
        continue
    }

    if (-not $lookup.ContainsKey($reference.FileName)) {
        $missing.Add($reference.FileName)
        continue
    }

    $selected = $lookup[$reference.FileName] |
        Sort-Object Priority, Path |
        Select-Object -First 1
    Copy-Item -LiteralPath $selected.Path -Destination $destination -Force
    $restored.Add([PSCustomObject]@{
        Name = $reference.FileName
        Source = $selected.Path
        Version = $selected.Version
    })
}

Write-Host "Repository: $repoRoot"
Write-Host "Game:       $resolvedGamePath"
Write-Host "Libraries:  $libsDirectory"
Write-Host ""
Write-Host "Restored:   $($restored.Count)"
Write-Host "Kept:       $($kept.Count)"
Write-Host "Missing:    $($missing.Count)"

if ($restored.Count -gt 0) {
    Write-Host ""
    Write-Host "Restored files:"
    foreach ($entry in $restored) {
        Write-Host "  $($entry.Name) <- $($entry.Source) [$($entry.Version)]"
    }
}

if ($kept.Count -gt 0) {
    Write-Host ""
    Write-Host "Existing files kept:"
    foreach ($entry in $kept) {
        Write-Host "  $($entry.Name) [$($entry.Version)]"
    }
}

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "Not found in the game installation:"
    foreach ($fileName in $missing) {
        Write-Host "  $fileName"
    }

    throw "Some reference assemblies could not be restored. Start the game once with the installed MelonLoader version so generated assemblies are available, then run this script again."
}

Write-Host ""
Write-Host "All project reference assemblies are present."
