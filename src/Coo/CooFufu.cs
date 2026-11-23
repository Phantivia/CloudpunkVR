using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace CloudpunkVR.Coo;

// A 可缇 fufu plush sitting in the passenger seat: the seated doll from art/CortiDoll (tools/fufu-export turns it into a
// plain mesh and two textures in BepInEx/plugins/CloudpunkVR/fufu). It is part of the cockpit, under the car interior on
// its layer (PlayerCar), placed from the interior's camera socket (the seat, not the tracked head), so it sits still while
// the player looks around; it rocks a little with the car's turns and acceleration.
internal sealed class CooFufu
{
    public bool On = true;
    // from the camera socket, along the interior's axes (metres), and its turn toward the driver (degrees)
    // measured (interiorparts): the passenger seat cushion's centre 0.77 m right, its top 0.52 m below the eye
    public static float Right = 0.77f, Down = 0.555f, Fwd = -0.04f, Yaw = -40f, Scale = 1.5f;
    public static float LightIntensity = 0.9f;
    Light fill;

    GameObject root;
    Transform doll;
    Material mat;
    bool loading, failed;
    (int w, int h, byte[] rgba) baseTex, normalTex;   // rgba: every mip level, largest first
    Vector3[] pos, nrm;
    Vector2[] uv;
    int[] tris;
    Vector3 sway;
    Vector3 lastVel;

    static string Path(string name) => System.IO.Path.Combine(Plugin.Dir, "fufu", name);

