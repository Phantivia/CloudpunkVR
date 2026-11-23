using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CloudpunkVR;

// The compositor's own record of each frame (IVRCompositor::GetFrameTimings), summarised in the log every 30 s: how
// often the game hands over a frame, how many vsyncs each frame stays on screen, whether SteamVR reprojects or throttles
// the game, and how much time the compositor sits idle waiting for it. This is what tells a frame-rate cap outside the
// game (the streaming app, SteamVR's pacing) from the game being too slow.
//
// Off unless `[Debug] FrameTimingLog` is on or the dev command `frametiming` asks. The first version fetched up to 128
// records at once into an array sized for 184-byte records; the SteamVR runtime writes its own (larger) record
// stride, ran past the buffer and corrupted the process (an access violation the runtime cannot catch). Now one record
// at a time (GetFrameTiming) goes into a 1 KB native buffer, read back with the fields this layout knows.
internal static class VRFrameTiming
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetGenericInterfaceFn([MarshalAs(UnmanagedType.LPStr)] string version, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool GetFrameTimingFn(IntPtr timing, uint framesAgo);

    // openvr.h Compositor_FrameTiming (pack 8)
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    struct FrameTiming
    {
        public uint Size, FrameIndex, NumFramePresents, NumMisPresented, NumDroppedFrames, ReprojectionFlags;
        public double SystemTimeInSeconds;
        public float PreSubmitGpuMs, PostSubmitGpuMs, TotalRenderGpuMs, CompositorRenderGpuMs, CompositorRenderCpuMs, CompositorIdleCpuMs;
        public float ClientFrameIntervalMs, PresentCallCpuMs, WaitForPresentCpuMs, SubmitFrameMs;
        public float WaitGetPosesCalledMs, NewPosesReadyMs, NewFrameReadyMs, CompositorUpdateStartMs, CompositorUpdateEndMs, CompositorRenderStartMs;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public float[] HmdPose;   // TrackedDevicePose_t, 80 bytes
        public uint NumVSyncsReadyForUse, NumVSyncsToFirstView;
    }

    // The table index of GetFrameTiming per interface version (openvr.h; SubmitWithArrayIndex came in with 027).
    static readonly (string version, int slot)[] Versions = { ("IVRCompositor_022", 8), ("IVRCompositor_027", 9) };

    /** Set by the dev command `frametiming`; the config switch is `[Debug] FrameTimingLog`. */
    public static bool Requested;
    static bool Enabled => Requested || Plugin.Cfg.FrameTimingLog.Value;

    const int NativeBytes = 1024;
    static GetFrameTimingFn getTiming;
    static IntPtr native;
    static string version;
    static float nextLog;
    static bool failed;
    static uint lastFrameIndex;
    static readonly List<FrameTiming> window = new();
    static int structSize;

    static bool Init()
    {
        if (getTiming != null) return true;
        if (failed) return false;
        failed = true;
        var module = GetModuleHandleW("openvr_api.dll");
        if (module == IntPtr.Zero) return false;
        var getInterface = Marshal.GetDelegateForFunctionPointer<GetGenericInterfaceFn>(GetProcAddress(module, "VR_GetGenericInterface"));
        structSize = Marshal.SizeOf<FrameTiming>();
        foreach (var (v, slot) in Versions)
        {
            int err = 0;
            var table = getInterface("FnTable:" + v, ref err);
            if (table == IntPtr.Zero || err != 0) continue;
            getTiming = Marshal.GetDelegateForFunctionPointer<GetFrameTimingFn>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));
            native = Marshal.AllocHGlobal(NativeBytes);
            version = v;
            failed = false;
            Plugin.L.LogInfo($"frame timing: {v}, record {structSize} bytes");
            return true;
        }
        Plugin.L.LogWarning("frame timing: no IVRCompositor interface");
        return false;
    }

    // Every 20 frames, the records since the last poll, newest first, one at a time.
    public static void Update()
    {
        if (!Enabled || !UnityEngine.XR.XRSettings.enabled) return;
        if (Time.frameCount % 20 != 0) return;
        try
        {
            if (!Init()) return;
            uint newest = 0;
            for (uint ago = 0; ago < 64; ago++)
            {
                for (int i = 0; i < NativeBytes; i += 8) Marshal.WriteInt64(native, i, 0);
                Marshal.WriteInt32(native, 0, structSize);
                if (!getTiming(native, ago)) break;
                var t = Marshal.PtrToStructure<FrameTiming>(native);
                if (t.FrameIndex <= lastFrameIndex) break;
                newest = Math.Max(newest, t.FrameIndex);
                window.Add(t);
            }
            lastFrameIndex = Math.Max(lastFrameIndex, newest);
            if (Time.unscaledTime < nextLog) return;
            nextLog = Time.unscaledTime + 30f;
            Report();
        }
        catch (Exception e)
        {
            failed = true;
            getTiming = null;
            Plugin.L.LogWarning("frame timing: " + e.Message);
        }
    }

    // Also on request (dev "frametiming"). One entry per game frame. Flags as decoded with the openvr 1.x masks of this
    // interface version: 0x08 motion reprojection, 0x30 extra frames of prediction, 0xC0 frames the game is throttled by
    // (newer headers widen these to 0xF0 / 0xF00, so the raw values are logged too). Under a driver direct mode headset
    // (Virtual Desktop) the presents, reprojection and throttle come from the driver's own frame timing report, which is
    // how a streamer's spacewarp makes SteamVR run the game at a fraction of the refresh rate.
    static int lastGameFrame = -1;
    static float lastGameTime;

    public static void Report()
    {
        int gf = Time.frameCount;
        float gt = Time.unscaledTime;
        string gameRate = lastGameFrame >= 0 && gt > lastGameTime ? $"{(gf - lastGameFrame) / (gt - lastGameTime):F1} fps" : "-";
        lastGameFrame = gf;
        lastGameTime = gt;
        if (window.Count == 0) { Plugin.L.LogInfo($"frame timing: game {gameRate}; no compositor frames recorded"); return; }
        var w = window.OrderBy(t => t.FrameIndex).ToList();
        window.Clear();
        float Avg(Func<FrameTiming, float> f) => w.Average(f);
        string Hist(Func<FrameTiming, uint> f) => string.Join(" ", w.GroupBy(f).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"));
        int motion = w.Count(t => (t.ReprojectionFlags & 0x08) != 0);
        Plugin.L.LogInfo($"frame timing: game {gameRate}; {w.Count} compositor frames; throttled by {Hist(t => (t.ReprojectionFlags & 0xC0) >> 6)} frames, " +
                         $"extra prediction {Hist(t => (t.ReprojectionFlags & 0x30) >> 4)}, motion smoothing on {motion}/{w.Count}, " +
                         $"reprojected (cpu/gpu) {w.Count(t => (t.ReprojectionFlags & 0x03) != 0)}; presents per frame {Hist(t => t.NumFramePresents)}, " +
                         $"mispresented {w.Sum(t => t.NumMisPresented)}, dropped {w.Sum(t => t.NumDroppedFrames)}; raw flags " +
                         string.Join(" ", w.GroupBy(t => t.ReprojectionFlags).OrderBy(g => g.Key).Select(g => $"0x{g.Key:X}:{g.Count()}")));
        Plugin.L.LogInfo($"frame timing: game GPU {Avg(t => t.PreSubmitGpuMs):F2} ms, compositor GPU {Avg(t => t.CompositorRenderGpuMs):F2} CPU {Avg(t => t.CompositorRenderCpuMs):F2}, " +
                         $"compositor idle (time the game could still have used) {Avg(t => t.CompositorIdleCpuMs):F2} ms; WaitGetPoses called {Avg(t => t.WaitGetPosesCalledMs):F2} ms, " +
                         $"poses handed back {Avg(t => t.NewPosesReadyMs):F2}, frame submitted {Avg(t => t.NewFrameReadyMs):F2}");
    }
}
