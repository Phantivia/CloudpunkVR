<#
Starts the game in the dev channel without taking the operator's mouse or keyboard focus: the window opens without
being activated, the plugin puts it on the second screen and hands the foreground back to whatever had it.

  tools\launch-dev.ps1                       # -cloudpunkvr-dev -cloudpunkvr-nosave, window at 2600,-150 (second screen)
  tools\launch-dev.ps1 -Window 2600,-150 -Audible
  tools\launch-dev.ps1 -RestoreRegistry     # after the session: the game's own window settings back

-Window is the window's top-left in physical desktop pixels. This machine: main screen 2560x1600 at 0,0 (150 %), second
screen 2560x1440 to its right at x 2560-5120, y -173-1267 (100 %). Write cmd.txt before launching.
#>
param(
    [string]$Game = "G:\Games\CloudpunkVR",
    [string]$Window = "2600,-150",
    [switch]$Audible,
    [switch]$RestoreRegistry
)
$ErrorActionPreference = "Stop"
if ($RestoreRegistry) {
    $backup = Join-Path $env:LOCALAPPDATA "CloudpunkVR-window-registry.json"
    if (Get-Process Cloudpunk -ErrorAction SilentlyContinue) { throw "close the game first (it writes these on exit)" }
    if (-not (Test-Path $backup)) { "no backup"; return }
    $vals = Get-Content $backup -Raw | ConvertFrom-Json
    foreach ($p in $vals.PSObject.Properties) { Set-ItemProperty "HKCU:\Software\ION LANDS\Cloudpunk" $p.Name $p.Value }
    Remove-Item $backup
    "restored the game's window settings"; return
}
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DevLaunch {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, int flags,
        IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
}
"@
if (Get-Process Cloudpunk -ErrorAction SilentlyContinue) { throw "the game is already running" }
# Start windowed at the test size: the game's own startup otherwise goes borderless-fullscreen on the main screen for a
# moment. The game's values are kept in a backup next to this script's output and put back with -RestoreRegistry.
$key = "HKCU:\Software\ION LANDS\Cloudpunk"
$backup = Join-Path $env:LOCALAPPDATA "CloudpunkVR-window-registry.json"
$names = (Get-ItemProperty $key).PSObject.Properties.Name | Where-Object { $_ -match '^(Screenmanager (Resolution (Width|Height|Use Native)|Fullscreen mode)_h|Window(Width|Height)_h)' }
if (-not (Test-Path $backup)) {
    $vals = @{}; foreach ($n in $names) { $vals[$n] = (Get-ItemProperty $key).$n }
    $vals | ConvertTo-Json | Set-Content $backup
}
foreach ($n in $names) {
    $v = switch -Regex ($n) { 'Fullscreen mode' { 3 } 'Use Native' { 0 } 'Width' { 1280 } 'Height' { 720 } }
    Set-ItemProperty $key $n $v
}
$fg = [DevLaunch]::GetForegroundWindow()
$exe = Join-Path $Game "Cloudpunk.exe"
$cmd = "`"$exe`" -cloudpunkvr-dev -cloudpunkvr-nosave -cloudpunkvr-window $Window -cloudpunkvr-refocus $([int64]$fg)"
if ($Audible) { $cmd += " -cloudpunkvr-audible" }
$si = New-Object DevLaunch+STARTUPINFO
$si.cb = [Runtime.InteropServices.Marshal]::SizeOf($si)
$si.dwFlags = 1          # STARTF_USESHOWWINDOW
$si.wShowWindow = 4      # SW_SHOWNOACTIVATE
$pi = New-Object DevLaunch+PROCESS_INFORMATION
if (-not [DevLaunch]::CreateProcessW($exe, $cmd, [IntPtr]::Zero, [IntPtr]::Zero, $false, 0, [IntPtr]::Zero, $Game, [ref]$si, [ref]$pi)) {
    throw "CreateProcess failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
}
[DevLaunch]::CloseHandle($pi.hThread) | Out-Null
[DevLaunch]::CloseHandle($pi.hProcess) | Out-Null
"started pid $($pi.dwProcessId), window $Window, focus back to $fg"
