using System;
using System.Runtime.InteropServices;

namespace CloudpunkVR;

// Hybrid CPUs (performance + efficiency cores): Windows 11 lowers the scheduling QoS of processes whose window is not
// in the foreground and moves their threads to efficiency cores. In VR the game window usually is not in front (the
// user looks at the headset, focus sits on SteamVR or the streamer), so the main thread can end up on a slow core.
// Opting the process out of execution-speed power throttling keeps it at full QoS; this is per process and touches no
// system setting. Even in front, the engine's two serial threads (main and render) get pushed to efficiency cores
// when the many job and driver threads crowd the performance cores. Pinning those two to the fast cores with CPU sets
// measured slower (they then wait for a busy fast core), so it is only a dev command ("pin").
internal static unsafe class CpuTopology
{
    [DllImport("kernel32")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] static extern int SetProcessInformation(IntPtr process, int infoClass, void* info, int size);
    [DllImport("kernel32")] static extern int GetSystemCpuSetInformation(byte* info, int length, out int returned, IntPtr process, int flags);
    [DllImport("kernel32")] static extern void GetCurrentProcessorNumberEx(out uint processor);   // PROCESSOR_NUMBER: WORD Group, BYTE Number, BYTE Reserved

    const int ProcessPowerThrottling = 4;
    const int ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;

    [DllImport("kernel32")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32")] static extern int SetThreadSelectedCpuSets(IntPtr thread, uint* ids, uint count);

    static byte[] efficiency = Array.Empty<byte>();
    static byte topClass;
    static uint[] fastSets = Array.Empty<uint>();

    // PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask, StateMask }: a controlled bit with state 0 is
    // explicitly off; an empty ControlMask hands the decision back to Windows.
    public static void SetFullSpeed(bool on)
    {
        int* state = stackalloc int[3];
        state[0] = 1;
        state[1] = on ? ExecutionSpeed | IgnoreTimerResolution : 0;
        state[2] = 0;
        bool ok = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, state, 12) != 0;
        Plugin.L.LogInfo(!ok ? "cpu: power throttling control not available: " + Marshal.GetLastWin32Error()
                         : on ? $"cpu: opted out of power throttling ({FastCores} fastest of {efficiency.Length} cores)"
                              : "cpu: power throttling back under Windows control");
    }

    static int FastCores => Array.FindAll(efficiency, c => c == topClass).Length;

    // Efficiency class per logical processor (higher = faster core); all equal on non-hybrid CPUs.
    public static void Load()
    {
        GetSystemCpuSetInformation(null, 0, out int length, IntPtr.Zero, 0);
        if (length <= 0) return;
        var buffer = new byte[length];
        var classes = new byte[256];
        var ids = new uint[256];
        int max = -1;
        fixed (byte* b = buffer)
        {
            if (GetSystemCpuSetInformation(b, length, out length, IntPtr.Zero, 0) == 0) return;
            for (int o = 0; o + 20 <= length;)
            {
                int size = *(int*)(b + o);
                if (size <= 0) break;
                if (*(int*)(b + o + 4) == 0)   // CpuSetInformation: Id at 8, LogicalProcessorIndex at 14, EfficiencyClass at 18
                {
                    int lp = b[o + 14];
                    ids[lp] = *(uint*)(b + o + 8);
                    classes[lp] = b[o + 18];
                    if (classes[lp] > topClass) topClass = classes[lp];
                    max = Math.Max(max, lp);
                }
                o += size;
            }
        }
        efficiency = classes.AsSpan(0, max + 1).ToArray();
        var fast = new System.Collections.Generic.List<uint>();
        for (int lp = 0; lp <= max; lp++) if (efficiency[lp] == topClass) fast.Add(ids[lp]);
        fastSets = fast.ToArray();
    }

    // Soft affinity of the calling thread to the fastest cores (CPU sets), or back to any core.
    public static void PinCurrentThread(bool on)
    {
        if (!Hybrid) return;
        fixed (uint* p = fastSets) SetThreadSelectedCpuSets(GetCurrentThread(), on ? p : null, on ? (uint)fastSets.Length : 0);
    }

    // The engine's render thread only runs our code inside plugin events.
    [UnmanagedCallersOnly]
    static void PinRenderThreadEvent(int on)
    {
        try { PinCurrentThread(on != 0); } catch { }
    }

    public static void PinEngineThreads(bool on)
    {
        if (!Hybrid) return;
        PinCurrentThread(on);
        UnityEngine.GL.IssuePluginEvent((IntPtr)(delegate* unmanaged<int, void>)&PinRenderThreadEvent, on ? 1 : 0);
        Plugin.L.LogInfo(on ? $"cpu: main and render thread prefer the {fastSets.Length} fastest cores" : "cpu: main and render thread on any core");
    }

    public static bool Hybrid => efficiency.Length > 0 && Array.Exists(efficiency, c => c != topClass);

    // True when the calling thread currently runs on one of the fastest cores.
    public static bool OnFastCore()
    {
        GetCurrentProcessorNumberEx(out uint processor);
        int lp = (int)(processor >> 16) & 0xFF;
        return lp >= efficiency.Length || efficiency[lp] == topClass;
    }
}
