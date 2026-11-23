using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;

namespace CloudpunkVR.Preload;

// Unity 2018 sizes its job system once, at engine start, from GetSystemInfo().dwNumberOfProcessors (one worker per
// logical processor minus one: 23 on a 24-core laptop). Most of its jobs are tiny, and every batch wakes the sleeping
// workers through a semaphore, which costs the main thread far more than the work it hands out. BepInEx runs
// preloader patchers inside il2cpp_init, before the engine creates the job system, so this patcher can answer
// UnityPlayer.dll's GetSystemInfo with a smaller processor count. Nothing else in the structure changes.
[PatcherPluginInfo("palai.cloudpunkvr.preload", "CloudpunkVR job workers", "0.1.0")]
public unsafe class JobWorkers : BasePatcher
{
    [DllImport("kernel32")] static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string name);
    [DllImport("kernel32")] static extern int VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);

    static IntPtr realGetSystemInfo;
    static uint processors;

    public override void Initialize()
    {
        var log = Logger.CreateLogSource("CloudpunkVR.Preload");
        var cfg = new ConfigFile(Path.Combine(Paths.ConfigPath, "palai.cloudpunkvr.cfg"), false);
        int workers = cfg.Bind("Performance", "JobWorkerThreads", -1, Description).Value;
        if (int.TryParse(Environment.GetEnvironmentVariable("CLOUDPUNKVR_JOBWORKERS"), out int forced)) workers = forced;
        int engineDefault = Environment.ProcessorCount - 1;
        if (workers < 0) workers = Math.Min(engineDefault, AutoWorkers);
        if (workers == 0 || workers >= engineDefault) { log.LogInfo($"job workers: engine default ({engineDefault})"); return; }
        processors = (uint)workers + 1;   // the engine starts one worker per processor except the main thread's
        var player = GetModuleHandleW("UnityPlayer.dll");
        if (player == IntPtr.Zero) { log.LogWarning("job workers: UnityPlayer.dll not loaded yet"); return; }
        var slot = FindImport((byte*)player, "kernel32.dll", "GetSystemInfo");
        if (slot == null) { log.LogWarning("job workers: GetSystemInfo import not found"); return; }
        realGetSystemInfo = *slot;
        if (VirtualProtect((IntPtr)slot, (UIntPtr)8, 0x04, out uint old) == 0) { log.LogWarning("job workers: cannot patch import"); return; }
        *slot = (IntPtr)(delegate* unmanaged<byte*, void>)&GetSystemInfoHook;
        VirtualProtect((IntPtr)slot, (UIntPtr)8, old, out _);
        log.LogInfo($"job workers: engine will start {workers} (machine has {Environment.ProcessorCount} logical processors)");
    }

    // Measured on a 24-core laptop: 8 workers instead of the engine's 23 cut frame time by a quarter on the desktop and
    // the main thread's time per VR frame by a fifth; 4 and 6 were no better within the noise.
    const int AutoWorkers = 8;

    public const string Description =
        "Number of Unity job worker threads: -1 = automatic (at most 8), 0 = engine default (one per logical processor minus one), " +
        "N = exactly N. Takes effect at the next start.";

    [UnmanagedCallersOnly]
    static void GetSystemInfoHook(byte* info)
    {
        ((delegate* unmanaged<byte*, void>)realGetSystemInfo)(info);
        uint* count = (uint*)(info + 0x20);   // SYSTEM_INFO.dwNumberOfProcessors
        if (*count > processors) *count = processors;
    }

    // Import address table slot of dll!name in a mapped PE image.
    static IntPtr* FindImport(byte* image, string dll, string name)
    {
        byte* nt = image + *(int*)(image + 0x3C);
        byte* opt = nt + 24;
        uint importRva = *(uint*)(opt + 112 + 8);
        for (byte* desc = image + importRva; *(uint*)(desc + 12) != 0; desc += 20)
        {
            string module = Marshal.PtrToStringAnsi((IntPtr)(image + *(uint*)(desc + 12)));
            if (!module.Equals(dll, StringComparison.OrdinalIgnoreCase)) continue;
            ulong* lookup = (ulong*)(image + (*(uint*)desc != 0 ? *(uint*)desc : *(uint*)(desc + 16)));
            IntPtr* slots = (IntPtr*)(image + *(uint*)(desc + 16));
            for (int i = 0; lookup[i] != 0; i++)
            {
                if ((lookup[i] >> 63) != 0) continue;   // imported by ordinal
                if (Marshal.PtrToStringAnsi((IntPtr)(image + (uint)lookup[i] + 2)) == name) return &slots[i];
            }
        }
        return null;
    }
}
