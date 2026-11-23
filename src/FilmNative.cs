using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace CloudpunkVR;

// Mesh data handed to the game without a new game-side array per upload. Assigning a managed array to a mesh property
// (mesh.vertices = Vector3[]) makes Il2CppInterop allocate a fresh array in the game's heap and copy into it every call
// (the film stage's LED wall: ~0.5 M vertices, several MB, up to four times a frame). Here each managed array gets one
// game-side array, allocated once, and is copied into it as a block (Il2CppStructArray.AsSpan). Measured 11-08: about
// the same speed as the plain assignment; what made the stage slow was the per-element math (see FilmStage.Place).
internal static class FilmNative
{
    static readonly Dictionary<object, object> cache = new(ReferenceEqualityComparer.Instance);

    /// <summary>The game-side copy of a long-lived managed array (same length), refreshed from it.</summary>
    public static Il2CppStructArray<T> Of<T>(T[] a) where T : unmanaged
    {
        if (!cache.TryGetValue(a, out var o) || ((Il2CppStructArray<T>)o).Length != a.Length)
        {
            o = new Il2CppStructArray<T>(a.Length);
            cache[a] = o;
        }
        var n = (Il2CppStructArray<T>)o;
        a.AsSpan().CopyTo(n.AsSpan());
        return n;
    }

    /// <summary>A new game-side array holding these values (for data whose length changes call to call).</summary>
    public static Il2CppStructArray<T> Fresh<T>(ReadOnlySpan<T> a) where T : unmanaged
    {
        var n = new Il2CppStructArray<T>(a.Length);
        a.CopyTo(n.AsSpan());
        return n;
    }

    public static void Clear() => cache.Clear();
}
