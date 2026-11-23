using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace CloudpunkVR;

// Post-processing that the game computes for one flat screen, fixed or replaced for two eyes:
//   - depth of field: the cockpit volume focuses on the windshield and blurs the city behind it; in a headset the
//     eyes do the focusing, so the effect is off in VR
//   - screen-space lens artefacts (Amplify Bloom's lens flare ghosts and halo, lens dirt, starburst; WhiteCat's
//     headlight flares): drawn per eye at mirrored screen positions, so they float at different places in each eye.
//     Off in VR; headlights get world-space glows instead (VRGlows)
//   - WhiteCat global fog: reconstructs view rays from _CameraVectorX/Y/Z, which the game computes once per frame
//     from the camera's symmetric field of view. Set per eye from that eye's own view and projection
//   - windshield: keep the tinted, slightly frosted glass, but scale the grab-pass blur down and make rain drops
//     brighter than the glass behind them, so they read as drops instead of dark specks
internal static class VRPostFx
{
    static readonly int CameraVectorX = Shader.PropertyToID("_CameraVectorX");
    static readonly int CameraVectorY = Shader.PropertyToID("_CameraVectorY");
    static readonly int CameraVectorZ = Shader.PropertyToID("_CameraVectorZ");

    internal static bool FogFix = true;     // dev A/B switches ("vrfx fog|dof|flare 0|1")
    internal static bool DofOff = true;
    internal static bool FlaresOff = true;
    internal static bool GlassTune = true;

