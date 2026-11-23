<#
Creates or refreshes the VR copy of Cloudpunk. The Steam install is only read, never modified.

  install.bat                               # (double click) find the Steam copy, make <drive>:\Games\CloudpunkVR
  tools\install.ps1 -Target D:\CloudpunkVR  # another place
  tools\install.ps1 -Source E:\SteamLibrary\steamapps\common\Cloudpunk
  tools\install.ps1 -PluginOnly             # just redeploy the plugin, the preloader patcher, the SteamVR bindings,
                                            # the controller layout editor and the player readme

Steps: copy the game (skipped when the target already has Cloudpunk.exe), unpack BepInEx 6 IL2CPP, add
openvr_api.dll from SteamVR, add OpenVR to the VR device list in globalgamemanagers (backup kept next to it),
turn graphics jobs on in boot.config, deploy the plugin, write the launchers. The first launch afterwards takes about
a minute longer: BepInEx generates the interop assemblies (it downloads the Unity base libraries once).

Needs: Cloudpunk and SteamVR installed through Steam. Nothing else (Windows PowerShell 5 is enough).
This file stays ASCII so that Windows PowerShell 5 reads it the same way with or without a BOM.
#>
param(
    [string]$Source = "",
    [string]$Target = "",
    [string]$BepInExZip = "",
    [string]$OpenVRDll = "",
    [switch]$PluginOnly
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# --- Steam libraries -------------------------------------------------------------------------------------------------
function Get-SteamLibraries {
    $libs = @()
    foreach ($key in "HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam") {
        $p = Get-ItemProperty $key -ErrorAction SilentlyContinue
        foreach ($name in "SteamPath", "InstallPath") {
            if ($p -and $p.$name) { $libs += ($p.$name -replace '/', '\') }
        }
    }
    foreach ($steam in @($libs)) {
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"(.+)"') { $libs += ($Matches[1] -replace '\\\\', '\') }
            }
        }
    }
    $libs | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') } | Sort-Object -Unique
}
function Find-SteamApp([string]$folder, [string]$probe) {
    foreach ($lib in Get-SteamLibraries) {
        $dir = Join-Path $lib "steamapps\common\$folder"
        if (Test-Path (Join-Path $dir $probe)) { return $dir }
    }
    return ""
}

# --- globalgamemanagers: BuildSettings.enabledVRDevices ----------------------------------------------------------------
# SerializedFile format 17 (Unity 2018.4), type trees stripped. Field order of BuildSettings (class 141):
# scenes, preloadedPlugins, enabledVRDevices, buildTags, buildGUID, ...  Same logic as tools\ggm_vr_patch.py, which also
# has "inspect" and "restore".
function Read-BE32([byte[]]$d, [int]$o) { return ([uint32]$d[$o] -shl 24) -bor ([uint32]$d[$o + 1] -shl 16) -bor ([uint32]$d[$o + 2] -shl 8) -bor [uint32]$d[$o + 3] }
function Read-StringList([byte[]]$d, [ref]$pos) {
    $items = @()
    $n = [BitConverter]::ToInt32($d, $pos.Value); $pos.Value += 4
    for ($i = 0; $i -lt $n; $i++) {
        $len = [BitConverter]::ToInt32($d, $pos.Value); $pos.Value += 4
        $items += [Text.Encoding]::UTF8.GetString($d, $pos.Value, $len)
        $pos.Value += $len
        $pos.Value = [int]([Math]::Floor(($pos.Value + 3) / 4)) * 4
    }
    return , $items
}
function Set-VRDevices([string]$path, [string[]]$devices) {
    [byte[]]$d = [IO.File]::ReadAllBytes($path)
    $version = Read-BE32 $d 8
    $dataOffset = [int](Read-BE32 $d 12)
    if ($version -ne 17) { throw "globalgamemanagers: unsupported SerializedFile version $version (expected 17, Unity 2018.4)" }
    if ($d[16] -ne 0) { throw "globalgamemanagers: big-endian files are not supported" }
    $p = 20
    while ($d[$p] -ne 0) { $p++ }; $p++            # unity version string
    $p += 4                                         # target platform
    if ($d[$p] -ne 0) { throw "globalgamemanagers has type trees; this only handles stripped player builds" }
    $p++
    $typeCount = [BitConverter]::ToInt32($d, $p); $p += 4
    $types = New-Object int[] $typeCount
    for ($i = 0; $i -lt $typeCount; $i++) {
        $classId = [BitConverter]::ToInt32($d, $p); $p += 4
        $p += 3                                     # isStrippedType, scriptTypeIndex
        if ($classId -eq 114) { $p += 16 }
        $p += 16
        $types[$i] = $classId
    }
    $objCount = [BitConverter]::ToInt32($d, $p); $p += 4
    $objects = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $objCount; $i++) {
        $p = [int]([Math]::Floor(($p + 3) / 4)) * 4
        $tablePos = $p
        $p += 8                                     # path id
        $start = [BitConverter]::ToUInt32($d, $p); $p += 4
        $size = [BitConverter]::ToUInt32($d, $p); $p += 4
        $typeIndex = [BitConverter]::ToInt32($d, $p); $p += 4
        [void]$objects.Add([pscustomobject]@{ Start = [int]$start; Size = [int]$size; ClassId = $types[$typeIndex]; TablePos = $tablePos; Index = $i })
    }
    $bs = $objects | Where-Object { $_.ClassId -eq 141 } | Select-Object -First 1
    if (-not $bs) { throw "globalgamemanagers: no BuildSettings object" }
    $base = $dataOffset + $bs.Start
    $pos = $base
    [void](Read-StringList $d ([ref]$pos))          # scenes
    [void](Read-StringList $d ([ref]$pos))          # preloadedPlugins
    $vrStart = $pos
    $current = Read-StringList $d ([ref]$pos)
    $vrEnd = $pos
    if (($current -join ",") -eq ($devices -join ",")) { Write-Host "globalgamemanagers: enabledVRDevices already $($devices -join ',')"; return }

    $backup = $path + ".cloudpunkvr-orig"
    if (-not (Test-Path $backup)) { Copy-Item $path $backup }

    $enc = New-Object IO.MemoryStream
    $enc.Write([BitConverter]::GetBytes([int]$devices.Count), 0, 4)
    foreach ($s in $devices) {
        $b = [Text.Encoding]::UTF8.GetBytes($s)
        $enc.Write([BitConverter]::GetBytes([int]$b.Length), 0, 4)
        $enc.Write($b, 0, $b.Length)
        while ($enc.Length % 4) { $enc.WriteByte(0) }
    }
    $newObj = New-Object IO.MemoryStream
    $newObj.Write($d, $base, $vrStart - $base)
    $encBytes = $enc.ToArray(); $newObj.Write($encBytes, 0, $encBytes.Length)
    $newObj.Write($d, $vrEnd, $base + $bs.Size - $vrEnd)
    $newBytes = $newObj.ToArray()

    # Rebuild the data region in the original order, keeping 8-byte object alignment.
    $head = New-Object byte[] $dataOffset
    [Array]::Copy($d, 0, $head, 0, $dataOffset)
    $body = New-Object IO.MemoryStream
    # Zero-size objects share their start with the next one: the table order breaks the tie.
    foreach ($o in ($objects | Sort-Object Start, Index)) {
        while ($body.Length % 8) { $body.WriteByte(0) }
        $offset = [uint32]$body.Length
        if ($o -eq $bs) { $len = $newBytes.Length; $body.Write($newBytes, 0, $len) }
        else { $len = $o.Size; $body.Write($d, $dataOffset + $o.Start, $len) }
        [Array]::Copy([BitConverter]::GetBytes($offset), 0, $head, $o.TablePos + 8, 4)
        [Array]::Copy([BitConverter]::GetBytes([uint32]$len), 0, $head, $o.TablePos + 12, 4)
    }
    $total = [uint32]($head.Length + $body.Length)
    $head[4] = [byte](($total -shr 24) -band 0xFF); $head[5] = [byte](($total -shr 16) -band 0xFF)
    $head[6] = [byte](($total -shr 8) -band 0xFF); $head[7] = [byte]($total -band 0xFF)
    $tmp = $path + ".tmp"
    $fs = [IO.File]::Create($tmp)
    try { $fs.Write($head, 0, $head.Length); $body.WriteTo($fs) } finally { $fs.Close() }
    Move-Item $tmp $path -Force
    Write-Host "globalgamemanagers: enabledVRDevices $($current -join ',') -> $($devices -join ',')"
}

