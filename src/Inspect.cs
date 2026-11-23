using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace CloudpunkVR;

// Dev-only inspectors for reverse-engineering components and materials without the game's source.
internal static class Inspect
{
    static Dictionary<string, Type> proxies;

    // The interop proxy class for an IL2CPP type name ("Ns.Outer/Inner" -> "Ns.Outer+Inner").
    internal static Type Proxy(Il2CppSystem.Type t)
    {
        if (proxies == null)
        {
            proxies = new Dictionary<string, Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                foreach (var x in types)
                    if (x.FullName != null && typeof(Il2CppObjectBase).IsAssignableFrom(x)) proxies.TryAdd(x.FullName, x);
            }
        }
        for (; t != null; t = t.BaseType)
            if (proxies.TryGetValue(t.FullName.Replace('/', '+'), out var p)) return p;
        return null;
    }

    internal static object As(Il2CppObjectBase o)
    {
        var p = Proxy(o.TryCast<Il2CppSystem.Object>().GetIl2CppType());
        return p == null ? o : Activator.CreateInstance(p, o.Pointer);
    }

    static string Fmt(object v, int depth)
    {
        switch (v)
        {
            case null: return "null";
            case string s: return "\"" + s + "\"";
            case float f: return f.ToString("G5");
            case Vector2 or Vector3 or Vector4 or Quaternion or Color or Matrix4x4 or Rect: return v.ToString();
            case UnityEngine.Object uo: return uo == null ? "null(dead)" : $"{uo.GetIl2CppType().Name}'{uo.name}'";
            case Il2CppObjectBase ob:
                var t = v.GetType();
                if (t.Name.StartsWith("Il2Cpp") && t.Name.Contains("Array"))
                {
                    var len = t.GetProperty("Length")?.GetValue(v);
                    if (depth > 0 && len is int n && n > 0)
                    {
                        var item = t.GetProperty("Item");
                        var parts = new List<string>();
                        for (int i = 0; i < Math.Min(n, 8); i++)
                            try { parts.Add(Fmt(item.GetValue(v, new object[] { i }), depth - 1)); } catch { parts.Add("?"); }
                        return $"[{n}] {{{string.Join("; ", parts)}}}";
                    }
                    return $"[{len}]";
                }
                if (t.Name.StartsWith("List`1"))
                    return $"List[{t.GetProperty("Count")?.GetValue(v)}]";
                if (depth > 0) return "{" + Members(As(ob), depth - 1, "") + "}";
                return t.Name;
            default:
                if (v.GetType().IsValueType && !v.GetType().IsPrimitive && !v.GetType().IsEnum && depth > 0)
                    return "{" + Members(v, depth - 1, "") + "}";
                return v.ToString();
        }
    }

    static readonly HashSet<string> Skip = new() { "Pointer", "ObjectClass", "WasCollected", "isActiveAndEnabled", "gameObject", "transform",
        "tag", "name", "hideFlags", "useGUILayout", "runInEditMode", "allowPrefabModeInPlayMode", "destroyCancellationToken" };

    static string Members(object o, int depth, string filter)
    {
        var sb = new StringBuilder();
        var stop = new HashSet<Type> { typeof(Il2CppObjectBase), typeof(Il2CppSystem.Object), typeof(UnityEngine.Object), typeof(Component), typeof(Behaviour), typeof(MonoBehaviour), typeof(object), typeof(ValueType) };
        for (var t = o.GetType(); t != null && !stop.Contains(t); t = t.BaseType)
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (Skip.Contains(p.Name) || p.GetIndexParameters().Length > 0 || p.GetMethod == null) continue;
                if (filter.Length > 0 && p.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string val;
                try { val = Fmt(p.GetValue(o), depth); } catch (Exception e) { val = "!" + (e.InnerException ?? e).GetType().Name; }
                sb.Append($" {p.Name}={val};");
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (filter.Length > 0 && f.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string val;
                try { val = Fmt(f.GetValue(o), depth); } catch (Exception e) { val = "!" + e.GetType().Name; }
                sb.Append($" {f.Name}={val};");
            }
        }
        return sb.ToString();
    }

    // inspect <path> [TypeName] [depth] [memberFilter]
    internal static void Components(string path, string type, int depth, string filter)
    {
        var go = Resolve(path);
        if (go == null) { Plugin.L.LogWarning("inspect: not found " + path); return; }
        var sb = new StringBuilder($"===== inspect {path} =====\n");
        foreach (var c in go.GetComponents<Component>())
        {
            var name = c.GetIl2CppType().Name;
            if (type != null && type != "*" && name != type) continue;
            var b = c.TryCast<Behaviour>();
            sb.AppendLine($"-- {c.GetIl2CppType().FullName}{(b != null ? $" enabled={b.enabled}" : "")}");
            sb.AppendLine("  " + Members(As(c), depth, filter ?? ""));
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    internal static string Obj(Il2CppObjectBase o, int depth = 1, string filter = "") => Members(As(o), depth, filter);

    // typeinfo <regex>: every property and method of game types whose full name matches
    internal static void TypeInfo(string pattern)
    {
        var re = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var sb = new StringBuilder($"===== typeinfo {pattern} =====\n");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var an = asm.GetName().Name;
            if (an != "Assembly-CSharp" && an != "Assembly-CSharp-firstpass") continue;
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
            foreach (var t in types)
            {
                if (!re.IsMatch(t.FullName ?? "")) continue;
                var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Select(p => p.Name + ":" + p.PropertyType.Name);
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).Select(m => m.Name).Distinct();
                sb.AppendLine($"{t.FullName} : {t.BaseType?.Name}\n  props: {string.Join(", ", props)}\n  methods: {string.Join(", ", methods)}");
            }
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    // findmember <regex>: interop types with a property or method whose name matches (game assemblies only)
    internal static void FindMember(string pattern)
    {
        var re = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var sb = new StringBuilder($"===== findmember {pattern} =====\n");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var an = asm.GetName().Name;
            if (an != "Assembly-CSharp" && an != "Assembly-CSharp-firstpass") continue;
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
            foreach (var t in types)
            {
                var hits = new List<string>();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (re.IsMatch(p.Name)) hits.Add(p.Name + ":" + p.PropertyType.Name);
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (!m.IsSpecialName && re.IsMatch(m.Name)) hits.Add(m.Name + "()");
                if (hits.Count > 0) sb.AppendLine($"{t.FullName}: {string.Join(", ", hits.Distinct())}");
            }
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    // "main" is the game's main camera; '+' stands for a space (commands are split on spaces).
    static GameObject Resolve(string path)
    {
        if (path == "main") { var g = Global.Instance; return g != null && g.mainCamera != null ? g.mainCamera.gameObject : null; }
        return GameObject.Find(path.Replace('+', ' '));
    }

    // setmember <path> <TypeName> <member> <value>: write a float/int/bool/enum property of a component (A/B tests)
    internal static void Set(string path, string type, string member, string value)
    {
        var go = Resolve(path);
        if (go == null) { Plugin.L.LogWarning("setmember: not found " + path); return; }
        foreach (var c in go.GetComponents<Component>())
        {
            if (c.GetIl2CppType().Name != type) continue;
            var o = As(c);
            var p = o.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || p.SetMethod == null) { Plugin.L.LogWarning($"setmember: {type}.{member} not writable"); return; }
            var t = p.PropertyType;
            object v = t.IsEnum ? Enum.Parse(t, value) : t == typeof(bool) ? value is "1" or "true" : Convert.ChangeType(value, t, System.Globalization.CultureInfo.InvariantCulture);
            var before = p.GetValue(o);
            p.SetValue(o, v);
            Plugin.L.LogInfo($"setmember: {path} {type}.{member} {Fmt(before, 0)} -> {Fmt(p.GetValue(o), 0)}");
            return;
        }
        Plugin.L.LogWarning($"setmember: no {type} on {path}");
    }

    // mats <path> [depth]: renderers below an object with their materials, shaders, keywords and queues
    internal static void Materials(string path, int maxDepth)
    {
        var go = Resolve(path);
        if (go == null) { Plugin.L.LogWarning("mats: not found " + path); return; }
        var sb = new StringBuilder($"===== mats {path} =====\n");
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            int d = 0;
            for (var t = r.transform; t != null && t != go.transform; t = t.parent) d++;
            if (d > maxDepth) continue;
            sb.AppendLine($"{Diag.PathOf(r.transform)} [{r.GetIl2CppType().Name}] on={r.enabled && r.gameObject.activeInHierarchy} layer={LayerMask.LayerToName(r.gameObject.layer)}");
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) { sb.AppendLine("   (null material)"); continue; }
                sb.AppendLine($"   mat '{m.name}' shader='{(m.shader != null ? m.shader.name : "-")}' queue={m.renderQueue} keywords=[{string.Join(" ", m.shaderKeywords)}] " +
                              $"tex={Diag.Name(m.mainTexture)}");
            }
        }
        Plugin.L.LogInfo(sb.ToString());
    }

    // matprops <path> <prop>...: float/color/texture values of named properties on the renderers' materials
    internal static void MaterialProps(string path, IEnumerable<string> props)
    {
        var go = Resolve(path);
        if (go == null) { Plugin.L.LogWarning("matprops: not found " + path); return; }
        var sb = new StringBuilder($"===== matprops {path} =====\n");
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                sb.Append($"{r.name}/{m.name}:");
                foreach (var p in props)
                {
                    if (!m.HasProperty(p)) continue;
                    string v;
                    try { var tex = m.GetTexture(p); v = tex != null ? $"tex'{tex.name}' {tex.width}x{tex.height}" : $"{m.GetFloat(p):G4}/{m.GetColor(p)}"; }
                    catch { v = $"{m.GetFloat(p):G4}"; }
                    sb.Append($" {p}={v}");
                }
                sb.AppendLine();
            }
        Plugin.L.LogInfo(sb.ToString());
    }

    // find <TypeName> [max]: paths of active components of a type
    internal static void Find(string type, int max)
    {
        var sb = new StringBuilder($"===== find {type} =====\n");
        int n = 0;
        foreach (var b in UnityEngine.Object.FindObjectsOfType<Component>())
        {
            if (b.GetIl2CppType().Name != type) continue;
            if (n++ < max) sb.AppendLine($"  {Diag.PathOf(b.transform)} pos={Diag.V(b.transform.position)}");
        }
        sb.AppendLine($"  total {n}");
        Plugin.L.LogInfo(sb.ToString());
    }
}
