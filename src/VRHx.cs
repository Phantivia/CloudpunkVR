using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace CloudpunkVR;

// Hx Volumetric Lighting (the halos and beams around street lamps, signs and headlights) drawn for each eye with that
// eye's own matrices.
//
// Hx takes any camera that renders both eyes for single-pass stereo: its main matrix set comes from the right eye and
// a second set ("2") from the left, for the shader to pick by eye index. The game renders multi-pass, where its
// shaders only read the main set, so the left eye drew the right eye's picture: on the Quest 3, whose eye frusta are
// mirrored and asymmetric, every halo sat a quarter of the screen off in the left eye (a test light 4 m ahead: its halo
// at u 0.384 in the left eye's buffer where the eye sees the light at 0.626; the right eye's 0.377 against 0.376).
// Hx records everything into command buffers once per frame, in its OnPreCull, so both eyes run the same commands.
// Now that build records the left eye, and the right eye gets a build of its own between the eyes (as the left eye
// finishes; Hx's own OnPreCull again, which first finds the lights in view: BuildBuffer alone recorded no lights):
//   - per light, Hx's lights take their volume matrix from the camera's MatrixVP / MatrixV, which are set to the eye
//     being built just before the lights are recorded (CreateLightbuffers);
//   - the per-camera matrices (inverse projection and view, camera-to-world, inverse view-projection, last frame's
//     view-projection for the temporal filter) are appended to Hx's buffer after its own, with the eye's values; the
//     last frame's view-projection is kept per eye, since the two builds alternate;
//   - the temporal filter keeps its history per eye: the right eye's build is given a texture of its own, or each eye
//     would blend the other eye's picture into its own.
// Measured after: halos at 0.625 / 0.375 against 0.626 / 0.376. Cost: the second build, 0.11 ms of main thread a frame.
internal static class VRHx
{
    public static bool Enabled = true;   // dev A/B: hxeyes 0|1
    public static bool OwnRightHistory = true; // dev: hxhist 0|1

    static int buildEye = -1;            // eye the current build records (0 left, 1 right), -1: leave Hx alone
    static int rebuilding = -1;          // eye RebuildForEye is building
    static bool injectedLights;
    static readonly Matrix4x4[] lastVP = new Matrix4x4[2];
    static readonly bool[] haveLast = new bool[2];
    static Matrix4x4 buildVP;
    static RenderTexture rightHistory;
    static bool rightHistoryOwn;         // false: a temporary Hx allocated in its place

    static readonly int InvP1 = Shader.PropertyToID("InverseProjectionMatrix1");
    static readonly int HxInvP1 = Shader.PropertyToID("hxInverseP1");
    static readonly int HxInvP2 = Shader.PropertyToID("hxInverseP2");
    static readonly int CamToWorld = Shader.PropertyToID("hxCameraToWorld");
    static readonly int CamToWorld2 = Shader.PropertyToID("hxCameraToWorld2");
    static readonly int InvViewProj = Shader.PropertyToID("_InvViewProj");
    static readonly int LastVP = Shader.PropertyToID("hxLastVP");
    static readonly int LastVP2 = Shader.PropertyToID("hxLastVP2");

    // Dev: build timing.
    static readonly System.Diagnostics.Stopwatch rebuildWatch = new();
    static double rebuildMs;
    static int rebuilds;

