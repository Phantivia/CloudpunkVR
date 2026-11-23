using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;
using UnityEngine.Experimental.LowLevel;

namespace CloudpunkVR;

// Names code addresses for CpuSampler without debug symbols. Function bounds come from each module's unwind table
// (.pdata); names from IL2CPP metadata (game and Unity C# code), the engine's PlayerLoop phase functions, engine
// icalls (native halves of Unity C# methods) and DLL exports.
internal static unsafe class Symbols
{
    // Keys for code without unwind data: leaf functions (rounded) and code outside any module (CoreCLR JIT, stubs).
    public const ulong LeafFlag = 1UL << 62, NoModuleFlag = 1UL << 61;

    internal sealed class Image
    {
        public string Name;
        public ulong Base, End;
        public bool Os;
        bool loaded;
        uint[] begin = Array.Empty<uint>(), end, unwind;
        uint[] exportRva = Array.Empty<uint>();
        string[] exportName;
        readonly List<(uint lo, uint hi)> code = new();

        void Load()
        {
            if (loaded) return;
            loaded = true;
            byte* b = (byte*)Base;
            if (*(ushort*)b != 0x5A4D) return;
            byte* nt = b + *(int*)(b + 0x3C);
            if (*(uint*)nt != 0x4550) return;
            byte* opt = nt + 24;
            if (*(ushort*)opt != 0x20B) return;
            byte* sections = opt + *(ushort*)(nt + 20);
            for (int i = 0; i < *(ushort*)(nt + 6); i++)
            {
                byte* s = sections + i * 40;
                if ((*(uint*)(s + 36) & 0x20000000) != 0) code.Add((*(uint*)(s + 12), *(uint*)(s + 12) + *(uint*)(s + 8)));
            }
            uint expRva = *(uint*)(opt + 112), expSize = *(uint*)(opt + 116);
            uint excRva = *(uint*)(opt + 136), excSize = *(uint*)(opt + 140);
            int n = (int)(excSize / 12);
            begin = new uint[n]; end = new uint[n]; unwind = new uint[n];
            uint* rf = (uint*)(b + excRva);
            for (int i = 0; i < n; i++) { begin[i] = rf[3 * i]; end[i] = rf[3 * i + 1]; unwind[i] = rf[3 * i + 2]; }
            if (expRva == 0 || expSize == 0) return;
            byte* ed = b + expRva;
            uint names = *(uint*)(ed + 24);
            uint* funcs = (uint*)(b + *(uint*)(ed + 28));
            uint* nameRvas = (uint*)(b + *(uint*)(ed + 32));
            ushort* ords = (ushort*)(b + *(uint*)(ed + 36));
            var list = new List<(uint rva, string name)>();
            for (uint i = 0; i < names; i++)
            {
                uint f = funcs[ords[i]];
                if (f >= expRva && f < expRva + expSize) continue;   // forwarder
                list.Add((f, Marshal.PtrToStringAnsi((IntPtr)(b + nameRvas[i]))));
            }
            // Nt*/Zw* share entry points; keep the Nt spelling.
            var best = list.GroupBy(e => e.rva).Select(g => g.OrderBy(e => e.name.StartsWith("Zw") ? 1 : 0).First()).OrderBy(e => e.rva).ToArray();
            exportRva = best.Select(e => e.rva).ToArray();
            exportName = best.Select(e => e.name).ToArray();
        }

        public bool IsCode(ulong addr)
        {
            Load();
            uint rva = (uint)(addr - Base);
            foreach (var (lo, hi) in code) if (rva >= lo && rva < hi) return true;
            return false;
        }

        public ulong FunctionOf(ulong addr)
        {
            Load();
            byte* b = (byte*)Base;
            uint rva = (uint)(addr - Base);
            int idx = Floor(begin, rva);
            if (idx < 0 || rva >= end[idx])
            {
                int e = Floor(exportRva, rva);
                return (e >= 0 && rva - exportRva[e] < 0x400 ? Base + exportRva[e] : addr & ~0xFFUL) | LeafFlag;
            }
            uint fb = begin[idx], uw = unwind[idx];
            for (int guard = 0; guard < 16; guard++)
            {
                if ((uw & 1) != 0) { uint* p = (uint*)(b + (uw & ~1u)); fb = p[0]; uw = p[2]; continue; }
                byte* ui = b + uw;
                if (((ui[0] >> 3) & 4) == 0) break;                    // not UNW_FLAG_CHAININFO: primary entry
                uint* parent = (uint*)(ui + 4 + ((ui[2] + 1) & ~1) * 2);
                fb = parent[0]; uw = parent[2];
            }
            return Base + fb;
        }

