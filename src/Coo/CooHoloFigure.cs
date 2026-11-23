using System;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR.Coo;

// 可缇 popping out of the cockpit call screen as a hologram: her whole figure from the Spout frame stands on the top
// edge of the screen's portrait, projected by a cone of light from the screen, with a halo, a pulsing ring at her feet,
// motes rising through the beam and a flickering cyan light on the cockpit.
//
// The game is Unity 2018 IL2CPP, so there is no compiling shaders here. Everything is drawn with shaders every build
// has: the figure picture is rendered each frame into its own texture with the Spout frame cut into strips (a strip
// slides sideways now and then: tearing), then scan lines and a flicker multiplied over it (Hidden/Internal-Colored
// with a multiply blend); in the car it is two quads, the picture alpha-blended and the same picture additive and a
// little larger (the glow, which the game's bloom picks up). Beam, ring and motes are vertex-coloured meshes through
// Internal-Colored with an additive blend.
internal sealed class CooHoloFigure
{
    public static float Height = 0.30f;    // figure height, metres
    public static float Lift = 0.015f;     // feet above the top edge of the portrait
    public static float Out = 0.035f;      // toward the driver, off the screen plane
    public static float Yaw = 0f;          // turned toward the driver by this much, degrees
    public float Zoom = 1f;                // 1 = the whole frame height of the Spout picture
    public float Aspect = 0.68f;           // width / height of the figure picture (the chibi's head is wide)

    readonly CooSpout spout;
    GameObject root;
    Transform figure, glow, beam, ring, motes;
    Mesh moteMesh;
    Light lamp;
    RenderTexture pic;
    Material picMat, glowMat, addMat, mulMat, figMat;
    bool failed;
    float alpha, glitchUntil, nextGlitch, born;
    readonly System.Random rnd;
    // Film (FilmCockpit's "holo"): the picture from the job's PNG sequence instead of the Spout frame, cropped by
    // FilmCrop (uv, origin bottom left), and every animation on the film clock (repeatable frames)
    public Texture FilmSource;
    public Rect FilmCrop = new(0, 0, 1, 1);
    public double FilmClock = double.NaN;
    public int PicHeight = 512;
    float Now => double.IsNaN(FilmClock) ? Time.unscaledTime : (float)FilmClock;
    const int Motes = 18;
    readonly Vector3[] motePos = new Vector3[Motes];
    readonly float[] moteSpeed = new float[Motes];

    public CooHoloFigure(CooSpout spout, int seed = 0)
    {
        this.spout = spout;
        rnd = seed != 0 ? new System.Random(seed) : new System.Random();
    }

    public bool Visible => alpha > 0;

    static Shader Find(params string[] names)
    {
        foreach (var n in names) { var s = Shader.Find(n); if (s != null) return s; }
        return null;
    }

    static Material Colored(int src, int dst)
    {
        var sh = Find("Hidden/Internal-Colored");
        if (sh == null) return null;
        var m = new Material(sh) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        m.SetInt("_SrcBlend", src);
        m.SetInt("_DstBlend", dst);
        m.SetInt("_Cull", 0);
        m.SetInt("_ZWrite", 0);
        m.renderQueue = 3100;
        return m;
    }

