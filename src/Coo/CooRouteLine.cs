using System.Collections.Generic;
using UnityEngine;

namespace CloudpunkVR.Coo;

// The planned route drawn on the game's maps. The minimaps (the HUD's on foot, the dashboard's in the cockpit, which
// renders into the "MiniMap" texture) and the full map are cameras looking straight down at objects on the "Minimap"
// layer; a line on that layer just under each map camera shows on that map only (each camera's clip range is a band of
// heights below it), above the map's own drawing. The part already travelled is left out.
//
// The line is a cyan band with white chevrons pointing toward the destination, tiled along it and flowing forward
// (Particles/Alpha Blended scales and scrolls its texture; Sprites/Default would not).
internal sealed class CooRouteLine
{
    sealed class MapLine
    {
        public Camera Cam;
        public LineRenderer Line;
        public Material Mat;
        // what the points were last written for: only the first point (the player) moves every frame
        public List<Vector3> For;
        public int ForProgress = -1;
        public float ForY = float.NaN;
    }

    readonly List<MapLine> lines = new();
    List<Vector3> shown;
    bool visible;
    float nextScan;
    bool scannedCockpit;
    static Texture2D arrows;
    readonly List<Vector3> pts = new();

    public static Color RouteColor = new(0.25f, 0.85f, 1f, 1f);
    public static float ChevronEvery = 2.6f;   // one chevron per this many line widths
    public static float FlowSpeed = 0.9f;      // chevrons a second

    public void Show(List<Vector3> route)
    {
        shown = route;
        if (!visible) nextScan = 0;
        visible = true;
        foreach (var l in lines) l.For = null;
    }
    public void Hide()
    {
        visible = false;
        foreach (var l in lines) if (l.Line != null) l.Line.enabled = false;
    }

    public string Describe()
    {
        var sb = new System.Text.StringBuilder($"visible {visible} points {shown?.Count ?? 0} lines {lines.Count}");
        foreach (var l in lines)
            sb.Append($"; {(l.Cam != null ? Diag.PathOf(l.Cam.transform) : "-")} at {(l.Cam != null ? Diag.V(l.Cam.transform.position) + " rot " + Diag.V(l.Cam.transform.eulerAngles) + $" clip {l.Cam.nearClipPlane:F1}..{l.Cam.farClipPlane:F1}" : "")} first {(l.Line != null && l.Line.positionCount > 0 ? Diag.V(l.Line.GetPosition(0)) : "-")} camOn {(l.Cam != null && l.Cam.isActiveAndEnabled)} rt {(l.Cam != null && l.Cam.targetTexture != null ? l.Cam.targetTexture.name : "-")} lineOn {(l.Line != null && l.Line.enabled)} n {(l.Line != null ? l.Line.positionCount : 0)} w {(l.Line != null ? l.Line.startWidth : 0):F1}");
        return sb.ToString();
    }