    public static void Apply(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(DepthOfField), nameof(DepthOfField.IsEnabledAndSupported)),
            prefix: new HarmonyMethod(typeof(VRPostFx), nameof(NoDepthOfField)));
        h.Patch(AccessTools.Method(typeof(WhiteCat.Rendering.PostProcessing.PostLensFlare), nameof(WhiteCat.Rendering.PostProcessing.PostLensFlare.IsEnabledAndSupported)),
            prefix: new HarmonyMethod(typeof(VRPostFx), nameof(NoScreenFlares)));
        h.Patch(AccessTools.Method(typeof(WhiteCat.Rendering.CameraVectorsCache), nameof(WhiteCat.Rendering.CameraVectorsCache.Update)),
            postfix: new HarmonyMethod(typeof(VRPostFx), nameof(CameraVectorsPostfix)));
        Camera.onPreRender = Camera.onPreRender + (Camera.CameraCallback)new Action<Camera>(OnPreRender);
    }

    static bool NoDepthOfField(ref bool __result)
    {
        using var __pt = PluginTiming.Measure("patch:dof");
        if (!DofOff) return true;
        __result = false;
        return false;
    }

    static bool NoScreenFlares(ref bool __result)
    {
        using var __pt = PluginTiming.Measure("patch:lensflare");
        if (!FlaresOff) return true;
        __result = false;
        return false;
    }

    // A camera that draws the world into the headset: the game's main camera, and on the title screen the menu camera
    // (Global/UI/Menu/MenuCamera), which carries the same post-processing and fog.
    static bool IsHmdMain(Camera cam) =>
        cam != null && cam.targetTexture == null && cam.stereoTargetEye != StereoTargetEyeMask.None && UnityEngine.XR.XRSettings.enabled
        && cam.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>() != null;

    // The stream's spectator view renders the main camera once more in mono (Coo.CooView).
    static bool IsSpectator(Camera cam)
    {
        var g = Global.Instance;
        return Coo.CooView.Rendering && g != null && g.mainCamera != null && cam != null && cam.GetInstanceID() == g.mainCamera.GetInstanceID();
    }

    static void CameraVectorsPostfix(Camera camera)
    {
        using var __pt = PluginTiming.Measure("patch:camvectors");
        if (FogFix && (IsHmdMain(camera) || IsSpectator(camera))) SetEyeVectors(camera);
    }

    static void OnPreRender(Camera cam)
    {
        using var __pt = PluginTiming.Measure("cb:postfx.pre");
        try
        {
            if (FogFix && (IsHmdMain(cam) || IsSpectator(cam))) SetEyeVectors(cam);
            if (DevCommands.EyeLogFrames > 0 && IsHmdMain(cam))
            {
                var e = cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
                var pm = cam.GetStereoProjectionMatrix(e);
                Plugin.L.LogInfo($"eye: f={Time.frameCount} {cam.stereoActiveEye} target={(RenderTexture.active != null ? RenderTexture.active.name + " " + RenderTexture.active.width + "x" + RenderTexture.active.height : "-")} " +
                                 $"active={(cam.activeTexture != null ? cam.activeTexture.name : "-")} p.m02={pm.m02:F3} p.m00={pm.m00:F3} sky={Shader.GetGlobalTexture("_GlobalFogSkybox")?.name}");
                if (e == Camera.StereoscopicEye.Right) DevCommands.EyeLogFrames--;
            }
        }
        catch (Exception e)
        {
            if (!reportedFog) { reportedFog = true; Plugin.L.LogError("fog vectors: " + e); }
        }
    }
    static bool reportedFog, loggedFog;

    // The fog shader's view ray for texture coordinate (u, v) is Z + u*X + v*Y, scaled so the forward component is 1
    // (it is multiplied by linear eye depth). Z is the ray through the corner at uv (0,0), X spans the width and Y the
    // height. The game derives them from the camera's symmetric frustum; here they come from the eye's projection.
    static void SetEyeVectors(Camera cam)
    {
        Matrix4x4 p, camToWorld;
        var eye = Camera.StereoscopicEye.Left;
        if (IsSpectator(cam))
        {
            // mono spectator render: the camera's own frustum (the eye's vectors from earlier this frame would not fit)
            p = cam.projectionMatrix;
            camToWorld = cam.cameraToWorldMatrix;
        }
        else
        {
            var active = cam.stereoActiveEye;
            if (active == Camera.MonoOrStereoscopicEye.Mono) return;
            eye = active == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            p = cam.GetStereoProjectionMatrix(eye);
            camToWorld = cam.GetStereoViewMatrix(eye).inverse;
        }
        Vector3 Ray(float x, float y) => camToWorld.MultiplyVector(new Vector3((x + p.m02) / p.m00, (y + p.m12) / p.m11, -1f));

        // Which corner uv (0,0) is: keep the orientation the game uses (its Y points down the screen on D3D).
        var gameY = (Vector3)Shader.GetGlobalVector(CameraVectorY);
        bool topLeft = gameY.sqrMagnitude < 1e-8f || Vector3.Dot(gameY, cam.transform.up) < 0;
        float y0 = topLeft ? 1 : -1;
        // The shader scales the ray by Linear01Depth, so the game makes the forward component of every ray the far
        // clip distance.
        float scale = cam.farClipPlane;
        var origin = Ray(-1, y0) * scale;
        var x = Ray(1, y0) * scale - origin;
        var y = Ray(-1, -y0) * scale - origin;
        if (!loggedFog && DevCommands.Enabled)
        {
            loggedFog = true;
            Plugin.L.LogInfo($"fog vectors {eye}: game Z={Shader.GetGlobalVector(CameraVectorZ)} X={Shader.GetGlobalVector(CameraVectorX)} Y={gameY} | " +
                             $"eye Z={origin} X={x} Y={y} topLeft={topLeft} scale={scale}");
        }
        Shader.SetGlobalVector(CameraVectorZ, origin);
        Shader.SetGlobalVector(CameraVectorX, x);
        Shader.SetGlobalVector(CameraVectorY, y);
    }

    // Periodic pass (cameras and car interiors come and go with scenes).
    public static void Refresh()
    {
        foreach (var bloom in UnityEngine.Object.FindObjectsOfType<AmplifyBloom.AmplifyBloomEffect>())
        {
            if (bloom.ApplyLensFlare == !FlaresOff && bloom.ApplyLensDirt == !FlaresOff && !bloom.ApplyLensStardurst) continue;
            Remember(bloom);
            var orig = bloomDefaults[bloom.GetInstanceID()];
            bloom.ApplyLensFlare = !FlaresOff && orig.flare;
            bloom.ApplyLensDirt = !FlaresOff && orig.dirt;
            bloom.ApplyLensStardurst = !FlaresOff && orig.star;
            Plugin.L.LogInfo($"vr fx: lens flare/dirt/starburst {(FlaresOff ? "off" : "restored")} on {Diag.PathOf(bloom.transform)}");
        }
        TuneGlass();
    }

    static readonly Dictionary<int, (bool flare, bool dirt, bool star)> bloomDefaults = new();
    static void Remember(AmplifyBloom.AmplifyBloomEffect b)
    {
        int id = b.GetInstanceID();
        if (!bloomDefaults.ContainsKey(id)) bloomDefaults[id] = (b.ApplyLensFlare, b.ApplyLensDirt, b.ApplyLensStardurst);
    }

    // ---- windshield ----

    sealed class GlassDefaults
    {
        public float Blur, Noise, Samples, Refraction, NormalOffset, Boost;
        public Color RainColor;
        public float AppliedBlur = -1;
    }
    static readonly Dictionary<int, GlassDefaults> glass = new();
    const string WindshieldShader = "Roman's Shaders/Windshield Shader";

    static void TuneGlass()
    {
        var g = Global.Instance;
        var vi = g != null ? g.vehicleInterior : null;
        if (vi == null) return;
        var car = g.playerCar != null ? g.playerCar.gameObject : null;
        if (car == null) return;
        float blur = Plugin.Cfg.WindshieldBlur.Value;
        foreach (var r in car.GetComponentsInChildren<Renderer>(true))
        {
            var m = r.sharedMaterial;
            if (m == null || m.shader == null || m.shader.name != WindshieldShader) continue;
            int id = m.GetInstanceID();
            if (!glass.TryGetValue(id, out var d))
            {
                d = new GlassDefaults
                {
                    Blur = m.GetFloat("_BlurIntensity"), Noise = m.GetFloat("_NoiseIntensity"), Samples = m.GetFloat("_BlurSamples"),
                    Refraction = m.GetFloat("_RefractionAmount"), NormalOffset = m.GetFloat("_NormalOffset"),
                    Boost = m.GetFloat("_RainColorBoost"), RainColor = m.GetColor("_RainColor"),
                };
                glass[id] = d;
            }
            float key = GlassTune ? blur : -2;
            if (d.AppliedBlur == key) continue;
            d.AppliedBlur = key;
            if (!GlassTune)
            {
                m.SetFloat("_BlurIntensity", d.Blur); m.SetFloat("_NoiseIntensity", d.Noise); m.SetFloat("_BlurSamples", d.Samples);
                m.SetColor("_RainColor", d.RainColor); m.SetFloat("_RainColorBoost", d.Boost);
                m.SetFloat("_RefractionAmount", d.Refraction); m.SetFloat("_NormalOffset", d.NormalOffset);
                continue;
            }
            // The blur radius is a share of the eye texture, which spans a wider angle than the desktop view; scale it
            // to the requested share of the game's blur. Fewer samples are enough for the smaller radius.
            m.SetFloat("_BlurIntensity", d.Blur * blur);
            m.SetFloat("_NoiseIntensity", d.Noise * blur);
            m.SetFloat("_BlurSamples", blur <= 0 ? 1 : Mathf.Clamp(Mathf.Round(d.Samples * Mathf.Max(blur, 0.5f)), 4, d.Samples));
            // Drop pixels show the refracted grab texture times the rain colour; the glass around them shows it times
            // the glass colour plus the flow map, so with the game's values drops are darker than the glass. Make them
            // bend more light and come out a little brighter than the glass.
            m.SetColor("_RainColor", Color.white);
            m.SetFloat("_RainColorBoost", Mathf.Max(d.Boost, 1.3f));
            m.SetFloat("_RefractionAmount", d.Refraction * 2.2f);
            m.SetFloat("_NormalOffset", Mathf.Max(d.NormalOffset, 0.16f));
            Plugin.L.LogInfo($"vr fx: windshield {Diag.PathOf(r.transform)} blur {d.Blur:G3} -> {d.Blur * blur:G3}, drops boost {Mathf.Max(d.Boost, 1.3f):G3} refraction {d.Refraction * 2.2f:G3}");
        }
    }
}
