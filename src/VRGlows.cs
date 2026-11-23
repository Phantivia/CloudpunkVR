using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace CloudpunkVR;

// World-space glows for the lights that carry a WhiteCat LensFlareSource (car headlights). The game draws those as
// a screen-space flare; in VR that flare sits at a different place in each eye, so it is switched off (VRPostFx) and
// replaced by an additive glow at the light, which both eyes see in the same place and geometry can hide.
//
// Cost: the city has some 250 of these lights (traffic headlights). They used to be found with a scene-wide search
// every second and drawn as one object each, read through a dozen engine calls each per frame: 0.5 ms of main thread
// a frame in Midtown, plus a draw per glow per eye. Now the sources register themselves (the flare's OnEnable and
// OnDisable, which also fire as traffic cars leave and return to their pool), their fixed settings are read once,
// their light state a few per frame in turn, each costs one engine call a frame (its transform matrix), and all glows
// are one mesh, one draw per eye.
internal sealed class VRGlows
{
    sealed class Source
    {
        public WhiteCat.Rendering.LensFlareSource Flare;
        public Light Light;
        public Transform T;
        public bool Spot, WorkWhenOff, On;
        public float CosEdge, SizeMul, Shell, IntensityMul, Intensity;
        public Color Color;
    }

    const int MaxGlows = 256;
    const int RefreshPerFrame = 16;
    static readonly Dictionary<IntPtr, Source> registry = new();
    static readonly List<Source> sources = new();
    static bool listDirty, scanned;
    int cursor, lastDrawn;

    static readonly int TintColor = Shader.PropertyToID("_TintColor");
    Material material;
    Transform root;
    Mesh mesh;
    MeshRenderer meshRenderer;
    Il2CppStructArray<Vector3> verts;
    Il2CppStructArray<Color32> colors;
    int drawn;
    string viewName = "-";
    float maxOff;

