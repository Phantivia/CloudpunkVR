using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CloudpunkVR;

// Test and film sessions out of the operator's way (dev channel): the game window sits on another screen and never keeps
// the mouse or the keyboard focus.
//   -cloudpunkvr-window x,y       top-left of the window in physical desktop pixels (per-monitor DPI coordinates);
//                                 put back there whenever the game resizes or moves it
//   -cloudpunkvr-refocus <hwnd>   for the first 30 s, whenever the game window comes to the front, the foreground goes
//                                 back to this window (tools/launch-dev.ps1 passes the one that had it at launch)
// With either flag the cursor is never locked or hidden by the game (it locks it for mouse look).
internal static class DevWindow
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32")] static extern bool ClipCursor(IntPtr rect);

    static readonly string[] Args = Environment.GetCommandLineArgs();
    static readonly (int x, int y)? Pos = ParsePos();
    static readonly IntPtr Refocus = ParseHwnd();
    static IntPtr hwnd;
    static float nextCheck;
    static int lastW, lastH;

    public static bool On => DevCommands.Enabled && (Pos != null || Refocus != IntPtr.Zero);

    static string Arg(string name)
    {
        int i = Array.FindIndex(Args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
    }

    static (int, int)? ParsePos()
    {
        var s = Arg("-cloudpunkvr-window");
        if (s == null) return null;
        var p = s.Split(',');
        return p.Length == 2 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y) ? (x, y) : null;
    }

    static IntPtr ParseHwnd() => long.TryParse(Arg("-cloudpunkvr-refocus"), out long h) ? (IntPtr)h : IntPtr.Zero;

    public static void Update()
    {
        if (!On) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        float now = Time.unscaledTime;
        bool resized = Screen.width != lastW || Screen.height != lastH;
        if (!resized && now < nextCheck) return;
        nextCheck = now + 0.5f;
        lastW = Screen.width; lastH = Screen.height;
        if (hwnd == IntPtr.Zero) hwnd = Process.GetCurrentProcess().MainWindowHandle;
        if (hwnd == IntPtr.Zero) return;
        if (Pos is (int x, int y))
        {
            var old = SetThreadDpiAwarenessContext((IntPtr)(-4));   // per-monitor v2: physical pixels
            if (GetWindowRect(hwnd, out var r) && (r.Left != x || r.Top != y))
            {
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
                Plugin.L.LogInfo($"dev window: moved to {x},{y} ({Screen.width}x{Screen.height})");
            }
            SetThreadDpiAwarenessContext(old);
        }
        if (Refocus != IntPtr.Zero && now < 30f && GetForegroundWindow() == hwnd)
        {
            ClipCursor(IntPtr.Zero);
            SetForegroundWindow(Refocus);
            Plugin.L.LogInfo("dev window: gave the foreground back");
        }
    }
}