    void StartLoad()
    {
        loading = true;
        if (!File.Exists(Path("fufu.mesh"))) { failed = true; Plugin.L.LogInfo("fufu: no model in plugins/CloudpunkVR/fufu (tools/fufu-export)"); return; }
        Task.Run(() =>
        {
            try
            {
                var b = File.ReadAllBytes(Path("fufu.mesh"));
                if (b[0] != 'C' || b[1] != 'F' || b[2] != 'U' || b[3] != '1') throw new InvalidDataException("not a fufu mesh");
                int nv = BitConverter.ToInt32(b, 4), ni = BitConverter.ToInt32(b, 8), at = 12;
                var p = new Vector3[nv]; var n = new Vector3[nv]; var u = new Vector2[nv];
                for (int i = 0; i < nv; i++, at += 32)
                {
                    p[i] = new Vector3(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));
                    n[i] = new Vector3(BitConverter.ToSingle(b, at + 12), BitConverter.ToSingle(b, at + 16), BitConverter.ToSingle(b, at + 20));
                    u[i] = new Vector2(BitConverter.ToSingle(b, at + 24), BitConverter.ToSingle(b, at + 28));
                }
                var t = new int[ni];
                for (int i = 0; i < ni; i++, at += 4) t[i] = BitConverter.ToInt32(b, at);
                // Blender's front faces wind counter-clockwise, Unity's clockwise (the same triangle seen from outside)
                for (int i = 0; i + 2 < ni; i += 3) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
                var bt = WithMips(Png.Decode(File.ReadAllBytes(Path("fufu_base.png"))));
                var nt = File.Exists(Path("fufu_normal.png")) ? WithMips(Png.Decode(File.ReadAllBytes(Path("fufu_normal.png")))) : default;
                lock (this) { pos = p; nrm = n; uv = u; tris = t; baseTex = bt; normalTex = nt; }
            }
            catch (Exception e) { failed = true; Plugin.L.LogWarning("fufu: model unreadable: " + e.Message); }
        });
    }

    // The full mip chain after the picture (2x2 box filter; a square power-of-two picture).
    static (int w, int h, byte[] rgba) WithMips((int w, int h, byte[] rgba) d)
    {
        var levels = new System.Collections.Generic.List<byte[]> { d.rgba };
        int w = d.w, h = d.h;
        var cur = d.rgba;
        int total = cur.Length;
        while (w > 1 || h > 1)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1), y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                        int s = cur[(y0 * w + x0) * 4 + c] + cur[(y0 * w + x1) * 4 + c] + cur[(y1 * w + x0) * 4 + c] + cur[(y1 * w + x1) * 4 + c];
                        next[(y * nw + x) * 4 + c] = (byte)((s + 2) >> 2);
                    }
            levels.Add(next);
            total += next.Length;
            cur = next; w = nw; h = nh;
        }
        var all = new byte[total];
        int at = 0;
        foreach (var l in levels) { Buffer.BlockCopy(l, 0, all, at, l.Length); at += l.Length; }
        return (d.w, d.h, all);
    }

    static Shader FindShader(params string[] names)
    {
        foreach (var s in names) { var sh = Shader.Find(s); if (sh != null) return sh; }
        return null;
    }

    static Texture2D Tex((int w, int h, byte[] rgba) d, bool linear, string name)
    {
        var t = new Texture2D(d.w, d.h, TextureFormat.RGBA32, true, linear) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4, hideFlags = HideFlags.DontUnloadUnusedAsset };
        t.LoadRawTextureData(d.rgba);
        t.Apply(false, true);
        return t;
    }

    void Build()
    {
        Vector3[] p; Vector3[] n; Vector2[] u; int[] t;
        lock (this) { p = pos; n = nrm; u = uv; t = tris; }
        root = new GameObject("CloudpunkVR_CortiFufu");
        UnityEngine.Object.DontDestroyOnLoad(root);
        var go = new GameObject("Doll");
        go.transform.SetParent(root.transform, false);
        doll = go.transform;
        var mesh = new Mesh { name = "CloudpunkVR_Fufu" };
        if (p.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = p;
        mesh.normals = n;
        mesh.uv = u;
        mesh.triangles = t;
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        var sh = FindShader("Standard", "Legacy Shaders/Bumped Diffuse", "Legacy Shaders/Diffuse", "Unlit/Texture");
        mat = new Material(sh) { name = "CloudpunkVR_Fufu", hideFlags = HideFlags.DontUnloadUnusedAsset };
        mat.mainTexture = Tex(baseTex, false, "CloudpunkVR_FufuBase");
        if (normalTex.rgba != null && mat.HasProperty("_BumpMap"))
        {
            mat.SetTexture("_BumpMap", Tex(normalTex, true, "CloudpunkVR_FufuNormal"));
            mat.EnableKeyword("_NORMALMAP");
        }
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.12f);
        if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        r.receiveShadows = true;
        baseTex = normalTex = default;
        // the cockpit is lit dim and blue; a small soft light over the seat keeps the white plush readable
        var lg = new GameObject("Fill");
        lg.transform.SetParent(root.transform, false);
        lg.transform.localPosition = new Vector3(0.05f, 0.42f, -0.25f);
        fill = lg.AddComponent<Light>();
        fill.type = LightType.Point;
        fill.color = new Color(1f, 0.93f, 0.85f);
        fill.range = 0.9f;
        fill.intensity = LightIntensity;
        fill.shadows = LightShadows.None;
        root.SetActive(false);
        Plugin.L.LogInfo($"fufu: ready ({p.Length} vertices, {t.Length / 3} triangles, shader {sh?.name})");
    }

    static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++) SetLayer(go.transform.GetChild(i).gameObject, layer);
    }

    // Each frame before rendering.
    public void Update(float dt)
    {
        var g = Global.Instance;
        bool want = On && g != null && Global.IsInVehicle && Global.IsInVehicleCockpit && g.vehicleInterior != null && g.vehicleInterior.interiorLocal != null;
        if (!want) { if (root != null && root.activeSelf) root.SetActive(false); return; }
        if (failed) return;
        if (root == null)
        {
            if (!loading) StartLoad();
            bool ready;
            lock (this) ready = pos != null;
            if (!ready) return;
            try { Build(); }
            catch (Exception e)
            {
                failed = true;
                Plugin.L.LogError("fufu: " + (e.InnerException ?? e).Message + " " + e);
                if (root != null) UnityEngine.Object.Destroy(root);
                root = null;
                return;
            }
        }
        var vi = g.vehicleInterior;
        if (root.transform.parent != vi.interiorLocal)
        {
            root.transform.SetParent(vi.interiorLocal, false);
            SetLayer(root, LayerMask.NameToLayer("PlayerCar"));
            Plugin.L.LogInfo($"fufu: seated under {Diag.PathOf(vi.interiorLocal)}");
        }
        if (!root.activeSelf) root.SetActive(true);
        var socket = vi.interiorCameraTransform != null ? vi.interiorCameraTransform : vi.interiorLocal;
        var il = vi.interiorLocal;
        var up = il.up;
        var fwd = Vector3.ProjectOnPlane(il.forward, up).normalized;
        var right = Vector3.Cross(up, fwd);
        var t = root.transform;
        t.position = socket.position + right * Right - up * Down + fwd * Fwd;
        // the doll faces -z: turned to face forward, then toward the driver by Yaw
        t.rotation = Quaternion.AngleAxis(Yaw, up) * Quaternion.LookRotation(-fwd, up);
        if (fill != null) fill.intensity = LightIntensity;
        var ps = il.lossyScale;
        t.localScale = new Vector3(Scale / ps.x, Scale / ps.y, Scale / ps.z);

        // a soft plush: leans against the car's acceleration and turns, springing back
        var car = g.playerCar;
        if (car != null && car.rBody != null && dt > 0)
        {
            var vel = car.transform.InverseTransformDirection(car.rBody.velocity);
            var acc = (vel - lastVel) / Mathf.Max(dt, 1e-3f);
            lastVel = vel;
            float yawRate = car.rBody.angularVelocity.y * Mathf.Rad2Deg;
            var target = new Vector3(Mathf.Clamp(-acc.z * 0.6f, -8, 8), 0, Mathf.Clamp(yawRate * 0.12f + acc.x * 0.5f, -10, 10));
            sway = Vector3.Lerp(sway, target, 1 - Mathf.Exp(-dt * 4f));
        }
        doll.localRotation = Quaternion.Euler(sway.x, 0, sway.z);
    }

    public void SetShader(string name)
    {
        var sh = Shader.Find(name);
        if (sh == null || mat == null) { Plugin.L.LogWarning("fufu: no shader " + name); return; }
        mat.shader = sh;
        Plugin.L.LogInfo("fufu: shader " + sh.name);
    }

    public string Describe() => root == null ? (failed ? "failed" : loading ? "loading" : "not built")
        : $"active {root.activeSelf} parent {Diag.PathOf(root.transform.parent)} layer {root.layer} pos {Diag.V(root.transform.position)} shader {mat?.shader?.name}";
}