    void Build(Transform parent, int layer)
    {
        root = new GameObject("CloudpunkVR_CortiHolo");
        root.transform.SetParent(parent, false);
        int ph = FilmSource != null ? PicHeight : 512, pw = FilmSource != null ? Mathf.RoundToInt(PicHeight * Aspect) : 352;
        pic = new RenderTexture(pw, ph, 0, RenderTextureFormat.ARGB32) { name = "CloudpunkVR_CortiFigure", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset };
        pic.Create();
        var alphaShader = Find("Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended");
        var addShader = Find("Legacy Shaders/Particles/Additive", "Particles/Additive");
        picMat = new Material(alphaShader) { mainTexture = pic, renderQueue = 3110, hideFlags = HideFlags.DontUnloadUnusedAsset };
        glowMat = new Material(addShader ?? alphaShader) { mainTexture = pic, renderQueue = 3105, hideFlags = HideFlags.DontUnloadUnusedAsset };
        // beam, ring, motes: vertex colours through the additive particle shader (white by default without a texture),
        // else Internal-Colored set to One One
        addMat = addShader != null ? new Material(addShader) { renderQueue = 3100, hideFlags = HideFlags.DontUnloadUnusedAsset } : Colored(1, 1);
        mulMat = Colored(2, 0);   // DstColor Zero: multiply, for the scan lines baked into the picture (none without it)
        if (picMat.shader == null || addMat == null) throw new InvalidOperationException("no particle shaders in this build");

        figure = Quad("Figure", picMat, layer);
        glow = Quad("Glow", glowMat, layer);
        beam = MeshObject("Beam", BeamMesh(), addMat, layer);
        ring = MeshObject("Ring", RingMesh(), addMat, layer);
        moteMesh = new Mesh { name = "CloudpunkVR_Motes" };
        moteMesh.MarkDynamic();
        motes = MeshObject("Motes", moteMesh, addMat, layer);
        for (int i = 0; i < Motes; i++) { motePos[i] = RandomMote(true); moteSpeed[i] = 0.25f + (float)rnd.NextDouble() * 0.35f; }

        var lampGo = new GameObject("Light");
        lampGo.transform.SetParent(root.transform, false);
        lampGo.layer = layer;
        lamp = lampGo.AddComponent<Light>();
        lamp.type = LightType.Point;
        lamp.color = new Color(0.35f, 0.8f, 1f);
        lamp.range = 0.9f;
        lamp.intensity = 0;
        lamp.shadows = LightShadows.None;
        root.layer = layer;
        root.SetActive(false);
        Plugin.L.LogInfo($"corti: pop-out hologram ready (picture {alphaShader?.name}, glow and beam {addMat.shader?.name}, scan lines {(mulMat != null ? mulMat.shader.name : "none")})");
    }

    Transform Quad(string name, Material mat, int layer)
    {
        var m = new Mesh { name = "CloudpunkVR_" + name };
        // a unit quad standing on its bottom edge, facing -z (like a canvas facing the viewer)
        m.vertices = new[] { new Vector3(-0.5f, 0, 0), new Vector3(-0.5f, 1, 0), new Vector3(0.5f, 1, 0), new Vector3(0.5f, 0, 0) };
        m.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
        m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        m.RecalculateBounds();
        return MeshObject(name, m, mat, layer);
    }

