[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Project = "MdTranslatorViewer.csproj",
    [string]$Version,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepositoryRoot {
    param([string]$RequestedRoot)

    if (-not [string]::IsNullOrWhiteSpace($RequestedRoot)) {
        return (Resolve-Path -LiteralPath $RequestedRoot).Path
    }

    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

function Assert-PathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $resolvedBase = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\')
    $resolvedTarget = [System.IO.Path]::GetFullPath($TargetPath)

    if ([string]::Equals($resolvedTarget, $resolvedBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    if (-not $resolvedTarget.StartsWith($resolvedBase + "\", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the expected directory. Base: $resolvedBase Target: $resolvedTarget"
    }
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @ArgumentList
        if ($LASTEXITCODE -ne 0) {
            throw ("Command failed with exit code {0}: {1} {2}" -f $LASTEXITCODE, $FilePath, ($ArgumentList -join " "))
        }
    }
    finally {
        Pop-Location
    }
}

function Get-ReleaseVersionSuffix {
    param([string]$RequestedVersion)

    if ([string]::IsNullOrWhiteSpace($RequestedVersion)) {
        throw "A release version is required. Pass -Version with a value such as v0.1.0."
    }

    $trimmedVersion = $RequestedVersion.Trim()
    if ($trimmedVersion.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "Release version contains invalid filename characters: $trimmedVersion"
    }

    return $trimmedVersion
}

$resolvedRoot = Resolve-RepositoryRoot -RequestedRoot $RepoRoot
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) {
    $Project
}
else {
    Join-Path $resolvedRoot $Project
}

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file not found: $projectPath"
}

$appName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
$releaseVersion = Get-ReleaseVersionSuffix -RequestedVersion $Version
$releaseAssetName = "$appName-$releaseVersion-$RuntimeIdentifier-portable"
$distRoot = Join-Path $resolvedRoot "dist"
$publishDirectory = Join-Path $distRoot "$appName-$RuntimeIdentifier"
$zipPath = Join-Path $distRoot "$releaseAssetName.zip"
$checksumPath = Join-Path $distRoot "$releaseAssetName.zip.sha256.txt"

New-Item -ItemType Directory -Path $distRoot -Force | Out-Null

if (-not $SkipBuild) {
    Invoke-ExternalCommand `
        -FilePath "dotnet" `
        -ArgumentList @("build", $projectPath, "-c", $Configuration) `
        -WorkingDirectory $resolvedRoot
}

Assert-PathWithin -BasePath $distRoot -TargetPath $publishDirectory
Assert-PathWithin -BasePath $distRoot -TargetPath $zipPath
Assert-PathWithin -BasePath $distRoot -TargetPath $checksumPath

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}

Invoke-ExternalCommand `
    -FilePath "dotnet" `
    -ArgumentList @(
        "publish",
        $projectPath,
        "-c",
        $Configuration,
        "-r",
        $RuntimeIdentifier,
        "-p:PublishSingleFile=true",
        "--self-contained",
        "true",
        "-o",
        $publishDirectory
    ) `
    -WorkingDirectory $resolvedRoot

Get-ChildItem -Path $publishDirectory -File | Where-Object {
    $_.Extension -in @(".pdb", ".xml")
} | Remove-Item -Force

Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $zipPath -Force

$zipItem = Get-Item -LiteralPath $zipPath
$sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$sha256 *$($zipItem.Name)" | Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Created release zip: $($zipItem.FullName)"
Write-Host "Created checksum file: $checksumPath"
Write-Host "Publish folder: $publishDirectory"
Write-Host "SHA256: $sha256"

[pscustomobject]@{
    ChecksumPath = $checksumPath
    PublishDirectory = $publishDirectory
    ZipPath = $zipItem.FullName
    ZipSizeBytes = $zipItem.Length
    Sha256 = $sha256
}
