// Lists types and members of Il2CppInterop assemblies (BepInEx/interop) for reverse-engineering.
// usage: AsmDump <assembly.dll> <typeRegex> [memberRegex]
//        AsmDump <assembly.dll> --find <memberRegex>   (search member names across all types)
using System;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Cecil;

static class AsmDump
{
    static bool Noise(string name) =>
        name.StartsWith("NativeFieldInfoPtr_") || name.StartsWith("NativeMethodInfoPtr_") ||
        name.StartsWith("NativeMethodInfo") || name == ".cctor" || name.StartsWith("Il2Cpp");

    static string Sig(MethodDefinition m) =>
        $"{(m.IsStatic ? "static " : "")}{(m.IsVirtual ? "virtual " : "")}{m.ReturnType.Name} {m.Name}(" +
        string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name)) + ")";

    static void DumpType(TypeDefinition t, Regex member)
    {
        Console.WriteLine($"== {t.FullName} : {t.BaseType?.FullName}");
        if (t.IsEnum)
        {
            foreach (var f in t.Fields.Where(f => f.IsStatic && f.HasConstant))
                Console.WriteLine($"   {f.Name} = {f.Constant}");
            return;
        }
        foreach (var p in t.Properties.Where(p => !Noise(p.Name) && (member == null || member.IsMatch(p.Name))))
            Console.WriteLine($"   prop {p.PropertyType.Name} {p.Name}");
        foreach (var f in t.Fields.Where(f => !Noise(f.Name) && (member == null || member.IsMatch(f.Name))))
            Console.WriteLine($"   field {(f.IsStatic ? "static " : "")}{f.FieldType.Name} {f.Name}");
        foreach (var m in t.Methods.Where(m => !m.IsGetter && !m.IsSetter && !Noise(m.Name) && (member == null || member.IsMatch(m.Name))))
            Console.WriteLine($"   {Sig(m)}");
    }

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: AsmDump <dll> <typeRegex> [memberRegex] | <dll> --find <memberRegex>"); return 1; }
        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        var types = asm.MainModule.GetTypes().ToList();
        if (args[1] == "--find")
        {
            var rx = new Regex(args[2], RegexOptions.IgnoreCase);
            foreach (var t in types)
            {
                var hits = t.Methods.Where(m => !Noise(m.Name) && rx.IsMatch(m.Name)).Select(Sig)
                    .Concat(t.Properties.Where(p => !Noise(p.Name) && rx.IsMatch(p.Name)).Select(p => "prop " + p.PropertyType.Name + " " + p.Name))
                    .Concat(t.Fields.Where(f => !Noise(f.Name) && rx.IsMatch(f.Name)).Select(f => "field " + f.FieldType.Name + " " + f.Name)).ToList();
                if (hits.Count == 0) continue;
                Console.WriteLine($"== {t.FullName}");
                foreach (var h in hits) Console.WriteLine("   " + h);
            }
            return 0;
        }
        var typeRx = new Regex(args[1], RegexOptions.IgnoreCase);
        var memberRx = args.Length > 2 ? new Regex(args[2], RegexOptions.IgnoreCase) : null;
        foreach (var t in types.Where(t => typeRx.IsMatch(t.FullName)))
            DumpType(t, memberRx);
        return 0;
    }
}
