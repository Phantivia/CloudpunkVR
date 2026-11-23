using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime;

namespace CloudpunkVR;

// Dev: native addresses of the game's compiled methods and the offsets of its fields, so GameAssembly.dll can be read
// with a disassembler (the interop assemblies only carry signatures). Lines are "rva<TAB>Type::Method" and
// "field<TAB>Type::name<TAB>0xoffset".
internal static class NativeMap
{
    static long GameAssemblyBase()
    {
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            if (string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                return (long)m.BaseAddress;
        return 0;
    }

    public static unsafe void Dump(string outPath, string assemblyFilter)
    {
        long baseAddr = GameAssemblyBase();
        var sb = new StringBuilder();
        int methods = 0, fields = 0;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var an = asm.GetName().Name;
            if (!System.Text.RegularExpressions.Regex.IsMatch(an, assemblyFilter)) continue;
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            foreach (var t in types)
            {
                FieldInfo[] statics;
                try { statics = t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (var f in statics)
                {
                    if (f.FieldType != typeof(IntPtr)) continue;
                    try
                    {
                        if (f.Name.StartsWith("NativeMethodInfoPtr_"))
                        {
                            var mi = (IntPtr)f.GetValue(null);
                            if (mi == IntPtr.Zero) continue;
                            long code = (long)*(IntPtr*)mi;
                            if (code == 0) continue;
                            sb.Append((code - baseAddr).ToString("x")).Append('\t').Append(t.FullName).Append("::")
                              .Append(f.Name.Substring("NativeMethodInfoPtr_".Length)).Append('\n');
                            methods++;
                        }
                        else if (f.Name.StartsWith("NativeFieldInfoPtr_"))
                        {
                            var fi = (IntPtr)f.GetValue(null);
                            if (fi == IntPtr.Zero) continue;
                            uint off = IL2CPP.il2cpp_field_get_offset(fi);
                            sb.Append("field\t").Append(t.FullName).Append("::").Append(f.Name.Substring("NativeFieldInfoPtr_".Length))
                              .Append("\t0x").Append(off.ToString("x")).Append('\n');
                            fields++;
                        }
                    }
                    catch { }
                }
            }
        }
        File.WriteAllText(outPath, sb.ToString());
        Plugin.L.LogInfo($"nativemap: {methods} methods, {fields} fields from /{assemblyFilter}/ -> {outPath} (GameAssembly base 0x{baseAddr:x})");
    }
}
