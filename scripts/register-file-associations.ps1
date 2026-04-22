[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$SetAsDefault
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

function Ensure-RegistryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $null = Open-RegistryKey -Path $Path
}

function Convert-RegistrySubPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ($Path.StartsWith("HKCU:\", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring(6)
    }

    throw "Unsupported registry path: $Path"
}

function Open-RegistryKey {
    param([Parameter(Mandatory = $true)][string]$Path)

    $subPath = Convert-RegistrySubPath -Path $Path
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($subPath)
    if ($null -eq $key) {
        throw "Failed to create or open registry path: $Path"
    }

    return $key
}

function Set-RegistryDefaultValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $key = Open-RegistryKey -Path $Path
    try {
        $key.SetValue("", $Value, [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $key.Dispose()
    }
}

function Add-RegistryStringValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value
    )

    $key = Open-RegistryKey -Path $Path
    try {
        $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $key.Dispose()
    }
}

function Add-RegistryNoneValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $key = Open-RegistryKey -Path $Path
    try {
        $key.SetValue($Name, [byte[]]@(), [Microsoft.Win32.RegistryValueKind]::None)
    }
    finally {
        $key.Dispose()
    }
}

function Remove-RegistryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Remove-RegistryValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $key = Open-RegistryKey -Path $Path
    try {
        if ($key.GetValueNames() -contains $Name) {
            $key.DeleteValue($Name, $false)
        }
    }
    finally {
        $key.Dispose()
    }
}

function Get-RegistryValueNames {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    $key = Open-RegistryKey -Path $Path
    try {
        return @($key.GetValueNames())
    }
    finally {
        $key.Dispose()
    }
}

function Get-RegistryStringValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $key = Open-RegistryKey -Path $Path
    try {
        return $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
    finally {
        $key.Dispose()
    }
}

function Register-ProgId {
    param(
        [Parameter(Mandatory = $true)][string]$ProgId,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$DisplayName
    )

    $basePath = "HKCU:\Software\Classes\$ProgId"
    $command = ('"{0}" "%1"' -f $ExecutablePath)

    Set-RegistryDefaultValue -Path $basePath -Value $DisplayName
    Set-RegistryDefaultValue -Path (Join-Path $basePath "DefaultIcon") -Value ('"{0}",0' -f $ExecutablePath)
    Set-RegistryDefaultValue -Path (Join-Path $basePath "shell\open\command") -Value $command
}

function Register-Application {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutableName,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string[]]$Extensions
    )

    $basePath = "HKCU:\Software\Classes\Applications\$ExecutableName"
    $command = ('"{0}" "%1"' -f $ExecutablePath)

    Set-RegistryDefaultValue -Path (Join-Path $basePath "shell\open\command") -Value $command
    foreach ($extension in $Extensions) {
        Add-RegistryStringValue -Path (Join-Path $basePath "SupportedTypes") -Name $extension -Value ""
    }
}

function Update-OpenWithList {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$PreferredExecutableName,
        [Parameter(Mandatory = $true)][string[]]$RemoveExecutableNames
    )

    $existingNames = Get-RegistryValueNames -Path $Path
    $existingExecutables = @()
    foreach ($name in $existingNames) {
        if ($name -eq "MRUList") {
            continue
        }

        $value = Get-RegistryStringValue -Path $Path -Name $name
        if (-not [string]::IsNullOrWhiteSpace([string]$value)) {
            $existingExecutables += [string]$value
        }
    }

    $filtered = @($existingExecutables | Where-Object {
        $_ -and
        $_ -ne $PreferredExecutableName -and
        ($RemoveExecutableNames -notcontains $_)
    })

    foreach ($name in $existingNames) {
        Remove-RegistryValue -Path $Path -Name $name
    }

    $ordered = @($PreferredExecutableName) + $filtered
    $index = 0
    $mruList = New-Object System.Text.StringBuilder
    foreach ($executableName in $ordered) {
        $slot = [char]([int][char]'a' + $index)
        Add-RegistryStringValue -Path $Path -Name $slot -Value $executableName
        [void]$mruList.Append($slot)
        $index++
    }

    Add-RegistryStringValue -Path $Path -Name "MRUList" -Value $mruList.ToString()
}