# --- install ---------------------------------------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $root "build\CloudpunkVR.dll"))) {
    throw "build\CloudpunkVR.dll is missing. Use the release zip (it has the built plugin), or build first: bash tools/build.sh --no-deploy"
}
if (-not $PluginOnly) {
    if (-not $Source) { $Source = Find-SteamApp "Cloudpunk" "Cloudpunk.exe" }
    if (-not $Target) {
        if (-not $Source) { throw "Cloudpunk was not found in any Steam library. Pass -Source <folder with Cloudpunk.exe>." }
        $Target = Join-Path ([IO.Path]::GetPathRoot($Source)) "Games\CloudpunkVR"
    }
    if (-not (Test-Path (Join-Path $Target "Cloudpunk.exe"))) {
        if (-not $Source -or -not (Test-Path (Join-Path $Source "Cloudpunk.exe"))) { throw "Cloudpunk was not found in any Steam library. Pass -Source <folder with Cloudpunk.exe>." }
        Write-Host "copying $Source -> $Target (about 15 GB)"
        robocopy $Source $Target /E /COPY:DAT /DCOPY:T /R:1 /W:1 /MT:8 /NFL /NDL /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
    }
    if (-not (Test-Path (Join-Path $Target "BepInEx\core"))) {
        if (-not $BepInExZip) {
            $BepInExZip = Get-ChildItem (Join-Path $root "vendor") -Filter "BepInEx-Unity.IL2CPP-win-x64-*.zip" -ErrorAction SilentlyContinue |
                Select-Object -First 1 -ExpandProperty FullName
        }
        if (-not $BepInExZip -or -not (Test-Path $BepInExZip)) {
            throw "BepInEx is missing. The release zip has it in vendor\. Otherwise download BepInEx 6 (bleeding edge) 'Unity.IL2CPP-win-x64' from https://builds.bepinex.dev/projects/bepinex_be (tested with build 790) and pass -BepInExZip <zip>."
        }
        Write-Host "unpacking BepInEx"
        Expand-Archive -Path $BepInExZip -DestinationPath $Target -Force
        Remove-Item (Join-Path $Target "changelog.txt") -ErrorAction SilentlyContinue
    }
    # Running outside Steam's library folder: the Steam API needs the app id next to the exe.
    Set-Content -Path (Join-Path $Target "steam_appid.txt") -Value "746850" -NoNewline
    if (-not $OpenVRDll) {
        $steamvr = Find-SteamApp "SteamVR" "bin\win64\openvr_api.dll"
        if (-not $steamvr) { throw "SteamVR was not found in any Steam library (install it from Steam), or pass -OpenVRDll <openvr_api.dll>." }
        $OpenVRDll = Join-Path $steamvr "bin\win64\openvr_api.dll"
    }
    Copy-Item $OpenVRDll (Join-Path $Target "Cloudpunk_Data\Plugins\openvr_api.dll") -Force
    Set-VRDevices (Join-Path $Target "Cloudpunk_Data\globalgamemanagers") @("None", "OpenVR")

    # Graphics jobs: on Direct3D 11 the engine then builds each eye's draw commands on its job threads (legacy graphics
    # jobs) instead of on the main thread, which is what limits the frame rate in VR (PERF.md).
    $boot = Join-Path $Target "Cloudpunk_Data\boot.config"
    $bootText = Get-Content $boot -Raw
    if ($bootText -match '(?m)^gfx-enable-gfx-jobs=') { $bootText = [regex]::Replace($bootText, '(?m)^gfx-enable-gfx-jobs=.*$', 'gfx-enable-gfx-jobs=1') }
    else { $bootText = $bootText.TrimEnd("`r", "`n") + "`ngfx-enable-gfx-jobs=1`n" }
    Set-Content -Path $boot -Value $bootText -NoNewline

    $cfg = Join-Path $Target "BepInEx\config\BepInEx.cfg"
    if (Test-Path $cfg) {
        $text = Get-Content $cfg -Raw
        $text = [regex]::Replace($text, '(\[Logging\.Console\][^\[]*?\nEnabled = )true', '${1}false', 'Singleline')
        Set-Content -Path $cfg -Value $text -NoNewline
    }

    Set-Content -Path (Join-Path $Target "Cloudpunk VR.bat") -Value @"
@echo off
rem Cloudpunk in VR. Steam must be running; SteamVR (or Virtual Desktop -> SteamVR) starts with the game.
start "" "%~dp0Cloudpunk.exe" -vrmode OpenVR -force-d3d11
"@
    Set-Content -Path (Join-Path $Target "Cloudpunk (flat).bat") -Value @"
@echo off
rem Cloudpunk on the desktop, with the CloudpunkVR performance trims. Steam must be running.
start "" "%~dp0Cloudpunk.exe"
"@
}
if (-not $Target) { throw "-PluginOnly needs -Target <the VR copy of the game>." }
if (-not (Test-Path (Join-Path $Target "BepInEx"))) { throw "$Target has no BepInEx folder; run without -PluginOnly first." }

