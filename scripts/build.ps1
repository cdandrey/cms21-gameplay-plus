[CmdletBinding()]
param(
    [ValidateSet("Build", "Rebuild", "Clean")]
    [string]$Target = "Build",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot "src"

function Find-CMS21GameplayPlusProject {
    $preferredPaths = @(
        (Join-Path $sourceRoot "CMS21GameplayPlus.csproj"),
        (Join-Path $repoRoot "CMS21GameplayPlus.csproj")
    )

    foreach ($path in $preferredPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    $found = Get-ChildItem -LiteralPath $repoRoot -Filter "CMS21GameplayPlus.csproj" -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj|dist|packages)[\\/]'
        } |
        Select-Object -First 1

    if ($null -ne $found) {
        return $found.FullName
    }

    return $null
}

function Get-ProjectLibraryReferences {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectFile
    )

    [xml]$projectXml = Get-Content -LiteralPath $ProjectFile -Raw
    $namespaceUri = $projectXml.DocumentElement.NamespaceURI

    if ([string]::IsNullOrEmpty($namespaceUri)) {
        $hintPathNodes = $projectXml.SelectNodes("//Reference/HintPath")
    } else {
        $namespaceManager = New-Object System.Xml.XmlNamespaceManager($projectXml.NameTable)
        $namespaceManager.AddNamespace("msbuild", $namespaceUri)
        $hintPathNodes = $projectXml.SelectNodes("//msbuild:Reference/msbuild:HintPath", $namespaceManager)
    }

    $projectDirectory = Split-Path -Parent $ProjectFile
    $references = New-Object 'System.Collections.Generic.List[object]'

    foreach ($hintPathNode in $hintPathNodes) {
        $relativePath = ([string]$hintPathNode.InnerText).Trim()
        if ([string]::IsNullOrEmpty($relativePath)) {
            continue
        }

        $normalizedPath = $relativePath.Replace('/', '\')
        if (-not $normalizedPath.StartsWith("libs\", [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $projectDirectory $relativePath))
        $references.Add([PSCustomObject]@{
            Name = [System.IO.Path]::GetFileName($fullPath)
            RelativePath = $normalizedPath
            FullPath = $fullPath
        })
    }

    return @($references | Sort-Object RelativePath -Unique)
}

function Assert-ProjectLibraries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectFile
    )

    $projectDirectory = Split-Path -Parent $ProjectFile
    $libraryDirectory = Join-Path $projectDirectory "libs"
    $requiredLibraries = @(Get-ProjectLibraryReferences -ProjectFile $ProjectFile)

    if ($requiredLibraries.Count -eq 0) {
        throw "No library references under 'libs' were found in '$ProjectFile'."
    }

    $missingLibraries = @(
        $requiredLibraries |
            Where-Object { -not (Test-Path -LiteralPath $_.FullPath -PathType Leaf) }
    )

    if ((Test-Path -LiteralPath $libraryDirectory -PathType Container) -and
        $missingLibraries.Count -eq 0) {
        return
    }

    Write-Host ""
    Write-Host "Build was not started because required project libraries are unavailable." -ForegroundColor Red

    if (-not (Test-Path -LiteralPath $libraryDirectory -PathType Container)) {
        Write-Host "Library directory is missing: $libraryDirectory" -ForegroundColor Red
    } else {
        Write-Host "Missing libraries:" -ForegroundColor Red
        foreach ($library in $missingLibraries) {
            Write-Host "  - $($library.Name)"
        }
    }

    Write-Host ""
    Write-Host "All required libraries:"
    foreach ($library in $requiredLibraries) {
        Write-Host "  - $($library.Name)"
    }

    $restoreScript = Join-Path $repoRoot "scripts\restore-libs.ps1"
    $readmeFile = Join-Path $repoRoot "README.md"

    Write-Host ""
    Write-Host "The library restore helper must be present in the scripts directory:"
    Write-Host "  $restoreScript"
    Write-Host "Detailed project setup and library restoration instructions are in:"
    Write-Host "  $readmeFile"
    Write-Host ""
    throw "Restore the required DLL files under '$libraryDirectory' and run the script again."
}

function Find-MSBuild {
    $vswhere = Join-Path ([Environment]::GetFolderPath("ProgramFilesX86")) "Microsoft Visual Studio\Installer\vswhere.exe"

    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $candidates = @(
            & $vswhere `
                -latest `
                -products * `
                -requires Microsoft.Component.MSBuild `
                -find "MSBuild\**\Bin\MSBuild.exe"
        )

        if ($candidates.Length -gt 0) {
            return $candidates[0]
        }
    }

    $command = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw "MSBuild.exe was not found. Install Visual Studio Build Tools or add MSBuild.exe to PATH."
}

function Remove-BinDirectories {
    $paths = @(
        (Join-Path $repoRoot "bin"),
        (Join-Path $sourceRoot "bin")
    )

    foreach ($path in $paths | Select-Object -Unique) {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$projectFile = Find-CMS21GameplayPlusProject

if ($Target -eq "Clean") {
    Remove-BinDirectories
    Write-Host "Removed bin directories."
    exit 0
}

if ([string]::IsNullOrEmpty($projectFile)) {
    throw "CMS21GameplayPlus.csproj was not found. Expected '$sourceRoot\CMS21GameplayPlus.csproj' or '$repoRoot\CMS21GameplayPlus.csproj'."
}

Assert-ProjectLibraries -ProjectFile $projectFile

$projectDirectory = Split-Path -Parent $projectFile
$solutionFile = Join-Path $projectDirectory "CMS21GameplayPlus.sln"
$buildInput = if (Test-Path -LiteralPath $solutionFile -PathType Leaf) { $solutionFile } else { $projectFile }
$outputDll = Join-Path (Join-Path $projectDirectory "bin\$Configuration") "CMS21GameplayPlus.dll"
$msbuild = Find-MSBuild

$arguments = @(
    $buildInput,
    "/p:Configuration=$Configuration",
    "/m",
    "/t:$Target"
)

Write-Host "Repository:    $repoRoot"
Write-Host "Project:       $projectFile"
Write-Host "Build input:   $buildInput"
Write-Host "MSBuild:       $msbuild"
Write-Host "Target:        $Target"
Write-Host "Configuration: $Configuration"

& $msbuild @arguments
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $outputDll -PathType Leaf)) {
    throw "Build succeeded, but CMS21GameplayPlus.dll was not found at the expected path: $outputDll"
}

Write-Host "Compiler output: $outputDll"