        public bool Bounds(ulong addr, out ulong start, out ulong stop)
        {
            Load();
            uint rva = (uint)(addr - Base);
            int idx = Floor(begin, rva);
            start = stop = 0;
            if (idx < 0 || rva >= end[idx]) return false;
            start = Base + begin[idx];
            stop = Base + end[idx];
            return true;
        }

        public string ExportAt(ulong addr)
        {
            Load();
            int e = Floor(exportRva, (uint)(addr - Base));
            return e >= 0 && exportRva[e] == (uint)(addr - Base) ? exportName[e] : null;
        }

        static int Floor(uint[] sorted, uint value)
        {
            int lo = 0, hi = sorted.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (sorted[mid] <= value) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return found;
        }
    }

    static Image[] images = Array.Empty<Image>();
    static readonly Dictionary<ulong, string> methods = new(), phases = new(), icalls = new();
    static ulong[] methodStarts = Array.Empty<ulong>();
    static readonly HashSet<string> OsModules = new(StringComparer.OrdinalIgnoreCase) { "ntdll.dll", "KERNELBASE.dll", "kernel32.dll", "win32u.dll", "user32.dll" };
    static bool prepared;

    // Main thread only: reads IL2CPP metadata and the PlayerLoop.
    public static void Prepare()
    {
        Refresh();
        if (prepared) return;
        prepared = true;
        var sw = Stopwatch.StartNew();
        Try("IL2CPP methods", LoadMethods);
        methodStarts = methods.Keys.OrderBy(k => k).ToArray();
        Try("PlayerLoop phases", LoadPhases);
        Try("icalls", LoadIcalls);
        Try("engine symbols", LoadEngineSymbols);
        Plugin.L.LogInfo($"symbols: {methods.Count} IL2CPP methods, {phases.Count} PlayerLoop phase functions, {icalls.Count} icalls ({sw.ElapsedMilliseconds} ms)");
    }

    static void Try(string what, Action load)
    {
        try { load(); }
        catch (Exception e) { Plugin.L.LogWarning($"symbols: {what} unavailable: {e.Message}"); }
    }