    public static void Patch(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(HxVolumetricCamera), "BuildBuffer"),
            prefix: new HarmonyMethod(typeof(VRHx), nameof(BuildPrefix)), postfix: new HarmonyMethod(typeof(VRHx), nameof(BuildPostfix)));
        h.Patch(AccessTools.Method(typeof(HxVolumetricCamera), "CreateLightbuffers"),
            prefix: new HarmonyMethod(typeof(VRHx), nameof(LightsPrefix)));
        h.Patch(AccessTools.Method(typeof(HxVolumetricLight), "BuildBuffer"),
            prefix: new HarmonyMethod(typeof(VRHx), nameof(LightPrefix)));
    }

    static bool BothEyes(Camera cam) =>
        cam != null && XRSettings.enabled && cam.stereoEnabled && cam.targetTexture == null && cam.stereoTargetEye == StereoTargetEyeMask.Both;

    static void BuildPrefix(HxVolumetricCamera __instance)
    {
        using var __pt = PluginTiming.Measure("patch:hx.build");
        buildEye = !Enabled || !BothEyes(__instance.Mycamera) ? -1 : rebuilding >= 0 ? rebuilding : 0;
        injectedLights = false;
    }

    static void BuildPostfix()
    {
        if (buildEye >= 0) { lastVP[buildEye] = buildVP; haveLast[buildEye] = true; }
        buildEye = -1;
    }

    // All of Hx's per-camera globals are recorded by now; its lights are recorded next.
    static void LightsPrefix(HxVolumetricCamera __instance)
    {
        using var __pt = PluginTiming.Measure("patch:hx.lights");
        if (buildEye < 0) return;
        try
        {
            var cam = __instance.Mycamera;
            var eye = buildEye == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
            var p = cam.GetStereoProjectionMatrix(eye);
            var v = cam.GetStereoViewMatrix(eye);
            __instance.MatrixVP = GL.GetGPUProjectionMatrix(p, true) * v;
            __instance.MatrixV = v;
            buildVP = GL.GetGPUProjectionMatrix(p, false) * v;
            Inject(__instance.BufferRender, p, v);
        }
        catch (Exception e) { Report(e); }
    }

    // The lights may record into a buffer of their own; give it the same values ahead of the first light.
    static void LightPrefix(CommandBuffer __0)
    {
        using var __pt = PluginTiming.Measure("patch:hx.light");
        var CameraBuffer = __0;
        if (buildEye < 0 || injectedLights || CameraBuffer == null) return;
        injectedLights = true;
        try
        {
            var cam = HxVolumetricCamera.Active != null ? HxVolumetricCamera.Active.Mycamera : null;
            if (cam == null) return;
            var eye = buildEye == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
            Inject(CameraBuffer, cam.GetStereoProjectionMatrix(eye), cam.GetStereoViewMatrix(eye));
        }
        catch (Exception e) { Report(e); }
    }

    // The same forms Hx uses: raw projection inverses, GPU projection (not flipped) for the view-projections.
    static void Inject(CommandBuffer cb, Matrix4x4 p, Matrix4x4 v)
    {
        if (cb == null) return;
        var pInv = p.inverse;
        var c2w = v.inverse;
        var vp = GL.GetGPUProjectionMatrix(p, false) * v;
        var gInv = GL.GetGPUProjectionMatrix(p, false).inverse;
        cb.SetGlobalMatrix(HxVolumetricCamera.InverseProjectionMatrixPID, pInv);
        cb.SetGlobalMatrix(HxVolumetricCamera.InverseProjectionMatrix2PID, pInv);
        cb.SetGlobalMatrix(InvP1, pInv);
        cb.SetGlobalMatrix(HxInvP1, gInv);
        cb.SetGlobalMatrix(HxInvP2, gInv);
        cb.SetGlobalMatrix(HxVolumetricCamera.InverseViewMatrixPID, c2w);
        cb.SetGlobalMatrix(CamToWorld, c2w);
        cb.SetGlobalMatrix(CamToWorld2, c2w);
        cb.SetGlobalMatrix(InvViewProj, vp.inverse);
        if (haveLast[buildEye])
        {
            cb.SetGlobalMatrix(LastVP, lastVP[buildEye]);
            cb.SetGlobalMatrix(LastVP2, lastVP[buildEye]);
        }
    }

    // Called as the world camera finishes its left eye: build Hx again, for the right eye.
    public static void RebuildForEye(Camera cam, int eye)
    {
        if (!Enabled || !BothEyes(cam)) return;
        var hx = cam.GetComponent<HxVolumetricCamera>();
        if (hx == null || !hx.isActiveAndEnabled) return;
        bool dev = DevCommands.Enabled;
        if (dev) rebuildWatch.Restart();
        var leftHistory = hx.TemporalTexture;
        var leftId = hx.TemporalTextureRTID;
        bool swap = eye == 1 && OwnRightHistory && hx.TemporalSampling && leftHistory != null;
        if (swap)
        {
            if (rightHistory == null || !rightHistory.IsCreated() || rightHistory.width != leftHistory.width || rightHistory.height != leftHistory.height
                || rightHistory.format != leftHistory.format)
            {
                if (rightHistory != null)
                {
                    if (rightHistoryOwn) { rightHistory.Release(); UnityEngine.Object.Destroy(rightHistory); }
                    else RenderTexture.ReleaseTemporary(rightHistory);
                }
                // kept across scene loads (they unload assets nothing in the scene refers to)
                rightHistory = new RenderTexture(leftHistory.descriptor) { name = "CloudpunkVR_HxHistoryRight", hideFlags = HideFlags.DontUnloadUnusedAsset };
                rightHistory.Create();
                rightHistoryOwn = true;
                Plugin.L.LogInfo($"hx: right eye history {rightHistory.width}x{rightHistory.height} {rightHistory.format}");
            }
            hx.TemporalTexture = rightHistory;
            hx.TemporalTextureRTID = new RenderTargetIdentifier(rightHistory);
        }
        rebuilding = eye;
        try
        {
            // Hx's own pre-cull: finds the lights in view (a build uses them up), then builds
            hx.OnPreCull();
        }
        catch (Exception e) { Report(e); }
        finally
        {
            rebuilding = -1;
            if (swap)
            {
                // Hx replaces a history texture whose size no longer fits (it released ours); keep its new one.
                if (hx.TemporalTexture != null && hx.TemporalTexture.GetInstanceID() != rightHistory.GetInstanceID())
                {
                    rightHistory = hx.TemporalTexture;
                    rightHistoryOwn = false;
                }
                hx.TemporalTexture = leftHistory;
                hx.TemporalTextureRTID = leftId;
            }
        }
        if (dev)
        {
            rebuildMs += rebuildWatch.Elapsed.TotalMilliseconds;
            if (++rebuilds == 300)
            {
                Plugin.L.LogInfo($"hx: right eye rebuild {rebuildMs / rebuilds:F3} ms average over {rebuilds} frames");
                rebuilds = 0; rebuildMs = 0;
            }
        }
    }

    static bool reported;
    static void Report(Exception e)
    {
        if (reported) return;
        reported = true;
        Plugin.L.LogError("hx eyes: " + e);
    }
}
