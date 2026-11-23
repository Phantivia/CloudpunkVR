using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CloudpunkVR;

// Dev-only sampling profiler that needs no admin rights. A background thread suspends the Unity main thread and the
// busiest other threads about once per millisecond, reads their registers, copies the stack of the first few, resumes
// them and then unwinds the copy. Between suspend and resume nothing allocates or takes a lock, and a no-GC region
// covers the session, so a CoreCLR collection never waits on a thread held suspended here.
internal static unsafe class CpuSampler
{
    [DllImport("kernel32"), SuppressGCTransition] static extern uint SuspendThread(IntPtr thread);
    [DllImport("kernel32"), SuppressGCTransition] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32"), SuppressGCTransition] static extern int GetThreadContext(IntPtr thread, IntPtr context);
    [DllImport("ntdll", EntryPoint = "memcpy"), SuppressGCTransition] static extern void* Memcpy(void* dst, void* src, UIntPtr count);
    [DllImport("kernel32")] static extern IntPtr RtlLookupFunctionEntry(ulong pc, out ulong imageBase, IntPtr history);
    [DllImport("kernel32")] static extern IntPtr RtlVirtualUnwind(uint type, ulong imageBase, ulong pc, IntPtr function, IntPtr context,
                                                                  out IntPtr handlerData, out ulong establisherFrame, IntPtr contextPointers);
    [DllImport("kernel32")] static extern IntPtr OpenThread(uint access, int inherit, uint id);
    [DllImport("kernel32")] static extern int CloseHandle(IntPtr handle);
    [DllImport("kernel32")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32")] static extern int GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32")] static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
    [DllImport("kernel32")] static extern IntPtr LocalFree(IntPtr mem);
    [DllImport("ntdll")] static extern int NtQueryInformationThread(IntPtr thread, int infoClass, void* info, int length, IntPtr returnLength);
    [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm")] static extern uint timeEndPeriod(uint ms);

    const uint ThreadAccess = 0x0002 | 0x0008 | 0x0040;   // suspend/resume, get context, query information
    const uint ContextControl = 0x00100001, ContextControlInteger = 0x00100003;
    const int ContextSize = 1232, FlagsAt = 0x30, RegsAt = 0x78, RspAt = 0x98, RipAt = 0xF8;
    const int MaxFrames = 48, MaxCopy = 512 * 1024, CopySlack = 64 * 1024;

    public static uint MainThreadId;
    public static volatile bool Running, Sampling;
    public static volatile string PendingReport;
    public static int Frames;
    static string startState, threadSummary = "";

    public static void CaptureMainThread() { if (MainThreadId == 0) MainThreadId = GetCurrentThreadId(); }

    // Main thread, once per frame from the dev channel.
    public static void Tick() { if (Sampling) Frames++; }

    // Main thread: IL2CPP metadata is read here (the sampler thread is not attached to IL2CPP), then sampling starts.
    public static void Start(float seconds, string label, int threads, int deep)
    {
        if (Running) { Plugin.L.LogWarning("prof: already running"); return; }
        Symbols.Prepare();
        startState = Diag.StateLine();
        Running = true;
        new Thread(() => Run(seconds, label, threads, deep)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "CloudpunkVR sampler" }.Start();
    }

    sealed class Target
    {
        public uint Id;
        public IntPtr Handle;
        public ulong StackBase;
        public bool Deep;
        public ulong[] Frames;
        public byte[] Depth;
        public int Count;
        public long CpuStart, CpuEnd;
        public string Name;
    }

    static long CpuTime(IntPtr h) => GetThreadTimes(h, out _, out _, out long k, out long u) != 0 ? k + u : 0;

    static void Run(float seconds, string label, int threadCount, int deepCount)
    {
        var handles = new Dictionary<uint, IntPtr>();
        bool noGc = false;
        IntPtr ctxRaw = Marshal.AllocHGlobal(ContextSize + 16), uctxRaw = Marshal.AllocHGlobal(ContextSize + 16);
        IntPtr copyRaw = Marshal.AllocHGlobal(MaxCopy + CopySlack);
        try
        {
            try { noGc = GC.TryStartNoGCRegion(128L << 20); }
            catch (Exception e) { Plugin.L.LogWarning("prof: no-GC region unavailable: " + e.Message); }
            uint self = GetCurrentThreadId();
            foreach (ProcessThread pt in Process.GetCurrentProcess().Threads)
            {
                if ((uint)pt.Id == self) continue;
                var h = OpenThread(ThreadAccess, 0, (uint)pt.Id);
                if (h != IntPtr.Zero) handles[(uint)pt.Id] = h;
            }
            var starts = new Dictionary<string, int>();
            foreach (var h in handles.Values)
            {
                ulong start = 0;
                NtQueryInformationThread(h, 9, &start, 8, IntPtr.Zero);   // ThreadQuerySetWin32StartAddress
                Bump(starts, start == 0 ? "?" : Symbols.Name(Symbols.FunctionOf(start, out _)));
            }
            threadSummary = $"{handles.Count + 1} threads by start function: " +
                            string.Join(", ", starts.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} x{kv.Value}"));
            var before = handles.ToDictionary(kv => kv.Key, kv => CpuTime(kv.Value));
            Thread.Sleep(1000);
            var order = handles.Keys.OrderByDescending(id => CpuTime(handles[id]) - before[id]).ToList();
            if (MainThreadId != 0 && handles.ContainsKey(MainThreadId)) { order.Remove(MainThreadId); order.Insert(0, MainThreadId); }

            int cap = (int)(seconds * 1100) + 64;
            var targets = order.Take(threadCount).Select((id, i) => new Target { Id = id, Handle = handles[id], Deep = i < deepCount }).ToArray();
            foreach (var t in targets)
            {
                if (t.Deep) t.StackBase = StackBase(t.Handle);
                t.Deep &= t.StackBase != 0;
                t.Frames = new ulong[cap * (t.Deep ? MaxFrames : 1)];
                t.Depth = new byte[cap];
                t.Name = ThreadName(t);
            }

            byte* ctx = (byte*)(((long)ctxRaw + 15) & ~15L), uctx = (byte*)(((long)uctxRaw + 15) & ~15L), copy = (byte*)copyRaw;
            new Span<byte>(copy, MaxCopy + CopySlack).Clear();
            foreach (var t in targets) t.CpuStart = CpuTime(t.Handle);
            timeBeginPeriod(1);
            Frames = 0;
            Sampling = true;
            var sw = Stopwatch.StartNew();
            long endMs = (long)(seconds * 1000);
            while (sw.ElapsedMilliseconds < endMs)
            {
                foreach (var t in targets)
                {
                    if (t.Count >= cap) continue;
                    if (SuspendThread(t.Handle) == uint.MaxValue) continue;
                    *(uint*)(ctx + FlagsAt) = t.Deep ? ContextControlInteger : ContextControl;
                    bool ok = GetThreadContext(t.Handle, (IntPtr)ctx) != 0;
                    ulong rsp = *(ulong*)(ctx + RspAt), copied = 0;
                    if (ok && t.Deep && rsp < t.StackBase && t.StackBase - rsp < (64UL << 20))
                    {
                        copied = Math.Min(t.StackBase - rsp, MaxCopy);
                        Memcpy(copy, (void*)rsp, (UIntPtr)copied);
                    }
                    ResumeThread(t.Handle);
                    if (!ok) continue;
                    fixed (ulong* f = &t.Frames[t.Count * (t.Deep ? MaxFrames : 1)])
                    {
                        if (copied > 0) t.Depth[t.Count] = (byte)Unwind(ctx, uctx, rsp, copied, copy, f);
                        else { f[0] = *(ulong*)(ctx + RipAt); t.Depth[t.Count] = 1; }
                    }
                    t.Count++;
                }
                Thread.Sleep(1);
            }
            Sampling = false;
            timeEndPeriod(1);
            double wall = sw.Elapsed.TotalSeconds;
            foreach (var t in targets) t.CpuEnd = CpuTime(t.Handle);
            Report(label, wall, targets, Frames);
        }
        catch (Exception e) { Plugin.L.LogError("prof: " + e); }
        finally
        {
            Sampling = false;
            if (noGc) { try { GC.EndNoGCRegion(); } catch (InvalidOperationException) { } }
            foreach (var h in handles.Values) CloseHandle(h);
            Marshal.FreeHGlobal(ctxRaw);
            Marshal.FreeHGlobal(uctxRaw);
            Marshal.FreeHGlobal(copyRaw);
            Running = false;
        }
    }

    // Unwinds a copied stack: registers that point into the original stack range are moved into the copy before each
    // step, so RtlVirtualUnwind reads the snapshot and never the live stack.
    static int Unwind(byte* ctx, byte* uctx, ulong rsp, ulong copied, byte* copy, ulong* frames)
    {
        Memcpy(uctx, ctx, (UIntPtr)ContextSize);
        ulong lo = rsp, hi = rsp + copied, delta = (ulong)copy - rsp;
        ulong bufLo = (ulong)copy, bufHi = bufLo + copied;
        Rebase(uctx, lo, hi, delta);
        ulong pc = *(ulong*)(uctx + RipAt);
        int k = 0;
        frames[k++] = pc;
        while (k < MaxFrames)
        {
            ulong sp = *(ulong*)(uctx + RspAt);
            if (sp < bufLo || sp >= bufHi || (sp & 7) != 0) break;
            IntPtr fe = RtlLookupFunctionEntry(k == 1 ? pc : pc - 1, out ulong imageBase, IntPtr.Zero);
            if (fe == IntPtr.Zero)
            {
                if (k > 1) break;                       // only the interrupted frame may be a leaf function
                pc = *(ulong*)sp;
                *(ulong*)(uctx + RspAt) = sp + 8;
                *(ulong*)(uctx + RipAt) = pc;
            }
            else
            {
                uint unwindRva = ((uint*)fe)[2];
                if ((unwindRva & 1) == 0)
                {
                    int frameReg = ((byte*)(imageBase + unwindRva))[3] & 0xF;
                    ulong fr = *(ulong*)(uctx + RegsAt + 8 * frameReg);
                    if (frameReg != 0 && (fr < bufLo || fr > bufHi)) break;
                }
                RtlVirtualUnwind(0, imageBase, pc, fe, (IntPtr)uctx, out _, out _, IntPtr.Zero);
                pc = *(ulong*)(uctx + RipAt);
            }
            ulong nsp = *(ulong*)(uctx + RspAt);
            if (pc < 0x10000 || nsp <= sp || nsp > bufHi) break;
            Rebase(uctx, lo, hi, delta);
            frames[k++] = pc;
        }
        return k;
    }

    static void Rebase(byte* uctx, ulong lo, ulong hi, ulong delta)
    {
        for (int r = 0; r < 16; r++)
        {
            ulong* reg = (ulong*)(uctx + RegsAt + 8 * r);
            if (*reg >= lo && *reg < hi) *reg += delta;
        }
    }

    static ulong StackBase(IntPtr thread)
    {
        byte* tbi = stackalloc byte[48];   // THREAD_BASIC_INFORMATION; TebBaseAddress at 8, NT_TIB.StackBase at TEB+8
        if (NtQueryInformationThread(thread, 0, tbi, 48, IntPtr.Zero) != 0) return 0;
        ulong teb = *(ulong*)(tbi + 8);
        return teb == 0 ? 0 : *(ulong*)(teb + 8);
    }

    static string ThreadName(Target t)
    {
        if (t.Id == MainThreadId) return "MAIN";
        if (GetThreadDescription(t.Handle, out var p) >= 0 && p != IntPtr.Zero)
        {
            var s = Marshal.PtrToStringUni(p);
            LocalFree(p);
            if (!string.IsNullOrEmpty(s)) return $"\"{s}\"";
        }
        ulong start = 0;
        if (NtQueryInformationThread(t.Handle, 9, &start, 8, IntPtr.Zero) == 0 && start != 0)   // ThreadQuerySetWin32StartAddress
            return "start=" + Symbols.Name(Symbols.FunctionOf(start, out _));
        return "(unnamed)";
    }

    // ---- report ----------------------------------------------------------------------------------------------------

    static void Bump<T>(Dictionary<T, int> d, T key) => d[key] = d.TryGetValue(key, out int v) ? v + 1 : 1;
    static string Pct(int part, int total) => $"{100.0 * part / Math.Max(1, total),5:F1}%";

    static void Section(StringBuilder sb, string title, Dictionary<ulong, int> counts, int total, int top)
    {
        sb.AppendLine($"  {title}:");
        foreach (var kv in counts.OrderByDescending(kv => kv.Value).Take(top))
            if (kv.Value * 1000 >= total) sb.AppendLine($"    {Pct(kv.Value, total)}  {(kv.Key == 0 ? "(no IL2CPP frame: engine only)" : Symbols.Name(kv.Key))}");
    }

    sealed class Node
    {
        public int Count;
        public Dictionary<ulong, Node> Kids;
    }

    // Top-down call tree. Chains where one child carries (nearly) all of its parent's samples print on one line.
    static void Tree(StringBuilder sb, Node node, ulong fn, int depth, int total, int minCount, ref int lines)
    {
        var chain = new List<ulong> { fn };
        while (node.Kids is { Count: > 0 } && node.Kids.Values.Max(k => k.Count) * 100 >= node.Count * 97)
        {
            var only = node.Kids.OrderByDescending(k => k.Value.Count).First();
            if (only.Value.Count < minCount) break;
            chain.Add(only.Key);
            node = only.Value;
        }
        if (fn != 0 && lines++ < 400)
            sb.AppendLine($"    {new string(' ', Math.Min(depth, 30) * 2)}{Pct(node.Count, total)}  {string.Join("  >  ", chain.Select(Symbols.Name))}");
        if (node.Kids == null) return;
        foreach (var kid in node.Kids.OrderByDescending(k => k.Value.Count))
            if (kid.Value.Count >= minCount) Tree(sb, kid.Value, kid.Key, depth + 1, total, minCount, ref lines);
    }

    static void Report(string label, double wall, Target[] targets, int frames)
    {
        Symbols.Refresh();
        double frameMs = frames > 0 ? wall * 1000 / frames : 0;
        var sb = new StringBuilder();
        sb.AppendLine($"===== CPU profile {label}: {wall:F1} s, {frames} frames ({frameMs:F2} ms/frame) | {startState} =====");
        sb.AppendLine("  " + threadSummary);
        var seen = new HashSet<ulong>();
        foreach (var t in targets)
        {
            if (t.Count == 0) continue;
            int stride = t.Deep ? MaxFrames : 1, os = 0;
            long depthSum = 0;
            var self = new Dictionary<ulong, int>();
            var incl = new Dictionary<ulong, int>();
            var inner = new Dictionary<ulong, int>();
            var waitBy = new Dictionary<ulong, int>();
            var mods = new Dictionary<string, int>();
            var phase = new Dictionary<string, int>();
            var entry = new Dictionary<string, Dictionary<ulong, int>>();
            var root = new Node();
            var callers = new Dictionary<ulong, Dictionary<string, int>>();
            var fns = new ulong[MaxFrames];
            for (int s = 0; s < t.Count; s++)
            {
                int d = t.Depth[s];
                depthSum += d;
                ulong f0 = Symbols.FunctionOf(t.Frames[s * stride], out var img0);
                Bump(self, f0);
                Bump(mods, img0?.Name ?? "(outside modules)");
                bool inOs = img0 != null && img0.Os;
                if (inOs) os++;
                if (!t.Deep) continue;
                seen.Clear();
                string ph = null;
                ulong outer = 0, innermost = 0, caller = 0;
                for (int k = 0; k < d; k++)
                {
                    ulong pc = t.Frames[s * stride + k];
                    ulong fn = Symbols.FunctionOf(k == 0 ? pc : pc - 1, out var img);
                    fns[k] = fn;
                    if (seen.Add(fn)) Bump(incl, fn);
                    if (Symbols.PhaseOf(fn, out var p)) ph = p;
                    if (Symbols.IsMethod(fn)) { if (innermost == 0) innermost = fn; outer = fn; }
                    if (inOs && caller == 0 && (img == null || !img.Os)) caller = fn;
                }
                var node = root;
                node.Count++;
                for (int k = d - 1; k >= 0; k--)
                {
                    node.Kids ??= new Dictionary<ulong, Node>();
                    if (!node.Kids.TryGetValue(fns[k], out var next)) node.Kids[fns[k]] = next = new Node();
                    next.Count++;
                    node = next;
                }
                if (d > 1)
                {
                    if (!callers.TryGetValue(fns[0], out var paths)) callers[fns[0]] = paths = new Dictionary<string, int>();
                    Bump(paths, string.Join(" < ", Enumerable.Range(1, Math.Min(4, d - 1)).Select(k => Symbols.Name(fns[k]))));
                }
                if (t.Id == MainThreadId)
                {
                    ph ??= "(no phase on stack)";
                    Bump(phase, ph);
                    if (!entry.TryGetValue(ph, out var e)) entry[ph] = e = new Dictionary<ulong, int>();
                    Bump(e, outer);
                }
                Bump(inner, innermost);
                if (caller != 0) Bump(waitBy, caller);
            }
            double busy = (t.CpuEnd - t.CpuStart) / 1e7 / wall * 100;
            sb.AppendLine();
            sb.AppendLine($"--- thread {t.Id} {t.Name}: {busy:F0}% of a core busy ({(t.CpuEnd - t.CpuStart) / 1e4 / Math.Max(1, frames):F2} ms/frame), {t.Count} samples" +
                          $"{(t.Deep ? $", stacks avg {depthSum / (double)t.Count:F1} frames" : "")}, {Pct(os, t.Count).Trim()} in OS code");
            sb.AppendLine("  modules (self):");
            foreach (var kv in mods.OrderByDescending(kv => kv.Value).Take(8))
                sb.AppendLine($"    {Pct(kv.Value, t.Count)}  {kv.Key}");
            Section(sb, "self", self, t.Count, t.Deep ? 30 : 12);
            if (!t.Deep) continue;
            if (phase.Count > 0)
            {
                sb.AppendLine("  PlayerLoop phases (share of samples, ms per frame; below each: outermost IL2CPP method):");
                foreach (var kv in phase.OrderByDescending(kv => kv.Value))
                {
                    if (kv.Value * 500 < t.Count) continue;
                    sb.AppendLine($"    {Pct(kv.Value, t.Count)} {kv.Value * frameMs / t.Count,6:F2} ms  {kv.Key}");
                    foreach (var e in entry[kv.Key].OrderByDescending(x => x.Value).Take(10))
                        if (e.Value * 500 >= t.Count)
                            sb.AppendLine($"           {Pct(e.Value, t.Count)}  {(e.Key == 0 ? "(no IL2CPP frame: engine only)" : Symbols.Name(e.Key))}");
                }
            }
            Section(sb, "inclusive", incl, t.Count, 45);
            Section(sb, "innermost IL2CPP method (script code that is running or called into the engine)", inner, t.Count, 30);
            if (waitBy.Count > 0) Section(sb, "first non-OS caller of OS code (who waits)", waitBy, t.Count, 15);
            sb.AppendLine("  callers of the hottest functions (up to 4 frames):");
            foreach (var kv in self.OrderByDescending(kv => kv.Value).Take(t.Id == MainThreadId ? 14 : 6))
            {
                if (kv.Value * 200 < t.Count || !callers.TryGetValue(kv.Key, out var paths)) continue;
                sb.AppendLine($"    {Pct(kv.Value, t.Count)}  {Symbols.Name(kv.Key)}");
                foreach (var path in paths.OrderByDescending(p => p.Value).Take(5))
                    sb.AppendLine($"           {Pct(path.Value, t.Count)}  < {path.Key}");
            }
            sb.AppendLine("  call tree (top-down):");
            int lines = 0;
            Tree(sb, root, 0, -1, t.Count, Math.Max(1, t.Count / (t.Id == MainThreadId ? 250 : 100)), ref lines);
        }
        PendingReport = sb.ToString();
    }
}