    // 64x32, along x: a cyan band with a dark rim, a white chevron pointing to +x with a dark outline
    static Texture2D Arrows()
    {
        if (arrows != null) return arrows;
        const int W = 64, H = 32;
        var t = new Texture2D(W, H, TextureFormat.RGBA32, false, false) { name = "CloudpunkVR_RouteArrows", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontUnloadUnusedAsset };
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float v = Mathf.Abs((y + 0.5f) / H - 0.5f) * 2;          // 0 centre .. 1 edge
                float u = (x + 0.5f) / W;
                // chevron: the line from the tip (u .72, centre) back to (u .38, edges)
                float along = u - (0.72f - v * 0.34f);
                float d = Mathf.Abs(along) * W;                           // pixels from the chevron's stroke
                Color c = new(RouteColor.r, RouteColor.g, RouteColor.b, 0.92f);
                if (v > 0.82f) c = new Color(0.02f, 0.10f, 0.16f, 0.95f); // rim
                if (v < 0.86f)
                {
                    if (d < 5f) c = Color.white;
                    else if (d < 7.5f) c = new Color(0.02f, 0.12f, 0.2f, 1f);
                }
                int i = (y * W + x) * 4;
                px[i] = (byte)(c.r * 255); px[i + 1] = (byte)(c.g * 255); px[i + 2] = (byte)(c.b * 255); px[i + 3] = (byte)(c.a * 255);
            }
        t.LoadRawTextureData(px);
        t.Apply(false, true);
        arrows = t;
        return t;
    }

    // Every map camera. Finding them walks every camera object the game has (about 4 ms), so it is done when the
    // route is shown, when the player gets in or out of the cockpit (the dashboard's map camera comes and goes with
    // it) and otherwise every 15 s.
    void Ensure(float now)
    {
        lines.RemoveAll(l => { if (l.Cam != null && l.Line != null) return false; if (l.Line != null) Object.Destroy(l.Line.gameObject); return true; });
        bool cockpit = Global.Instance != null && Global.IsInVehicle && Global.IsInVehicleCockpit;
        if (cockpit != scannedCockpit) { scannedCockpit = cockpit; nextScan = 0; }
        if (now < nextScan) return;
        nextScan = now + 15f;
        var sw = DevCommands.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        try { Scan(); }
        finally { if (sw != null && sw.Elapsed.TotalMilliseconds > 1) Plugin.L.LogInfo($"coo: route line camera scan took {sw.Elapsed.TotalMilliseconds:F1} ms"); }
    }

    void Scan()
    {
        int layer = LayerMask.NameToLayer("Minimap");
        if (layer < 0) return;
        var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Sprites/Default");
        foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
        {
            if (!cam.gameObject.scene.IsValid() || cam.cullingMask != (1 << layer) || !cam.name.StartsWith("MiniMapCamera")) continue;
            if (lines.Exists(l => l.Cam == cam)) continue;
            var go = new GameObject("CooRoute_" + cam.name) { layer = layer };
            Object.DontDestroyOnLoad(go);
            var lr = go.AddComponent<LineRenderer>();
            var mat = new Material(shader) { mainTexture = Arrows(), hideFlags = HideFlags.DontUnloadUnusedAsset };
            // the particle shader doubles the tint, and the linear colour space linearises it: 0.7354 reads as neutral
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", new Color(0.7354f, 0.7354f, 0.7354f, 0.5f));
            lr.sharedMaterial = mat;
            lr.textureMode = LineTextureMode.Tile;
            lr.useWorldSpace = true;
            lr.numCapVertices = 2;
            lr.numCornerVertices = 3;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.alignment = LineAlignment.TransformZ;
            go.transform.rotation = Quaternion.Euler(90, 0, 0);   // flat, facing the camera above
            lr.startColor = lr.endColor = Color.white;
            lr.enabled = false;
            lines.Add(new MapLine { Cam = cam, Line = lr, Mat = mat });
            Plugin.L.LogInfo($"coo: route line for {Diag.PathOf(cam.transform)} (into {(cam.targetTexture != null ? cam.targetTexture.name : "the screen")})");
        }
    }

    public void Update(List<Vector3> route, int progress)
    {
        if (!visible || route == null || route.Count < 2) return;
        float now = Time.unscaledTime;
        Ensure(now);
        var player = CooGame.PlayerPos();
        foreach (var l in lines)
        {
            var cam = l.Cam;
            // the camera object, not its enabled flag: Perf switches map cameras off on the frames it skips
            if (cam == null || !cam.gameObject.activeInHierarchy) { if (l.Line != null) l.Line.enabled = false; continue; }
            // just under the camera, inside its clip range
            float y = cam.transform.position.y - cam.nearClipPlane - 2f;
            if (!ReferenceEquals(l.For, route) || l.ForProgress != progress || Mathf.Abs(l.ForY - y) > 0.01f || !(l.ForY == l.ForY))
            {
                pts.Clear();
                pts.Add(new Vector3(player.x, y, player.z));
                for (int i = progress + 1; i < route.Count; i++) pts.Add(new Vector3(route[i].x, y, route[i].z));
                l.Line.positionCount = pts.Count;
                for (int i = 0; i < pts.Count; i++) l.Line.SetPosition(i, pts[i]);
                l.For = route;
                l.ForProgress = progress;
                l.ForY = y;
            }
            else l.Line.SetPosition(0, new Vector3(player.x, y, player.z));
            // a constant width on screen: proportional to how much of the world the map shows
            float span = cam.orthographic ? cam.orthographicSize : (cam.transform.position.y - player.y) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float w = Mathf.Max(1.5f, span * 0.045f);
            l.Line.startWidth = l.Line.endWidth = w;
            // one chevron every few widths, flowing toward the destination
            l.Mat.mainTextureScale = new Vector2(1f / (w * ChevronEvery), 1);
            l.Mat.mainTextureOffset = new Vector2(-now * FlowSpeed % 1f, 0);
            l.Line.enabled = true;
        }
    }
}