    Transform MeshObject(string name, Mesh mesh, Material mat, int layer)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        go.layer = layer;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return go.transform;
    }

    // The projection beam: an open cone from a narrow mouth at the screen up past the figure's head, bright at the
    // mouth and fading upward; the rim of the cone brighter than its middle (seen through, the sides stack up).
    static Mesh BeamMesh()
    {
        const int sides = 24;
        var v = new Vector3[(sides + 1) * 2];
        var c = new Color[v.Length];
        var tri = new int[sides * 6];
        for (int i = 0; i <= sides; i++)
        {
            float a = i * Mathf.PI * 2 / sides;
            var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a) * 0.55f);   // flattened: the screen is wider than deep
            v[i * 2] = dir * 0.12f;
            v[i * 2 + 1] = dir * 0.42f + Vector3.up * 1.08f;
            c[i * 2] = new Color(0.10f, 0.42f, 0.62f, 1);
            c[i * 2 + 1] = new Color(0, 0, 0, 0);
        }
        for (int i = 0; i < sides; i++)
        {
            int k = i * 6, b = i * 2;
            tri[k] = b; tri[k + 1] = b + 1; tri[k + 2] = b + 3;
            tri[k + 3] = b; tri[k + 4] = b + 3; tri[k + 5] = b + 2;
        }
        var m = new Mesh { name = "CloudpunkVR_Beam", vertices = v, colors = c, triangles = tri };
        m.RecalculateBounds();
        return m;
    }

    // The ring at her feet: a flat band, bright inside, fading out.
    static Mesh RingMesh()
    {
        const int sides = 32;
        var v = new Vector3[(sides + 1) * 3];
        var c = new Color[v.Length];
        var tri = new int[sides * 12];
        for (int i = 0; i <= sides; i++)
        {
            float a = i * Mathf.PI * 2 / sides;
            var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a) * 0.55f);
            v[i * 3] = dir * 0.16f; v[i * 3 + 1] = dir * 0.22f; v[i * 3 + 2] = dir * 0.34f;
            c[i * 3] = new Color(0, 0, 0, 0); c[i * 3 + 1] = new Color(0.35f, 0.85f, 1f, 1); c[i * 3 + 2] = new Color(0, 0, 0, 0);
        }
        for (int i = 0; i < sides; i++)
        {
            int k = i * 12, b = i * 3, n = b + 3;
            tri[k] = b; tri[k + 1] = n; tri[k + 2] = b + 1; tri[k + 3] = b + 1; tri[k + 4] = n; tri[k + 5] = n + 1;
            tri[k + 6] = b + 1; tri[k + 7] = n + 1; tri[k + 8] = b + 2; tri[k + 9] = b + 2; tri[k + 10] = n + 1; tri[k + 11] = n + 2;
        }
        var m = new Mesh { name = "CloudpunkVR_Ring", vertices = v, colors = c, triangles = tri };
        m.RecalculateBounds();
        return m;
    }

    Vector3 RandomMote(bool anyHeight)
    {
        float a = (float)rnd.NextDouble() * Mathf.PI * 2, r = Mathf.Sqrt((float)rnd.NextDouble()) * 0.28f;
        return new Vector3(Mathf.Cos(a) * r, anyHeight ? (float)rnd.NextDouble() : 0, Mathf.Sin(a) * r * 0.55f);
    }

    // Rising motes, small diamonds facing the driver (local -z), fading at both ends of their climb.
    void UpdateMotes(float dt, float a)
    {
        var v = new Vector3[Motes * 4];
        var c = new Color[Motes * 4];
        var tri = new int[Motes * 6];
        for (int i = 0; i < Motes; i++)
        {
            var p = motePos[i];
            p.y += moteSpeed[i] * dt * 0.5f;
            if (p.y > 1) { p = RandomMote(false); moteSpeed[i] = 0.25f + (float)rnd.NextDouble() * 0.35f; }
            motePos[i] = p;
            float s = 0.008f + 0.006f * (i % 3);
            float f = Mathf.Sin(p.y * Mathf.PI) * a * (0.6f + 0.4f * Mathf.Sin(Now * 7 + i));
            var col = new Color(0.4f * f, 0.85f * f, 1f * f, 1);
            int k = i * 4;
            // the mote cloud spans the figure's height; widths are in figure-height units scaled on the parent
            var q = new Vector3(p.x, p.y, p.z);
            v[k] = q + new Vector3(0, -s, 0); v[k + 1] = q + new Vector3(-s * 0.6f, 0, 0); v[k + 2] = q + new Vector3(0, s, 0); v[k + 3] = q + new Vector3(s * 0.6f, 0, 0);
            c[k] = c[k + 1] = c[k + 2] = c[k + 3] = col;
            int t = i * 6;
            tri[t] = k; tri[t + 1] = k + 1; tri[t + 2] = k + 2; tri[t + 3] = k; tri[t + 4] = k + 2; tri[t + 5] = k + 3;
        }
        moteMesh.Clear();
        moteMesh.vertices = v;
        moteMesh.colors = c;
        moteMesh.triangles = tri;
        moteMesh.RecalculateBounds();
    }

    public Rect Crop()
    {
        float frameAspect = spout.Height > 0 ? (float)spout.Width / spout.Height : 16f / 9f;
        float h = 1f / Mathf.Max(0.3f, Zoom);
        float w = h * Aspect / frameAspect;
        // her centre on the box's horizontal centre, standing on the bottom of the frame
        float y0 = Mathf.Clamp(spout.CenterY - h * 0.55f, 0, Mathf.Max(0, 1 - h));
        return new Rect(spout.CenterX - w / 2, y0, w, h);
    }

    // The picture: the Spout crop in horizontal strips (one slides aside during a glitch), then scan lines rolling
    // down and the flicker, multiplied over it.
    void DrawPicture(float now)
    {
        var src = FilmSource != null ? FilmSource : spout?.Source;
        bool film = FilmSource != null, flip = !film && spout.FlipY;
        // UI/Default writes alpha; the legacy particle shaders (the box's composite) have ColorMask RGB, which left the
        // picture fully transparent
        figMat ??= new Material(Shader.Find("UI/Default")) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        var blend = figMat;
        var prev = RenderTexture.active;
        bool srgb = GL.sRGBWrite;
        try
        {
            RenderTexture.active = pic;
            GL.sRGBWrite = false;
            GL.Clear(false, true, Color.clear);
            if (src == null || blend == null) return;
            blend.mainTexture = src;
            var uv = film ? FilmCrop : Crop();
            bool glitch = now < glitchUntil;
            if (now > nextGlitch) { glitchUntil = now + 0.08f + (float)rnd.NextDouble() * 0.14f; nextGlitch = now + 1.5f + (float)rnd.NextDouble() * 4f; }
            GL.PushMatrix();
            GL.LoadOrtho();
            blend.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            const int strips = 24;
            int torn = glitch ? rnd.Next(strips) : -1;
            float shift = glitch ? ((float)rnd.NextDouble() - 0.5f) * 0.12f : 0;
            for (int i = 0; i < strips; i++)
            {
                float y0 = (float)i / strips, y1 = (float)(i + 1) / strips;
                float dx = i == torn || i == torn + 1 ? shift : 0;
                float sv0 = uv.y + uv.height * y0, sv1 = uv.y + uv.height * y1;
                float v0 = flip ? 1f - sv0 : sv0, v1 = flip ? 1f - sv1 : sv1;
                GL.TexCoord2(uv.x, v0); GL.Vertex3(dx, y0, 0);
                GL.TexCoord2(uv.x, v1); GL.Vertex3(dx, y1, 0);
                GL.TexCoord2(uv.xMax, v1); GL.Vertex3(1 + dx, y1, 0);
                GL.TexCoord2(uv.xMax, v0); GL.Vertex3(1 + dx, y0, 0);
            }
            GL.End();

            // scan lines and the flicker, multiplied over everything (alpha too, so the lines are see-through)
            if (mulMat == null) { GL.PopMatrix(); return; }
            mulMat.SetPass(0);
            GL.Begin(GL.QUADS);
            float flicker = 0.9f + 0.1f * Mathf.PerlinNoise(now * 9f, 0.3f) - (glitch ? 0.15f : 0);
            const int lines = 128;
            float roll = now * 0.05f % (1f / lines * 2);
            for (int i = 0; i < lines; i++)
            {
                float y0 = (float)i / lines - roll, y1 = y0 + 0.5f / lines;
                float k = flicker * (i % 2 == 0 ? 0.72f : 1f);
                GL.Color(new Color(k, k, k, k));
                GL.Vertex3(0, y0, 0); GL.Vertex3(0, y1 + 0.5f / lines, 0); GL.Vertex3(1, y1 + 0.5f / lines, 0); GL.Vertex3(1, y0, 0);
            }
            GL.End();
            GL.PopMatrix();
        }
        finally
        {
            GL.sRGBWrite = srgb;
            RenderTexture.active = prev;
        }
    }

    // Each frame before rendering. anchor: the portrait of the cockpit call box; show: whether the box shows.
    public void Update(RectTransform anchor, bool show, float dt)
    {
        if (failed) return;
        float target = show && anchor != null && (FilmSource != null || (spout != null && spout.Live)) ? 1 : 0;
        float was = alpha;
        alpha = Mathf.MoveTowards(alpha, target, dt / (target > 0 ? 0.45f : 0.25f));
        if (alpha > 0 && was == 0) born = Now;
        if (root == null)
        {
            if (alpha <= 0 || anchor == null) return;
            try { Build(anchor.root, anchor.gameObject.layer); }
            catch (Exception e) { failed = true; Plugin.L.LogError("corti: pop-out hologram unavailable: " + e); return; }
        }
        float now = Now;
        // part of the cockpit: under the car interior, on its layer (PlayerCar), drawn by the interior camera like the
        // dashboard (the call screen's canvas sits in the same interior space)
        var g = Global.Instance;
        var vi = g != null ? g.vehicleInterior : null;
        if (vi == null || vi.interiorLocal == null || anchor == null) { if (root.activeSelf) root.SetActive(false); return; }
        if (root.transform.parent != vi.interiorLocal)
        {
            root.transform.SetParent(vi.interiorLocal, false);
            SetLayer(root, LayerMask.NameToLayer("PlayerCar"));
            Plugin.L.LogInfo($"corti: pop-out under {Diag.PathOf(vi.interiorLocal)} layer {root.layer}, anchor {Diag.PathOf(anchor)} layer {anchor.gameObject.layer}");
        }
        if (root.activeSelf != alpha > 0) root.SetActive(alpha > 0);
        if (alpha <= 0) return;

        // place: on the top edge of the portrait, standing up with the car, facing the driver's eye
        var c = WorldCorners(anchor);           // bottom left, top left, top right, bottom right
        var top = (c[1] + c[2]) * 0.5f;
        var up = vi.interiorLocal.up;
        var eye = vi.cameraCockpit != null ? vi.cameraCockpit.transform : vi.interiorCameraTransform;
        var face = eye != null ? Vector3.ProjectOnPlane(eye.position - top, up) : Vector3.ProjectOnPlane(-vi.interiorLocal.forward, up);
        if (face.sqrMagnitude < 1e-4f) face = Vector3.ProjectOnPlane(-vi.interiorLocal.forward, up);
        var rot = Quaternion.AngleAxis(Yaw, up) * Quaternion.LookRotation(-face.normalized, up);   // quads face -z
        var t = root.transform;
        t.position = top + up * Lift + face.normalized * Out;
        t.rotation = rot;
        var ps = t.parent.lossyScale;
        // materialising: grows up from the feet
        float grow = Mathf.SmoothStep(0, 1, Mathf.Clamp01((now - born) / 0.5f));
        float hgt = Height * Mathf.Lerp(0.05f, 1, target > 0 ? grow : 1);
        t.localScale = new Vector3(Height / ps.x, hgt / ps.y, Height / ps.z);

        DrawPicture(now);
        figure.localScale = new Vector3(Aspect, 1, 1);
        figure.localPosition = new Vector3(0, 0.02f + 0.008f * Mathf.Sin(now * 1.6f), 0);   // floating, slowly bobbing
        glow.localScale = new Vector3(Aspect * 1.08f, 1.05f, 1);
        glow.localPosition = figure.localPosition + new Vector3(0, -0.025f, 0.002f);
        float flick = 0.85f + 0.15f * Mathf.PerlinNoise(now * 6f, 1.7f);
        // the particle shaders double the tint; linear colour space linearises it (0.7354 reads as neutral)
        picMat.SetColor("_TintColor", new Color(0.60f, 0.80f, 0.86f, 0.62f * alpha * flick));
        glowMat.SetColor("_TintColor", new Color(0.10f, 0.32f, 0.45f, 0.5f * alpha * flick));
        float k = alpha * flick;
        if (addMat.HasProperty("_TintColor")) addMat.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f * k));
        if (addMat.HasProperty("_Color")) addMat.SetColor("_Color", new Color(k, k, k, 1));
        beam.localScale = Vector3.one;
        ring.localScale = Vector3.one * (1f + 0.06f * Mathf.Sin(now * 3.1f));
        ring.localPosition = new Vector3(0, 0.005f, 0);
        UpdateMotes(dt, alpha);
        lamp.transform.localPosition = new Vector3(0, 0.45f, -0.15f);
        lamp.intensity = 1.6f * alpha * flick;
    }

    static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++) SetLayer(go.transform.GetChild(i).gameObject, layer);
    }

    // RectTransform.GetWorldCorners fills a managed array through the interop, which never copies the result back
    // (all zeros); the same from the rectangle.
    public static Vector3[] WorldCorners(RectTransform rt)
    {
        var r = rt.rect;
        return new[]
        {
            rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0)), rt.TransformPoint(new Vector3(r.xMin, r.yMax, 0)),
            rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0)), rt.TransformPoint(new Vector3(r.xMax, r.yMin, 0)),
        };
    }

    public string Describe()
    {
        if (root == null) return "not built";
        var f = figure != null ? figure.position : Vector3.zero;
        return $"alpha {alpha:F2} active {root.activeSelf} parent {Diag.PathOf(root.transform.parent)} layer {root.layer} pos {Diag.V(root.transform.position)} scale {Diag.V(root.transform.lossyScale)} figure {Diag.V(f)}";
    }

    public void Hide()
    {
        alpha = 0;
        if (root != null && root.activeSelf) root.SetActive(false);
    }

    /// <summary>Remove it for good (a film's own figure when the film stops).</summary>
    public void Destroy()
    {
        if (root != null) UnityEngine.Object.Destroy(root);
        if (pic != null) { pic.Release(); UnityEngine.Object.Destroy(pic); }
        root = null; pic = null; alpha = 0;
    }
}