$plugin = Join-Path $Target "BepInEx\plugins\CloudpunkVR"
New-Item -ItemType Directory -Force (Join-Path $plugin "bindings") | Out-Null
Copy-Item (Join-Path $root "build\CloudpunkVR.dll") $plugin -Force
Copy-Item (Join-Path $root "bindings\*.json") (Join-Path $plugin "bindings") -Force
# The passenger-seat doll is optional: the plugin skips it when the folder is not there.
if (Test-Path (Join-Path $root "assets\fufu\fufu.mesh")) {
    New-Item -ItemType Directory -Force (Join-Path $plugin "fufu") | Out-Null
    Copy-Item (Join-Path $root "assets\fufu\*") (Join-Path $plugin "fufu") -Force
}
# Controller layout editor and the player's readme go next to the game.
# The editor's file name is Chinese; a wildcard keeps this script ASCII.
Copy-Item (Join-Path $root "build\Cloudpunk VR *.exe") $Target -Force
Copy-Item (Join-Path $root "README-CloudpunkVR.md") $Target -Force
# Preloader patcher (job worker count); it has to run before the engine starts, so it is not a plugin.
$patcher = Join-Path $Target "BepInEx\patchers\CloudpunkVR"
New-Item -ItemType Directory -Force $patcher | Out-Null
Copy-Item (Join-Path $root "build\CloudpunkVR.Preload.dll") $patcher -Force
Write-Host ""
Write-Host "CloudpunkVR installed in $Target"
Write-Host "Start Steam, then run '$Target\Cloudpunk VR.bat'. Player manual: $Target\README-CloudpunkVR.md"
