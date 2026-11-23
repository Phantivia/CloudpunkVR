using System;
using System.Linq;
using HarmonyLib;
using RedAlliance.Util;
using UnityEngine;

namespace CloudpunkVR;

internal static class Patches
{
    // Launch with -cloudpunkvr-nosave to keep test sessions from touching the player's save slots.
    internal static readonly bool BlockSaving = Environment.GetCommandLineArgs()
        .Contains("-cloudpunkvr-nosave", StringComparer.OrdinalIgnoreCase);

    static SteamVRInput rumble;

    internal static void Apply(Harmony h)
    {
        if (BlockSaving)
        {
            h.Patch(AccessTools.Method(typeof(Global), nameof(Global.Save)), prefix: new HarmonyMethod(typeof(Patches), nameof(SkipSave)));
            Plugin.L.LogWarning("saving is blocked for this session (-cloudpunkvr-nosave)");
        }
    }

    internal static void ApplyVR(Harmony h, SteamVRInput input)
    {
        rumble = input;
        h.Patch(AccessTools.Method(typeof(Cinemachine.CinemachineBrain), nameof(Cinemachine.CinemachineBrain.PushStateToUnityCamera)),
            prefix: new HarmonyMethod(typeof(Patches), nameof(BrainPushPrefix)),
            postfix: new HarmonyMethod(typeof(Patches), nameof(BrainPushPostfix)));
        h.Patch(AccessTools.Method(typeof(MainCameraVehicleRenderer), nameof(MainCameraVehicleRenderer.OnPostRender)),
            prefix: new HarmonyMethod(typeof(Patches), nameof(InteriorEye)));
        h.Patch(AccessTools.Method(typeof(Player), nameof(Player.EnterCameraVolume)),
            prefix: new HarmonyMethod(typeof(Patches), nameof(SkipCameraVolume)));
        h.Patch(AccessTools.Method(typeof(GameInput), nameof(GameInput.Vibrate), new[] { typeof(float), typeof(float), typeof(float) }),
            postfix: new HarmonyMethod(typeof(Patches), nameof(RumbleSides)));
        h.Patch(AccessTools.Method(typeof(GameInput), nameof(GameInput.Vibrate), new[] { typeof(float), typeof(float) }),
            postfix: new HarmonyMethod(typeof(Patches), nameof(RumbleBoth)));
    }

    // Walking in first person: the city's camera volumes (CameraControlVolume, triggers at corners and doorways) switch
    // to a fixed shot or swing the free-look camera for third-person framing. In first person they take the view and
    // the walking reference away from the player, so they are skipped there; third person and the car keep them.
    static float nextVolumeLog;
    static bool SkipCameraVolume(CameraControlVolume volume)
    {
        if (!Plugin.Cfg.FirstPersonOnFoot.Value || VRBody.ThirdPerson || Global.IsInVehicle) return true;
        if (Time.unscaledTime >= nextVolumeLog)
        {
            nextVolumeLog = Time.unscaledTime + 2f;
            Plugin.L.LogInfo($"camera volume skipped in first person: {Diag.PathOf(volume != null ? volume.transform : null)} " +
                             $"override={Diag.Name(volume != null ? volume.overrideCamera : null)}");
        }
        return false;
    }

    // Autosave and its callers still run; only the write is skipped, reported as a successful save of that slot.
    static bool SkipSave(int slot, ref int __result)
    {
        Plugin.L.LogInfo($"blocked Global.Save({slot})");
        __result = slot;
        return false;
    }

    // Cinemachine writes the lens every frame; on an HMD camera Unity rejects the field of view and logs a warning
    // each time. Push the state while the camera is briefly non-stereo so clip planes still apply and nothing logs.
    static void BrainPushPrefix(Cinemachine.CinemachineBrain __instance, out bool __state)
    {
        using var __pt = PluginTiming.Measure("patch:brainpush.pre");
        var cam = __instance.OutputCamera;
        __state = cam != null && cam.targetTexture == null && cam.stereoTargetEye == StereoTargetEyeMask.Both;
        if (__state) cam.stereoTargetEye = StereoTargetEyeMask.None;
    }

    static void BrainPushPostfix(Cinemachine.CinemachineBrain __instance, bool __state)
    {
        using var __pt = PluginTiming.Measure("patch:brainpush.post");
        if (__state) __instance.OutputCamera.stereoTargetEye = StereoTargetEyeMask.Both;
        if (__state) VRCameras.Instance?.AfterBrainPush(__instance.OutputCamera);
    }

    // Runs for each eye: MainCameraVehicleRenderer draws the cockpit interior camera inside the main camera's pass.
    static void InteriorEye(MainCameraVehicleRenderer __instance)
    {
        using var __pt = PluginTiming.Measure("patch:interioreye");
        VRCameras.Instance?.SetupInteriorEye(__instance.GetComponent<Camera>(), __instance.interiorCamera);
    }

    static void RumbleSides(float leftStrength, float rightStrength, float duration) =>
        rumble?.Rumble(duration, Math.Max(leftStrength, rightStrength));

    static void RumbleBoth(float strength, float duration) => rumble?.Rumble(duration, strength);
}