function Register-Extension {
    param(
        [Parameter(Mandatory = $true)][string]$Extension,
        [Parameter(Mandatory = $true)][string]$DefaultProgId,
        [Parameter(Mandatory = $true)][string]$ExecutableName,
        [switch]$AssignDefaultProgId
    )

    $basePath = "HKCU:\Software\Classes\$Extension"
    if ($AssignDefaultProgId) {
        Set-RegistryDefaultValue -Path $basePath -Value $DefaultProgId
    }

    $openWithProgIdsPath = Join-Path $basePath "OpenWithProgids"
    Add-RegistryStringValue -Path $openWithProgIdsPath -Name $DefaultProgId -Value ""
    Remove-RegistryValue -Path $openWithProgIdsPath -Name "MdDocViewer.Markdown"
    Remove-RegistryValue -Path $openWithProgIdsPath -Name "MdDocViewer.markdown"

    $explorerBasePath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$Extension"
    $explorerProgIdsPath = Join-Path $explorerBasePath "OpenWithProgids"
    Add-RegistryNoneValue -Path $explorerProgIdsPath -Name $DefaultProgId
    Remove-RegistryValue -Path $explorerProgIdsPath -Name "MdDocViewer.Markdown"
    Remove-RegistryValue -Path $explorerProgIdsPath -Name "MdDocViewer.markdown"

    $openWithListPath = Join-Path $explorerBasePath "OpenWithList"
    Update-OpenWithList `
        -Path $openWithListPath `
        -PreferredExecutableName $ExecutableName `
        -RemoveExecutableNames @("MdDocViewer.exe")
}

function Clear-ManagedExtensionDefault {
    param(
        [Parameter(Mandatory = $true)][string]$Extension,
        [Parameter(Mandatory = $true)][string[]]$ManagedProgIds
    )

    $basePath = "HKCU:\Software\Classes\$Extension"
    if (-not (Test-Path -LiteralPath $basePath)) {
        return $false
    }

    $key = Open-RegistryKey -Path $basePath
    try {
        $currentDefault = $key.GetValue("", $null)
        if ($null -eq $currentDefault) {
            return $false
        }

        foreach ($managedProgId in $ManagedProgIds) {
            if ([string]::Equals(
                    [string]$currentDefault,
                    $managedProgId,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                $key.DeleteValue("", $false)
                return $true
            }
        }

        return $false
    }
    finally {
        $key.Dispose()
    }
}

function Notify-AssociationChanged {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class MdTranslatorViewerShell32
{
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
"@ -ErrorAction SilentlyContinue

    [MdTranslatorViewerShell32]::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)
}

$resolvedRoot = Resolve-RepositoryRoot -RequestedRoot $RepoRoot
$appDirectory = Join-Path $resolvedRoot "app"
$newExecutablePath = Join-Path $appDirectory "MdTranslatorViewer.exe"

if (-not (Test-Path -LiteralPath $newExecutablePath)) {
    throw "Expected executable not found: $newExecutablePath"
}

$displayName = "Markdown Document (MD Translator Viewer)"
$defaultProgId = "MdTranslatorViewer.Markdown"
$extensions = @(".md", ".markdown", ".mdx", ".mkd")
$clearedDefaults = New-Object System.Collections.Generic.List[string]

Register-ProgId -ProgId $defaultProgId -ExecutablePath $newExecutablePath -DisplayName $displayName
Register-Application -ExecutableName "MdTranslatorViewer.exe" -ExecutablePath $newExecutablePath -Extensions $extensions

foreach ($extension in $extensions) {
    Register-Extension `
        -Extension $extension `
        -DefaultProgId $defaultProgId `
        -ExecutableName "MdTranslatorViewer.exe" `
        -AssignDefaultProgId:$SetAsDefault

    if (-not $SetAsDefault -and (Clear-ManagedExtensionDefault -Extension $extension -ManagedProgIds @($defaultProgId))) {
        $clearedDefaults.Add($extension) | Out-Null
    }
}

Remove-RegistryPath -Path "HKCU:\Software\Classes\Applications\MdDocViewer.exe"
Remove-RegistryPath -Path "HKCU:\Software\Classes\MdDocViewer.Markdown"
Remove-RegistryPath -Path "HKCU:\Software\Classes\MdDocViewer.markdown"

$legacyExecutablePath = Join-Path $appDirectory "MdDocViewer.exe"
if (Test-Path -LiteralPath $legacyExecutablePath) {
    Remove-Item -LiteralPath $legacyExecutablePath -Force
}

Notify-AssociationChanged

if ($SetAsDefault) {
    Write-Host "Registered Markdown file associations for $resolvedRoot and set this app as the per-user default handler."
}
elseif ($clearedDefaults.Count -gt 0) {
    Write-Host ("Registered Markdown file associations for {0} without changing the Windows default app. Cleared previous overrides for: {1}" -f $resolvedRoot, ($clearedDefaults -join ", "))
}
else {
    Write-Host "Registered Markdown file associations for $resolvedRoot without changing the Windows default app."
}
