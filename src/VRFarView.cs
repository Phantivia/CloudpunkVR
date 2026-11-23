using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using GlobalFog = WhiteCat.Rendering.PostProcessing.GlobalFog;

namespace CloudpunkVR;

// How far the city shows in VR. The game ends its view at about 600 m in four places: the camera's far plane,
// Unity's linear fog (clear to 300 m, solid at 600 m), WhiteCat's global fog (near/far 500 and 600 m in the city)
// and per-layer cull distances on the main camera (600 m for buildings, floors and streets, 100-400 m for traffic,
// lamps and small details). With a draw distance beyond that:
//   - the far plane goes to DrawDistance (VRCameras);
//   - Unity's fog becomes a long ramp: clear to 30% of DrawDistance, then thickening evenly to 95% just past the far
//     plane. Distant towers show through the haze as silhouettes and lights, fainter with distance, instead of
//     vanishing into a wall at 600 m (at 2500 m: 86% gets through at 1 km, 59% at 1.5 km, 6% at 2.5 km). An
//     exponential-squared fog with 3% left at the far plane was tried: it fogs the middle distance more (57% at
//     600 m for a 1500 m draw distance);
//   - WhiteCat's fog keeps its shape, scaled to the new distance (its density divided by the same factor);
//   - only the layers the game draws to its full range (the buildings and floors that make the skyline) are drawn
//     further. Traffic, lamps and small details keep the game's distances: measured from a car in Level3, the city
//     beyond 600 m is ~630 renderers of buildings and floors, and every renderer is drawn twice in VR (once per
//     eye), while the frame is bound by the render thread, not the GPU.
// The game's values are put back at the end of every frame, so its weather and district scripts keep reading and
// writing their own numbers.
internal sealed class VRFarView
{
    bool applied;
    float gameStart, gameEnd, gameDensity;
    FogMode gameMode;

    // Dev (fogtest): force a fog mode and density on top, to see which modes the game's shaders were built with.
    public static FogMode? TestMode;
    public static float TestDensity;
    Camera cam;
    float[] gameCull;

    public static float Factor { get; private set; } = 1f;
    static int activeFrame = -1;

    // WhiteCat's global fog (a post effect on top of Unity's fog) has its own near/far distances, 500 and 600 m in the
    // city, and a density. It writes them to shader globals while it renders; scale them after it does, for the main
    // camera on frames the far view is on. Dev knobs: fogcfg.
    public static float FogDistanceMul = 1f, FogDensityMul = 1f;   // on top of the draw distance factor
    static bool loggedFog;

    public static void Patch(Harmony h) =>
        h.Patch(AccessTools.Method(typeof(GlobalFog), nameof(GlobalFog.UpdateGlobalParams)),
            postfix: new HarmonyMethod(typeof(VRFarView), nameof(GlobalFogPostfix)));

    static void GlobalFogPostfix(PostProcessRenderContext context)
    {
        using var __pt = PluginTiming.Measure("patch:globalfog");
        if (activeFrame != Time.frameCount || Factor <= 1f) return;
        var g = Global.Instance;
        var cam = context != null ? context.camera : null;
        if (g == null || g.mainCamera == null || cam == null || cam.GetInstanceID() != g.mainCamera.GetInstanceID()) return;
        float k = Factor;
        float near = Shader.GetGlobalFloat(GlobalFog._fogNearID), far = Shader.GetGlobalFloat(GlobalFog._fogFarID), density = Shader.GetGlobalFloat(GlobalFog._densityID);
        Shader.SetGlobalFloat(GlobalFog._fogNearID, near * k * FogDistanceMul);
        Shader.SetGlobalFloat(GlobalFog._fogFarID, far * k * FogDistanceMul);
        Shader.SetGlobalFloat(GlobalFog._densityID, density / k * FogDensityMul);
        if (!loggedFog && DevCommands.Enabled)
        {
            loggedFog = true;
            Plugin.L.LogInfo($"far view: global fog near {near:F0} -> {near * k * FogDistanceMul:F0}, far {far:F0} -> {far * k * FogDistanceMul:F0}, density {density:G3} -> {density / k * FogDensityMul:G3}");
        }
    }

    public void BeforeRender(Camera main)
    {
        float far = Plugin.Cfg.DrawDistance.Value;
        if (main == null || far <= 0 || applied) return;
        float gameFar = RenderSettings.fog && RenderSettings.fogMode == FogMode.Linear ? RenderSettings.fogEndDistance : 600f;
        if (gameFar <= 1f || far <= gameFar) return;
        float k = far / gameFar;
        Factor = k;

        gameStart = RenderSettings.fogStartDistance;
        gameEnd = RenderSettings.fogEndDistance;
        gameDensity = RenderSettings.fogDensity;
        gameMode = RenderSettings.fogMode;
        if (RenderSettings.fog)
        {
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = far * 0.3f;
            RenderSettings.fogEndDistance = far * 1.05f;
            if (TestMode is FogMode tm) { RenderSettings.fogMode = tm; RenderSettings.fogDensity = TestDensity; }
        }

        cam = main;
        gameCull = main.layerCullDistances;
        var cull = main.layerCullDistances;
        for (int i = 0; i < 32; i++)
        {
            float d = gameCull[i];
            if (d >= gameFar * 0.99f) cull[i] = far;
        }
        main.layerCullDistances = cull;
        applied = true;
        activeFrame = Time.frameCount;
    }

    public void AfterRender()
    {
        if (!applied) return;
        applied = false;
        RenderSettings.fogStartDistance = gameStart;
        RenderSettings.fogEndDistance = gameEnd;
        RenderSettings.fogDensity = gameDensity;
        RenderSettings.fogMode = gameMode;
        if (cam != null && gameCull != null) cam.layerCullDistances = gameCull;
        cam = null;
    }
}
