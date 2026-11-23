using UnityEngine;
using UnityEngine.XR;

namespace CloudpunkVR;

// What Unity 2018's OpenVR path renders at eyeTextureResolutionScale 1 is not SteamVR's recommended size: seen at half
// of it per axis and at exactly it, depending on the SteamVR setup. Measure the ratio at start and expose the
// resolution as a share of SteamVR's recommendation.
internal static class Resolution
{
    static float unityPerRecommended = 2f;

    public static void Init()
    {
        XRSettings.eyeTextureResolutionScale = 1f;
        int unityWidth = XRSettings.eyeTextureWidth;
        if (SteamVRInput.TryRecommendedSize(out uint w, out uint h) && unityWidth > 0)
            unityPerRecommended = w / (float)unityWidth;
        Plugin.L.LogInfo($"resolution: SteamVR recommends {w}x{h} per eye, Unity renders {unityWidth}x{XRSettings.eyeTextureHeight} at scale 1 " +
                         $"(factor {unityPerRecommended:F2})");
    }

    public static void Apply(int percent)
    {
        XRSettings.eyeTextureResolutionScale = Mathf.Clamp(percent / 100f * unityPerRecommended, 0.3f, 3f);
        Plugin.L.LogInfo($"resolution: {percent}% of SteamVR's size -> eye {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}");
    }
}