    public static void Refresh()
    {
        var known = images.ToDictionary(i => i.Base);
        images = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m =>
        {
            ulong b = (ulong)(long)m.BaseAddress;
            return known.TryGetValue(b, out var old) ? old : new Image
            {
                Name = m.ModuleName, Base = b, End = b + (ulong)m.ModuleMemorySize, Os = OsModules.Contains(m.ModuleName),
            };
        }).OrderBy(i => i.Base).ToArray();
    }

    public static Image ImageOf(ulong addr)
    {
        int lo = 0, hi = images.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (images[mid].Base <= addr) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found >= 0 && addr < images[found].End ? images[found] : null;
    }

    public static ulong FunctionOf(ulong addr, out Image img)
    {
        img = ImageOf(addr);
        if (img == null) return (addr & ~0xFFFUL) | NoModuleFlag;
        ulong fn = img.FunctionOf(addr);
        if ((fn & LeafFlag) != 0 && addr >= engineBase && addr < engineEnd && EngineStart(addr) is ulong start and not 0) return start;
        if ((fn & LeafFlag) == 0 || methodStarts.Length == 0) return fn;
        // Small IL2CPP methods (getters and the like) are leaf functions without unwind data.
        int i = Array.BinarySearch(methodStarts, addr);
        if (i < 0) i = ~i - 1;
        if (i >= 0 && addr - methodStarts[i] < 0x200 && (img.FunctionOf(methodStarts[i]) & LeafFlag) != 0) return methodStarts[i];
        return fn;
    }

    public static bool IsMethod(ulong fn) => methods.ContainsKey(fn);
    public static bool PhaseOf(ulong fn, out string phase) => phases.TryGetValue(fn, out phase);

    public static string Name(ulong fn)
    {
        if ((fn & NoModuleFlag) != 0) return "(code outside modules: CoreCLR JIT, hook stubs)";
        bool leaf = (fn & LeafFlag) != 0;
        ulong a = fn & ~LeafFlag;
        if (phases.TryGetValue(a, out var p)) return "[phase] " + p;
        if (methods.TryGetValue(a, out var m)) return m;
        if (icalls.TryGetValue(a, out var ic)) return "[icall] " + ic;
        var engine = EngineName(a);
        if (engine != null) return engine;
        var img = ImageOf(a);
        if (img == null) return $"0x{a:X}";
        var exp = img.ExportAt(a);
        if (exp != null) return $"{img.Name}!{exp}";
        return $"{img.Name}!{(leaf ? "leaf" : "fn")}_{a - img.Base:X}";
    }

    static void LoadMethods()
    {
        uint count = 0;
        var assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref count);
        for (uint a = 0; a < count; a++)
        {
            var image = IL2CPP.il2cpp_assembly_get_image(assemblies[a]);
            uint classes = IL2CPP.il2cpp_image_get_class_count(image);
            for (uint c = 0; c < classes; c++)
            {
                var klass = IL2CPP.il2cpp_image_get_class(image, c);
                string type = ClassName(klass);
                IntPtr iter = IntPtr.Zero, method;
                while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    ulong ptr = (ulong)(long)*(IntPtr*)method;   // MethodInfo.methodPointer is the first field
                    if (ptr != 0) methods.TryAdd(ptr, type + "::" + Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method)));
                }
            }
        }
    }

    static string ClassName(IntPtr klass)
    {
        string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
        var outer = IL2CPP.il2cpp_class_get_declaring_type(klass);
        if (outer != IntPtr.Zero) return ClassName(outer) + "/" + name;
        string ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    // The engine's default PlayerLoop, as the flat struct array its icall returns (the managed wrapper that turns it
    // into a tree is stripped from this build). Each entry carries a pointer to the engine function it runs, or to
    // the slot holding it. The IL2CPP memory is read directly: struct arrays are not wrapped reliably.
    static void LoadPhases()
    {
        var getLoop = (delegate* unmanaged<IntPtr>)IL2CPP.il2cpp_resolve_icall("UnityEngine.Experimental.LowLevel.PlayerLoop::GetDefaultPlayerLoopInternal");
        if (getLoop == null) { Plugin.L.LogWarning("symbols: PlayerLoop icall not found"); return; }
        var klass = Il2CppClassPointerStore<PlayerLoopSystemInternal>.NativeClassPtr;
        int Off(string field) => (int)IL2CPP.il2cpp_field_get_offset(IL2CPP.il2cpp_class_get_field_from_name(klass, field)) - 16;
        uint align = 0;
        int size = IL2CPP.il2cpp_class_value_size(klass, ref align);
        int oType = Off("type"), oFn = Off("updateFunction");
        IntPtr array = getLoop();
        uint n = array == IntPtr.Zero ? 0 : IL2CPP.il2cpp_array_length(array);
        for (uint i = 0; i < n; i++)
        {
            byte* sys = (byte*)array + 32 + i * size;
            var typeObj = *(IntPtr*)(sys + oType);
            string name = typeObj == IntPtr.Zero ? "?" : ClassName(IL2CPP.il2cpp_class_from_system_type(typeObj))
                .Replace("UnityEngine.Experimental.PlayerLoop.", "").Replace('/', '.');
            AddPhase(*(ulong*)(sys + oFn), name);
        }
        AddUnambiguousJumpTargets();
    }

    static void AddPhase(ulong f, string name)
    {
        if (f == 0) return;
        var img = ImageOf(f);
        if (img == null) return;
        if (!img.IsCode(f)) { f = *(ulong*)f; img = ImageOf(f); }
        if (img == null || !img.IsCode(f)) return;
        phases[img.FunctionOf(f) & ~LeafFlag] = name;
        // Phase functions are often thin wrappers ending in a tail call, so their frame never shows up on a stack:
        // map every jump out of a small wrapper that lands exactly on a function start as the same phase.
        ulong start = f, stop = f + 16;
        if (img.Bounds(f, out ulong b, out ulong e)) { start = b; stop = e; }
        if (stop - start > 0x400) return;
        for (ulong p = start; p + 5 <= stop; p++)
        {
            ulong t = 0;
            if (*(byte*)p == 0xE9) t = p + 5 + (ulong)(long)*(int*)(p + 1);
            else if (*(byte*)p == 0xFF && *(byte*)(p + 1) == 0x25 && p + 6 <= stop)
            {
                ulong slot = p + 6 + (ulong)(long)*(int*)(p + 2);
                if (ImageOf(slot) != null) t = *(ulong*)slot;
            }
            if (t == 0 || (t >= start && t < stop)) continue;
            var ti = ImageOf(t);
            if (ti == null || !ti.IsCode(t) || ti.FunctionOf(t) != t) continue;
            if (!jumpTargets.TryGetValue(t, out var claims)) jumpTargets[t] = claims = new HashSet<string>();
            claims.Add(name);
        }
    }

    // Jump targets shared by several phases are common helpers, not phases.
    static readonly Dictionary<ulong, HashSet<string>> jumpTargets = new();
    static void AddUnambiguousJumpTargets()
    {
        foreach (var (target, claims) in jumpTargets)
            if (claims.Count == 1) phases.TryAdd(target, claims.First());
    }

    // Engine function names from Unity's public symbols (dev only): CLOUDPUNKVR_SYMBOLS names the folder holding
    // UnityPlayer_Win64_il2cpp_x64.pdb for this exact engine build.
    [DllImport("dbghelp", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SymInitializeW(IntPtr process, string searchPath, bool invade);
    [DllImport("dbghelp", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ulong SymLoadModuleExW(IntPtr process, IntPtr file, string imageName, string moduleName, ulong baseOfDll, uint dllSize, IntPtr data, uint flags);
    [DllImport("dbghelp", SetLastError = true)] static extern bool SymFromAddrW(IntPtr process, ulong address, out ulong displacement, IntPtr symbol);
    [DllImport("dbghelp")] static extern uint SymSetOptions(uint options);

    static readonly IntPtr SymSession = (IntPtr)0x43505652;   // any unique value; no debugging of a real process
    static ulong engineBase, engineEnd;
    static IntPtr symBuffer;
    static readonly Dictionary<ulong, string> engineNames = new();

    static void LoadEngineSymbols()
    {
        string dir = Environment.GetEnvironmentVariable("CLOUDPUNKVR_SYMBOLS");
        if (string.IsNullOrEmpty(dir)) return;
        var mod = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .FirstOrDefault(m => m.ModuleName.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase));
        if (mod == null) return;
        SymSetOptions(0x2 | 0x200 | 0x400 | 0x80000);   // UNDNAME, FAIL_CRITICAL_ERRORS, EXACT_SYMBOLS, NO_PROMPTS
        if (!SymInitializeW(SymSession, dir, false)) throw new Exception("SymInitialize error " + Marshal.GetLastWin32Error());
        ulong b = (ulong)(long)mod.BaseAddress;
        if (SymLoadModuleExW(SymSession, IntPtr.Zero, mod.FileName, null, b, (uint)mod.ModuleMemorySize, IntPtr.Zero, 0) == 0)
            throw new Exception("SymLoadModuleEx error " + Marshal.GetLastWin32Error());
        symBuffer = Marshal.AllocHGlobal(88 + 2 * 1024);
        engineBase = b;
        engineEnd = b + (ulong)mod.ModuleMemorySize;
        Plugin.L.LogInfo("symbols: engine names from " + dir);
    }

    static string EngineName(ulong addr)
    {
        if (symBuffer == IntPtr.Zero || addr < engineBase || addr >= engineEnd) return null;
        if (engineNames.TryGetValue(addr, out var name)) return name;
        byte* s = (byte*)symBuffer;
        if (Lookup(addr, out ulong displacement))
            name = Marshal.PtrToStringUni((IntPtr)(s + 84), (int)*(uint*)(s + 76)) + (displacement != 0 ? $"+0x{displacement:X}" : "");
        engineNames[addr] = name;
        return name;
    }

    // Start of the public symbol containing addr (leaf functions have no unwind data to give it).
    static readonly Dictionary<ulong, ulong> engineStarts = new();
    static ulong EngineStart(ulong addr)
    {
        if (symBuffer == IntPtr.Zero) return 0;
        if (engineStarts.TryGetValue(addr, out ulong start)) return start;
        start = Lookup(addr, out _) ? *(ulong*)((byte*)symBuffer + 56) : 0;   // SYMBOL_INFOW.Address
        engineStarts[addr] = start;
        return start;
    }

    static bool Lookup(ulong addr, out ulong displacement)
    {
        byte* s = (byte*)symBuffer;
        new Span<byte>(s, 88 + 2 * 1024).Clear();
        *(uint*)s = 88;                 // SYMBOL_INFOW.SizeOfStruct
        *(uint*)(s + 80) = 1024;        // MaxNameLen
        return SymFromAddrW(SymSession, addr, out displacement, symBuffer);
    }

    // icall names are registered by UnityPlayer.dll as plain strings; resolve each one through IL2CPP.
    static void LoadIcalls()
    {
        var player = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .FirstOrDefault(m => m.ModuleName.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase));
        if (player == null) return;
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(player.FileName));
        foreach (var name in Regex.Matches(text, @"UnityEngine(?:\.[A-Za-z0-9_]+)+::[A-Za-z0-9_]+").Select(m => m.Value).Distinct())
        {
            ulong f = (ulong)(long)IL2CPP.il2cpp_resolve_icall(name);
            var img = f != 0 ? ImageOf(f) : null;
            if (img != null && img.IsCode(f)) icalls.TryAdd(img.FunctionOf(f) & ~LeafFlag, name);
        }
    }
}
