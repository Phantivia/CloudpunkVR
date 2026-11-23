using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace CloudpunkVR;

// Dev-only GPU timeline. D3D11 timestamp queries are issued on the render thread (plugin events) at every camera
// event of every enabled camera, at the shadow events of shadowed lights and at frame begin/end. Consecutive
// timestamps split a frame into segments "A -> B"; each segment also gets the render thread's own time and,
// optionally, the draw calls it issued (counted by patching the immediate context's vtable for the session).
// Timestamps measure elapsed GPU time: when the frame is CPU-bound a segment also contains GPU idle time.
internal static unsafe class GpuTimer
{
    [DllImport("kernel32")] static extern int VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);

    const int Slots = 6, MaxMarks = 192;
    const int StartId = 2_000_000, StopId = 2_000_001, FrameBeginId = 2_000_002, FrameEndId = 2_000_003;

    static IntPtr device, context, callback;
    static IntPtr* vtbl;
    static readonly IntPtr[] tsQuery = new IntPtr[Slots * MaxMarks], disjointQuery = new IntPtr[Slots];
    static readonly int[] markId = new int[Slots * MaxMarks], markDraws = new int[Slots * MaxMarks];
    static readonly long[] markCpu = new long[Slots * MaxMarks];
    static readonly int[] markCount = new int[Slots];
    static readonly bool[] pending = new bool[Slots];
    static int slot, drawCount;
    static long instanceCount;
    static bool renderActive, frameOpen;
    static volatile bool stoppedOnRenderThread;

    sealed class Segment { public int Order; public double GpuMs, CpuMs; public long Draws; }
    static readonly object sync = new();
    static readonly Dictionary<(int from, int to, int occurrence), Segment> segments = new();
    static long framesMeasured, framesDropped, instancesMeasured, framesSeen;
    static double frameGpuMs, frameCpuMs;

    static readonly Dictionary<int, string> names = new();
    static readonly List<(Camera cam, CameraEvent evt, CommandBuffer cb)> camBuffers = new();
    static readonly List<(Light light, LightEvent evt, CommandBuffer cb)> lightBuffers = new();
    static bool active, hooked, countingDraws, waitingForReport;
    static float until;
    static string label, startState;
    public static volatile string PendingReport;

    static readonly CameraEvent[] DeferredEvents =
    {
        CameraEvent.BeforeGBuffer, CameraEvent.AfterGBuffer, CameraEvent.BeforeReflections, CameraEvent.AfterReflections,
        CameraEvent.BeforeLighting, CameraEvent.AfterLighting, CameraEvent.BeforeFinalPass, CameraEvent.AfterFinalPass,
        CameraEvent.BeforeForwardOpaque, CameraEvent.AfterForwardOpaque, CameraEvent.BeforeImageEffectsOpaque,
        CameraEvent.AfterImageEffectsOpaque, CameraEvent.BeforeSkybox, CameraEvent.AfterSkybox,
        CameraEvent.BeforeHaloAndLensFlares, CameraEvent.AfterHaloAndLensFlares, CameraEvent.BeforeForwardAlpha,
        CameraEvent.AfterForwardAlpha, CameraEvent.BeforeImageEffects, CameraEvent.AfterImageEffects, CameraEvent.AfterEverything,
    };
    static readonly CameraEvent[] ForwardEvents =
    {
        CameraEvent.BeforeDepthTexture, CameraEvent.AfterDepthTexture, CameraEvent.BeforeForwardOpaque, CameraEvent.AfterForwardOpaque,
        CameraEvent.BeforeImageEffectsOpaque, CameraEvent.AfterImageEffectsOpaque, CameraEvent.BeforeSkybox, CameraEvent.AfterSkybox,
        CameraEvent.BeforeForwardAlpha, CameraEvent.AfterForwardAlpha, CameraEvent.BeforeImageEffects, CameraEvent.AfterImageEffects,
        CameraEvent.AfterEverything,
    };
    static readonly LightEvent[] ShadowEvents = { LightEvent.BeforeShadowMap, LightEvent.AfterShadowMap, LightEvent.BeforeScreenspaceMask, LightEvent.AfterScreenspaceMask };

    // ---- main thread ------------------------------------------------------------------------------------------------

    public static void Start(float seconds, string runLabel, bool countDraws)
    {
        if (active || waitingForReport) { Plugin.L.LogWarning("gpu: session already running"); return; }
        if (!EnsureDevice()) return;
        label = runLabel;
        startState = Diag.StateLine();
        names.Clear();
        names[FrameBeginId] = "frame begin (Update)";
        names[FrameEndId] = "frame end (WaitForEndOfFrame)";
        // Includes disabled cameras: the cockpit interior camera is rendered by script with its component off.
        var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
        for (int c = 0; c < cams.Length; c++)
        {
            var cam = cams[c];
            var events = cam.actualRenderingPath == RenderingPath.DeferredShading ? DeferredEvents : ForwardEvents;
            foreach (var evt in events)
            {
                int id = 1000 + c * 32 + (int)evt;
                var cb = new CommandBuffer { name = "CloudpunkVR GPU timer" };
                cb.IssuePluginEvent(callback, id);
                cam.AddCommandBuffer(evt, cb);
                camBuffers.Add((cam, evt, cb));
                names[id] = $"{cam.name}.{evt}";
            }
        }
        var lights = UnityEngine.Object.FindObjectsOfType<Light>();
        int l = 0;
        foreach (var light in lights)
        {
            if (light.shadows == LightShadows.None || !light.isActiveAndEnabled) continue;
            foreach (var evt in ShadowEvents)
            {
                int id = 500_000 + l * 8 + (int)evt;
                var cb = new CommandBuffer { name = "CloudpunkVR GPU timer" };
                cb.IssuePluginEvent(callback, id);
                light.AddCommandBuffer(evt, cb);
                lightBuffers.Add((light, evt, cb));
                names[id] = $"light '{light.name}' ({light.type}).{evt}";
            }
            l++;
        }
        countingDraws = countDraws;
        if (countDraws) Hook(true);
        lock (sync) { segments.Clear(); framesMeasured = framesDropped = instancesMeasured = 0; frameGpuMs = frameCpuMs = 0; }
        stoppedOnRenderThread = false;
        GL.IssuePluginEvent(callback, StartId);
        active = true;
        until = Time.unscaledTime + seconds;
        Plugin.L.LogInfo($"gpu: timing {cams.Length} cameras, {l} shadowed lights, {camBuffers.Count + lightBuffers.Count} markers{(countDraws ? ", counting draws" : "")}");
    }

    // Once per frame from Update (dev channel).
    public static void Update()
    {
        if (active)
        {
            GL.IssuePluginEvent(callback, FrameBeginId);
            if (Time.unscaledTime >= until) Stop();
        }
        else if (waitingForReport && stoppedOnRenderThread)
        {
            waitingForReport = false;
            if (hooked) Hook(false);
            PendingReport = Report();
        }
    }

    // From the WaitForEndOfFrame coroutine.
    public static void EndOfFrame()
    {
        if (active) GL.IssuePluginEvent(callback, FrameEndId);
    }

    static void Stop()
    {
        active = false;
        foreach (var (cam, evt, cb) in camBuffers) if (cam != null) cam.RemoveCommandBuffer(evt, cb);
        foreach (var (light, evt, cb) in lightBuffers) if (light != null) light.RemoveCommandBuffer(evt, cb);
        camBuffers.Clear();
        lightBuffers.Clear();
        GL.IssuePluginEvent(callback, StopId);
        waitingForReport = true;
    }

    static bool EnsureDevice()
    {
        if (device != IntPtr.Zero) return true;
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
        {
            Plugin.L.LogWarning("gpu: needs Direct3D 11, running on " + SystemInfo.graphicsDeviceType);
            return false;
        }
        var rt = new RenderTexture(4, 4, 0);
        rt.Create();
        IntPtr tex = rt.GetNativeTexturePtr(), dev = IntPtr.Zero, ctx = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, IntPtr*, void>)(*(IntPtr**)tex)[3])(tex, &dev);          // ID3D11DeviceChild::GetDevice
        rt.Release();
        UnityEngine.Object.Destroy(rt);
        if (dev == IntPtr.Zero) { Plugin.L.LogWarning("gpu: no D3D11 device"); return false; }
        ((delegate* unmanaged<IntPtr, IntPtr*, void>)(*(IntPtr**)dev)[40])(dev, &ctx);          // ID3D11Device::GetImmediateContext
        device = dev;
        context = ctx;
        vtbl = *(IntPtr**)ctx;
        var createQuery = (delegate* unmanaged<IntPtr, int*, IntPtr*, int>)(*(IntPtr**)dev)[24]; // ID3D11Device::CreateQuery
        int* desc = stackalloc int[2];
        for (int i = 0; i < tsQuery.Length + Slots; i++)
        {
            desc[0] = i < tsQuery.Length ? 2 : 3;   // D3D11_QUERY_TIMESTAMP, D3D11_QUERY_TIMESTAMP_DISJOINT
            desc[1] = 0;
            IntPtr q = IntPtr.Zero;
            if (createQuery(dev, desc, &q) < 0) { Plugin.L.LogWarning("gpu: CreateQuery failed"); device = IntPtr.Zero; return false; }
            if (i < tsQuery.Length) tsQuery[i] = q; else disjointQuery[i - tsQuery.Length] = q;
        }
        callback = (IntPtr)(delegate* unmanaged<int, void>)&OnRenderEvent;
        return true;
    }

    // ---- call counting on the immediate context's vtable ----------------------------------------------------------------
    // ID3D11DeviceContext slots: DrawIndexed 12, Draw 13, DrawIndexedInstanced 20, DrawInstanced 21,
    // DrawIndexedInstancedIndirect 39, DrawInstancedIndirect 40, Dispatch 41, ExecuteCommandList 58.

    static readonly int[] HookSlots = { 12, 13, 20, 21, 39, 40, 41, 58 };
    static readonly IntPtr[] orig = new IntPtr[HookSlots.Length];
    static long indirectCount, dispatchCount, executeCount;

    static void Hook(bool on)
    {
        var region = (IntPtr)(vtbl + 12);
        var size = (UIntPtr)((58 - 12 + 1) * sizeof(IntPtr));
        if (VirtualProtect(region, size, 0x04, out uint old) == 0)
        {
            Plugin.L.LogWarning("gpu: cannot patch the context vtable, draws not counted");
            return;
        }
        if (on)
        {
            Symbols.Refresh();
            for (int i = 0; i < HookSlots.Length; i++) orig[i] = vtbl[HookSlots[i]];
            Plugin.L.LogInfo($"gpu: context vtable in {Symbols.ImageOf((ulong)vtbl)?.Name}, DrawIndexed in {Symbols.ImageOf((ulong)orig[0])?.Name}");
            vtbl[12] = (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, int, void>)&DrawIndexedHook;
            vtbl[13] = (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, void>)&DrawHook;
            vtbl[20] = (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, int, uint, void>)&DrawIndexedInstancedHook;
            vtbl[21] = (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)&DrawInstancedHook;
            vtbl[39] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, void>)&DrawIndexedInstancedIndirectHook;
            vtbl[40] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, void>)&DrawInstancedIndirectHook;
            vtbl[41] = (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, void>)&DispatchHook;
            vtbl[58] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, void>)&ExecuteCommandListHook;
        }
        else
        {
            for (int i = 0; i < HookSlots.Length; i++) vtbl[HookSlots[i]] = orig[i];
        }
        VirtualProtect(region, size, old, out _);
        hooked = on;
    }

    [UnmanagedCallersOnly]
    static void DrawIndexedHook(IntPtr self, uint count, uint start, int baseVertex)
    {
        drawCount++;
        ((delegate* unmanaged<IntPtr, uint, uint, int, void>)orig[0])(self, count, start, baseVertex);
    }

    [UnmanagedCallersOnly]
    static void DrawHook(IntPtr self, uint count, uint start)
    {
        drawCount++;
        ((delegate* unmanaged<IntPtr, uint, uint, void>)orig[1])(self, count, start);
    }

    [UnmanagedCallersOnly]
    static void DrawIndexedInstancedHook(IntPtr self, uint count, uint instances, uint start, int baseVertex, uint startInstance)
    {
        drawCount++;
        instanceCount += instances;
        ((delegate* unmanaged<IntPtr, uint, uint, uint, int, uint, void>)orig[2])(self, count, instances, start, baseVertex, startInstance);
    }

    [UnmanagedCallersOnly]
    static void DrawInstancedHook(IntPtr self, uint count, uint instances, uint start, uint startInstance)
    {
        drawCount++;
        instanceCount += instances;
        ((delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)orig[3])(self, count, instances, start, startInstance);
    }

    [UnmanagedCallersOnly]
    static void DrawIndexedInstancedIndirectHook(IntPtr self, IntPtr args, uint offset)
    {
        drawCount++;
        indirectCount++;
        ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)orig[4])(self, args, offset);
    }

    [UnmanagedCallersOnly]
    static void DrawInstancedIndirectHook(IntPtr self, IntPtr args, uint offset)
    {
        drawCount++;
        indirectCount++;
        ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)orig[5])(self, args, offset);
    }

    [UnmanagedCallersOnly]
    static void DispatchHook(IntPtr self, uint x, uint y, uint z)
    {
        dispatchCount++;
        ((delegate* unmanaged<IntPtr, uint, uint, uint, void>)orig[6])(self, x, y, z);
    }

    [UnmanagedCallersOnly]
    static void ExecuteCommandListHook(IntPtr self, IntPtr list, int restore)
    {
        executeCount++;
        ((delegate* unmanaged<IntPtr, IntPtr, int, void>)orig[7])(self, list, restore);
    }

    // ---- render thread ------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    static void OnRenderEvent(int id)
    {
        try
        {
            switch (id)
            {
                case StartId:
                    renderActive = true;
                    frameOpen = false;
                    slot = 0;
                    framesSeen = indirectCount = dispatchCount = executeCount = 0;
                    Array.Clear(pending, 0, Slots);
                    break;
                case StopId:
                    if (frameOpen) CloseFrame();
                    renderActive = false;
                    stoppedOnRenderThread = true;
                    break;
                case FrameBeginId:
                    if (!renderActive) break;
                    if (frameOpen) CloseFrame();
                    framesSeen++;
                    slot = (slot + 1) % Slots;
                    if (pending[slot]) Collect(slot);
                    ((delegate* unmanaged<IntPtr, IntPtr, void>)vtbl[27])(context, disjointQuery[slot]);   // Begin
                    markCount[slot] = 0;
                    frameOpen = true;
                    drawCount = 0;
                    instanceCount = 0;
                    Mark(FrameBeginId);
                    break;
                default:
                    if (!renderActive || !frameOpen) break;
                    Mark(id);
                    if (id == FrameEndId) CloseFrame();
                    break;
            }
        }
        catch { }   // never unwind into the engine's render thread
    }

    static void Mark(int id)
    {
        int i = markCount[slot];
        if (i >= MaxMarks) return;
        int k = slot * MaxMarks + i;
        ((delegate* unmanaged<IntPtr, IntPtr, void>)vtbl[28])(context, tsQuery[k]);                   // End(timestamp)
        markId[k] = id;
        markDraws[k] = drawCount;
        markCpu[k] = Stopwatch.GetTimestamp();
        drawCount = 0;
        markCount[slot] = i + 1;
    }

    static void CloseFrame()
    {
        ((delegate* unmanaged<IntPtr, IntPtr, void>)vtbl[28])(context, disjointQuery[slot]);           // End(disjoint)
        pending[slot] = true;
        frameOpen = false;
        lock (sync) instancesMeasured += instanceCount;
    }

    static void Collect(int s)
    {
        pending[s] = false;
        var getData = (delegate* unmanaged<IntPtr, IntPtr, void*, uint, uint, int>)vtbl[29];
        ulong* dj = stackalloc ulong[2];
        // D3D11_ASYNC_GETDATA_DONOTFLUSH; S_FALSE (1) means not ready yet: drop the frame rather than stall.
        if (getData(context, disjointQuery[s], dj, 16, 1) != 0 || ((int*)dj)[2] != 0 || dj[0] == 0) { framesDropped++; return; }
        int n = markCount[s];
        ulong* t = stackalloc ulong[MaxMarks];
        for (int i = 0; i < n; i++)
            if (getData(context, tsQuery[s * MaxMarks + i], &t[i], 8, 1) != 0) { framesDropped++; return; }
        double toMs = 1000.0 / dj[0], cpuToMs = 1000.0 / Stopwatch.Frequency;
        lock (sync)
        {
            for (int i = 1; i < n; i++)
            {
                int k = s * MaxMarks + i;
                int from = markId[k - 1], to = markId[k], occurrence = 0;
                for (int j = 1; j < i; j++)
                    if (markId[s * MaxMarks + j - 1] == from && markId[s * MaxMarks + j] == to) occurrence++;
                if (!segments.TryGetValue((from, to, occurrence), out var seg))
                    segments[(from, to, occurrence)] = seg = new Segment { Order = segments.Count };
                seg.GpuMs += (t[i] - t[i - 1]) * toMs;
                seg.CpuMs += (markCpu[k] - markCpu[k - 1]) * cpuToMs;
                seg.Draws += markDraws[k];
            }
            frameGpuMs += (t[n - 1] - t[0]) * toMs;
            frameCpuMs += (markCpu[s * MaxMarks + n - 1] - markCpu[s * MaxMarks]) * cpuToMs;
            framesMeasured++;
        }
    }

    // ---- report (main thread) ---------------------------------------------------------------------------------------

    static string Name(int id) => names.TryGetValue(id, out var n) ? n : $"#{id}";

    static string Report()
    {
        var sb = new StringBuilder();
        lock (sync)
        {
            long f = Math.Max(1, framesMeasured);
            double totalDraws = segments.Values.Sum(s => s.Draws) / (double)f;
            double seen = Math.Max(1, framesSeen);
            sb.AppendLine($"===== GPU timeline {label}: {framesMeasured} frames ({framesDropped} dropped), begin->end GPU {frameGpuMs / f:F2} ms, " +
                          $"render thread {frameCpuMs / f:F2} ms per frame | {startState} =====");
            if (countingDraws)
                sb.AppendLine($"  immediate context per frame: {totalDraws:F0} draws ({indirectCount / seen:F1} indirect, {instancesMeasured / seen:F0} instances in instanced draws), " +
                              $"{dispatchCount / seen:F1} dispatches, {executeCount / seen:F1} ExecuteCommandList");
            sb.AppendLine("     gpu ms   rt ms   draws  segment (from marker -> to marker, #n = repeat within the frame)");
            foreach (var kv in segments.OrderBy(kv => kv.Value.Order))
            {
                var s = kv.Value;
                if (s.GpuMs / f < 0.005 && s.CpuMs / f < 0.005 && s.Draws == 0) continue;
                string rep = kv.Key.occurrence > 0 ? $" #{kv.Key.occurrence + 1}" : "";
                sb.AppendLine($"    {s.GpuMs / f,7:F3} {s.CpuMs / f,7:F3} {s.Draws / (double)f,7:F0}  {Name(kv.Key.from)} -> {Name(kv.Key.to)}{rep}");
            }
        }
        return sb.ToString();
    }
}