    public static void Patch(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(WhiteCat.Rendering.LensFlareSource), "OnEnable"), postfix: new HarmonyMethod(typeof(VRGlows), nameof(Enabled)));
        h.Patch(AccessTools.Method(typeof(WhiteCat.Rendering.LensFlareSource), "OnDisable"), postfix: new HarmonyMethod(typeof(VRGlows), nameof(Disabled)));
    }

    static void Enabled(WhiteCat.Rendering.LensFlareSource __instance) => Register(__instance);

    static void Disabled(WhiteCat.Rendering.LensFlareSource __instance)
    {
        if (__instance != null && registry.Remove(__instance.Pointer)) listDirty = true;
    }

    static void Register(WhiteCat.Rendering.LensFlareSource f)
    {
        if (f == null || registry.ContainsKey(f.Pointer)) return;
        var light = f.lightSource != null ? f.lightSource : f.GetComponent<Light>();
        if (light == null) return;
        var s = new Source
        {
            Flare = f,
            Light = light,
            T = light.transform,
            Spot = light.type == LightType.Spot,
            WorkWhenOff = f.workWhenLightDisabled,
            CosEdge = Mathf.Cos(Mathf.Min(80f, light.spotAngle * 0.5f + 15f) * Mathf.Deg2Rad),
            SizeMul = Mathf.Max(0.05f, f.sizeMultiplier),
            Shell = Mathf.Max(f.shellRadius, 0.3f),
            IntensityMul = Mathf.Max(0.1f, f.intensityMultiplier),
        };
        Refresh(s);
        registry[f.Pointer] = s;
        listDirty = true;
    }

    // The light's state (headlights switch, dim and change colour rarely): a few sources per frame, in turn.
    static void Refresh(Source s)
    {
        s.On = s.Light.enabled || s.WorkWhenOff;
        s.Intensity = Mathf.Min(s.Light.intensity, 8f);
        s.Color = s.Light.color;
    }

    public void Setup()
    {
        var go = new GameObject("CloudpunkVR_Glows");
        UnityEngine.Object.DontDestroyOnLoad(go);
        root = go.transform;
    }

    // The additive particle shader lives in the city's shared assets, so it can only be found once a level loaded.
    bool EnsureMaterial()
    {
        if (material != null) return true;
        var shader = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Mobile/Particles/Additive");
        if (shader == null) return false;
        // Scene loads unload assets nothing references; the glows are often all hidden then.
        material = new Material(shader) { name = "CloudpunkVR_Glow", mainTexture = GlowTexture(), hideFlags = HideFlags.DontUnloadUnusedAsset };
        material.mainTexture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        // The shader draws 2 x tint x vertex colour x texture; each glow's colour goes in its vertex colours.
        material.SetColor(TintColor, Color.white);
        BuildMesh();
        Plugin.L.LogInfo($"glows: using {shader.name}");
        return true;
    }

    void BuildMesh()
    {
        mesh = new Mesh { name = "CloudpunkVR_Glows", hideFlags = HideFlags.DontUnloadUnusedAsset };
        mesh.MarkDynamic();
        verts = new Il2CppStructArray<Vector3>(MaxGlows * 4);
        colors = new Il2CppStructArray<Color32>(MaxGlows * 4);
        var uv = new Il2CppStructArray<Vector2>(MaxGlows * 4);
        var tris = new Il2CppStructArray<int>(MaxGlows * 6);
        for (int i = 0; i < MaxGlows; i++)
        {
            uv[i * 4] = new Vector2(0, 0); uv[i * 4 + 1] = new Vector2(1, 0); uv[i * 4 + 2] = new Vector2(0, 1); uv[i * 4 + 3] = new Vector2(1, 1);
            int v = i * 4, t = i * 6;
            tris[t] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1; tris[t + 3] = v + 2; tris[t + 4] = v + 3; tris[t + 5] = v + 1;
        }
        mesh.vertices = verts;
        mesh.uv = uv;
        mesh.colors32 = colors;
        mesh.triangles = tris;

        var go = new GameObject("glows");
        go.layer = 1;   // TransparentFX: drawn by the main camera, not by the minimap or the mirrors
        go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        meshRenderer.enabled = false;
    }

    static Texture2D GlowTexture()
    {
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "CloudpunkVR_GlowTex", wrapMode = TextureWrapMode.Clamp };
        var px = new Color[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                float r2 = dx * dx + dy * dy;
                // bright core with a soft wide halo, zero at the quad edge
                float v = Mathf.Max(0, 0.75f * Mathf.Exp(-r2 * 28f) + 0.35f * Mathf.Exp(-r2 * 5f) - 0.0067f * (1 + r2 * 6f) * r2);
                v *= Mathf.Clamp01((1 - r2) * 4f);
                px[y * n + x] = new Color(v, v, v, v);
            }
        tex.SetPixels(px);
        tex.Apply(false, true);
        return tex;
    }

    public string Describe() => $"glows: {sources.Count} registered headlight sources, {drawn} drawn for {viewName}, quad-to-lamp at most {maxOff:F2} m";

    // Called once per frame after the head pose is applied, before culling. The glows are placed, sized and turned
    // for the camera the world is actually seen through: on the title screen that is the menu camera, while the main
    // camera sits switched off somewhere else; measured against that one, the title taxi's headlight glows were
    // pulled metres off their lamps toward it and trailed behind them as the taxi flew.
    public void BeforeRender(Camera main)
    {
        main = VRCameras.WorldCamera(main);
        if (!EnsureMaterial()) return;
        if (!scanned)
        {
            // sources enabled before the hooks were in place (once; afterwards they report themselves)
            scanned = true;
            foreach (var f in UnityEngine.Object.FindObjectsOfType<WhiteCat.Rendering.LensFlareSource>())
                if (f.isActiveAndEnabled) Register(f);
        }
        if (listDirty)
        {
            listDirty = false;
            sources.Clear();
            sources.AddRange(registry.Values);
        }
        for (int k = 0; k < RefreshPerFrame && sources.Count > 0; k++)
        {
            var s = sources[cursor = (cursor + 1) % sources.Count];
            if (s.Flare == null || s.Light == null) { RemoveDead(); break; }
            Refresh(s);
        }

        int n = 0;
        maxOff = 0;
        viewName = main != null ? main.name : "-";
        if (main != null && Plugin.Cfg.HeadlightGlow.Value && VRPostFx.FlaresOff)
        {
            var ct = main.transform;
            var eye = ct.position;
            var rot = ct.rotation;
            var right = rot * Vector3.right;
            var up = rot * Vector3.up;
            float gain = 0.08f * Plugin.Cfg.HeadlightGlowIntensity.Value;
            foreach (var s in sources)
            {
                if (!s.On) continue;
                var m = s.T.localToWorldMatrix;
                var lamp = new Vector3(m.m03, m.m13, m.m23);
                var toEye = eye - lamp;
                float dist = toEye.magnitude;
                if (dist < 0.5f || dist > 600f) continue;
                var dir = toEye / dist;
                // Facing: a headlight glares when it points at the viewer.
                float facing = 1;
                if (s.Spot)
                {
                    var fwd = new Vector3(m.m02, m.m12, m.m22).normalized;
                    facing = Mathf.Clamp01((Vector3.Dot(fwd, dir) - s.CosEdge) / (1 - s.CosEdge));
                    facing *= facing;
                    if (facing <= 0) continue;
                }
                float fade = Mathf.Clamp01((600f - dist) / 200f) * Mathf.Clamp01((dist - 0.5f) / 2f);
                float strength = facing * fade * s.Intensity * s.IntensityMul * gain;
                if (strength <= 0.003f) continue;
                // Angular size shrinks slower than distance so far headlights stay visible as points.
                float size = s.SizeMul * (0.9f + 0.035f * dist);
                // Pull the glow towards the viewer past the lamp housing so the lamp's own mesh does not clip it.
                float pull = Mathf.Min(s.Shell + size * 0.25f, dist * 0.5f);
                var pos = lamp + dir * pull;
                maxOff = Mathf.Max(maxOff, pull);
                var hr = right * (size * 0.5f);
                var hu = up * (size * 0.5f);
                int v = n * 4;
                verts[v] = pos - hr - hu; verts[v + 1] = pos + hr - hu; verts[v + 2] = pos - hr + hu; verts[v + 3] = pos + hr + hu;
                var c = s.Color * strength;
                var c32 = new Color32((byte)(Mathf.Clamp01(c.r) * 255), (byte)(Mathf.Clamp01(c.g) * 255), (byte)(Mathf.Clamp01(c.b) * 255), 255);
                colors[v] = c32; colors[v + 1] = c32; colors[v + 2] = c32; colors[v + 3] = c32;
                if (++n == MaxGlows) break;
            }
            // collapse the glows drawn last frame and not this one
            for (int i = n * 4; i < lastDrawn * 4; i++) verts[i] = eye;
            if (n > 0 || lastDrawn > 0)
            {
                mesh.vertices = verts;
                mesh.colors32 = colors;
                mesh.bounds = new Bounds(eye, new Vector3(1300, 1300, 1300));
            }
        }
        lastDrawn = n;
        if (meshRenderer.enabled != n > 0) meshRenderer.enabled = n > 0;
        drawn = n;
    }

    // A source whose object was destroyed without being disabled (rare): rebuild the list without the dead entries.
    static void RemoveDead()
    {
        var dead = new List<IntPtr>();
        foreach (var kv in registry) if (kv.Value.Flare == null || kv.Value.Light == null) dead.Add(kv.Key);
        foreach (var k in dead) registry.Remove(k);
        listDirty = true;
    }
}
