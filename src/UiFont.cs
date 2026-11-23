using System;
using UnityEngine;

namespace CloudpunkVR;

internal static class UiFont
{
    // A dynamic font from the system's fonts: Chinese comments and lines need any character, and the game's own fonts
    // only hold the characters it uses. This build strips Font's constructors, so the native object is made by hand and
    // initialised the way the constructor would (Font.CreateDynamicFontFromOSFont throws MissingMethod).
    public static Font Os(string[] names, int size)
    {
        try
        {
            var obj = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_new(Il2CppInterop.Runtime.Il2CppClassPointerStore<Font>.NativeClassPtr);
            var f = new Font(obj);
            Font.Internal_CreateDynamicFont(f, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray(names), size);
            f.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return f;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning("system font unavailable, using the default font: " + e.Message);
            return Font.GetDefault();
        }
    }
}
