using System;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR;

// The city is stacked in layers and each layer limits how high and how low the car may fly (PlayerCar.layerMinY /
// layerMaxY). The flat game shows nothing when the car reaches a limit; it just stops climbing or sinking, which in
// the headset feels like the controls going dead. This shows it where the driver looks: a band of chevrons across the
// top of the windshield at the ceiling (pointing down) or just above the dashboard at the floor (pointing up), amber
// while approaching it with the stick pushed toward it, red and pulsing while pressed against it, with a short buzz
// in both hands on arrival. It is part of the cockpit (on the interior's layer, drawn by the interior camera), so
// both eyes see it on the glass. In the chase view and in flat mode the same band sits at the top or bottom of the
// game's HUD.
internal sealed class VRAltitude
{
    // Within this many metres of a limit, with the stick or the car's motion going toward it, the band shows
    // (at full climb the car rises 10 m/s, so about a second ahead).
    public static float Band = 10f;
    // After the stick lets go at a limit the band stays dim this long, then fades.
    const float Linger = 1.5f;
    public static float LastClimb;   // climb input sent to the game this frame (VRControls, Coo's autopilot)

    sealed class Strip
    {
        public GameObject Root;
        public Canvas Canvas;
        public RawImage Chevrons, Glow;
        public Text Label;
    }

    Strip cockpit, hud;
    Texture2D chevDown, chevUp, glowTex;
    Font font;
    int interiorLayer = -1;
    float alpha, shownSign, lastBuzz, lastPush = -10;
    bool wasAt;
    static string state = "off";

    public static string Describe() => state;

    void Build()
    {
        font = UiFont.Os(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" }, 40);
        chevDown = MakeChevrons(false);
        chevUp = MakeChevrons(true);
        glowTex = MakeGlow();
        cockpit = MakeStrip("CloudpunkVR_Altitude_Cockpit", RenderMode.WorldSpace);
        hud = MakeStrip("CloudpunkVR_Altitude_HUD", RenderMode.ScreenSpaceCamera);
        cockpit.Root.SetActive(false);
        hud.Root.SetActive(false);
    }

    // Once per frame before rendering (VR and flat).
    public void Update(SteamVRInput vr)
    {
        var g = Global.Instance;
        var car = g != null ? g.playerCar : null;
        bool inCar = g != null && Global.IsInVehicle && car != null && car.gameObject.activeInHierarchy && !g.IsInMenu() && !SceneLoader.IsLoading();
        float want = 0, sign = 0;
        float y = 0, top = 0, bottom = 0, vy = 0;
        bool at = false;
        if (inCar && car.layerMaxY > car.layerMinY)
        {
            // The limits apply to the car's floor plane (it hovers a couple of metres above it), not to its origin:
            // measured, the floor plane stops at layerMaxY while the car sits 2.6 m higher.
            y = car.currentFloorLevel; top = car.layerMaxY; bottom = car.layerMinY;
            vy = car.rBody != null ? car.rBody.velocity.y : 0;
            float toTop = top - y, toBottom = y - bottom;
            bool up = LastClimb > 0.25f || vy > 0.6f, down = LastClimb < -0.25f || vy < -0.6f;
            if (toTop < Band && up) { want = Mathf.Clamp01(1 - toTop / Band); sign = 1; at = toTop < 0.5f && LastClimb > 0.25f; }
            else if (toBottom < Band && down) { want = Mathf.Clamp01(1 - toBottom / Band); sign = -1; at = toBottom < 0.5f && LastClimb < -0.25f; }
            if (at) { want = 1; lastPush = Time.unscaledTime; }
            // just let go at the limit: stay dim for a moment instead of vanishing at once
            else if (want == 0 && Time.unscaledTime - lastPush < Linger && (toTop < 0.5f || toBottom < 0.5f)) want = 0.35f;
        }
        if (sign != 0) shownSign = sign;
        float dt = Time.unscaledDeltaTime;
        alpha = Mathf.MoveTowards(alpha, want, dt * (want > alpha ? 4f : 1.5f));
        if (at && !wasAt || at && Time.unscaledTime - lastBuzz > 1.5f)
        {
            lastBuzz = Time.unscaledTime;
            vr?.Rumble(0.06f, 0.45f);
        }
        wasAt = at;
        state = alpha <= 0.01f ? $"off (y {y:F1} in [{bottom:F1},{top:F1}])" : $"{(shownSign > 0 ? "ceiling" : "floor")} {alpha:F2}{(at ? " at limit" : "")} (y {y:F1} in [{bottom:F1},{top:F1}], vy {vy:F1})";

        if (alpha <= 0.01f)
        {
            if (cockpit != null && cockpit.Root.activeSelf) cockpit.Root.SetActive(false);
            if (hud != null && hud.Root.activeSelf) hud.Root.SetActive(false);
            return;
        }
        if (cockpit == null) Build();
        bool inCockpit = Global.IsInVehicleCockpit && PlaceInCockpit(g, shownSign);
        bool onHud = !inCockpit && g.uiCamera != null && PlaceOnHud(g, shownSign);
        if (cockpit.Root.activeSelf != inCockpit) cockpit.Root.SetActive(inCockpit);
        if (hud.Root.activeSelf != onHud) hud.Root.SetActive(onHud);
        if (inCockpit) Paint(cockpit, shownSign, at);
        if (onHud) Paint(hud, shownSign, at);
    }

    void Paint(Strip s, float sign, bool at)
    {
        float t = Time.unscaledTime;
        // amber while approaching, red when pressed against the limit; the chevrons scroll toward the allowed side
        var col = at ? new Color(1f, 0.25f, 0.18f) : new Color(1f, 0.72f, 0.2f);
        float pulse = at ? 0.65f + 0.35f * Mathf.Sin(t * Mathf.PI * 4) : 0.85f + 0.15f * Mathf.Sin(t * Mathf.PI * 2);
        float a = alpha * pulse;
        s.Chevrons.texture = sign > 0 ? chevDown : chevUp;
        s.Chevrons.color = new Color(col.r, col.g, col.b, a);
        s.Chevrons.uvRect = new Rect(0, 0, 12, 1);
        s.Glow.color = new Color(col.r, col.g, col.b, a * 0.35f);
        s.Label.text = sign > 0 ? "高度上限" : "高度下限";
        s.Label.color = new Color(1f, 0.95f, 0.88f, Mathf.Clamp01(a * 1.2f));
    }

    bool PlaceInCockpit(Global g, float sign)
    {
        var vi = g.vehicleInterior;
        if (vi == null || vi.interiorCameraTransform == null || vi.interiorLocal == null) return false;
        if (interiorLayer < 0 || cockpit.Root.transform.parent != vi.interiorLocal)
        {
            interiorLayer = LayerMask.NameToLayer("PlayerCar");
            cockpit.Root.transform.SetParent(vi.interiorLocal, false);
            SetLayer(cockpit.Root, interiorLayer);
        }
        var eye = vi.cameraCockpit != null ? vi.cameraCockpit.transform : vi.interiorCameraTransform;
        var fwd = Vector3.ProjectOnPlane(vi.interiorLocal.forward, vi.interiorLocal.up).normalized;
        var up = vi.interiorLocal.up;
        // the top of the windshield for the ceiling, just above the dashboard for the floor
        var pos = eye.position + fwd * CockpitDistance + up * (sign > 0 ? CockpitTop : CockpitBottom);
        var t = cockpit.Root.transform;
        t.position = pos;
        t.rotation = Quaternion.LookRotation(pos - eye.position, up);
        t.localScale = Vector3.one * CockpitScale;
        return true;
    }

    // Dev-tunable placement (altcfg).
    public static float CockpitDistance = 0.8f, CockpitTop = 0.17f, CockpitBottom = 0.0f, CockpitScale = 0.0006f;

    bool PlaceOnHud(Global g, float sign)
    {
        if (hud.Canvas.worldCamera != g.uiCamera)
        {
            hud.Canvas.worldCamera = g.uiCamera;
            SetLayer(hud.Root, LayerMask.NameToLayer("UI"));
        }
        var r = hud.Root.GetComponent<RectTransform>();
        foreach (var child in new Graphic[] { hud.Chevrons, hud.Glow, hud.Label })
        {
            var cr = child.rectTransform;
            cr.anchorMin = cr.anchorMax = new Vector2(0.5f, sign > 0 ? 0.86f : 0.2f);
        }
        return true;
    }

    Strip MakeStrip(string name, RenderMode mode)
    {
        var s = new Strip { Root = new GameObject(name) };
        UnityEngine.Object.DontDestroyOnLoad(s.Root);
        s.Canvas = s.Root.AddComponent<Canvas>();
        s.Canvas.renderMode = mode;
        var scaler = s.Root.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = mode == RenderMode.WorldSpace ? 4 : 1;
        s.Root.GetComponent<RectTransform>().sizeDelta = new Vector2(900, 120);
        if (mode == RenderMode.ScreenSpaceCamera) { s.Canvas.sortingOrder = 60; s.Canvas.planeDistance = 60; }
        s.Glow = Img(s.Root.transform, "Glow", glowTex, Vector2.zero, new Vector2(900, 120));
        s.Chevrons = Img(s.Root.transform, "Chevrons", null, Vector2.zero, new Vector2(820, 46));
        s.Label = Txt(s.Root.transform, "Label", new Vector2(0, 0), new Vector2(300, 60), 36);
        // the label sits on a dark plate in the middle of the band
        var plate = Img(s.Root.transform, "Plate", null, Vector2.zero, new Vector2(230, 56));
        plate.color = new Color(0.05f, 0.02f, 0.02f, 0.7f);
        plate.transform.SetSiblingIndex(s.Label.transform.GetSiblingIndex());
        return s;
    }

    static RawImage Img(Transform parent, string name, Texture tex, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<RawImage>();
        img.texture = tex;
        img.raycastTarget = false;
        var r = img.rectTransform;
        r.anchoredPosition = pos;
        r.sizeDelta = size;
        return img;
    }

    Text Txt(Transform parent, string name, Vector2 pos, Vector2 size, int fontSize)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<Text>();
        t.font = font;
        t.fontSize = fontSize;
        t.alignment = TextAnchor.MiddleCenter;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        var r = t.rectTransform;
        r.anchoredPosition = pos;
        r.sizeDelta = size;
        return t;
    }

    // One chevron per tile (the strip repeats it 12 times), pointing down or up, with soft edges.
    static Texture2D MakeChevrons(bool pointUp)
    {
        const int w = 32, h = 32;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, hideFlags = HideFlags.DontUnloadUnusedAsset };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w - 0.5f, v = (y + 0.5f) / h;
                if (pointUp) v = 1 - v;
                // a V with its point at the bottom (flipped for up): distance from the line v = 0.2 + |u| * 1.2
                float d = Mathf.Abs(v - (0.2f + Mathf.Abs(u) * 1.2f));
                float a = Mathf.Clamp01((0.09f - d) / 0.035f) * (Mathf.Abs(u) < 0.42f ? 1 : 0);
                t.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        t.Apply();
        return t;
    }

    static Texture2D MakeGlow()
    {
        const int w = 64, h = 16;
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset };
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w * 2 - 1, v = (y + 0.5f) / h * 2 - 1;
                float a = Mathf.Clamp01(1 - Mathf.Pow(Mathf.Abs(u), 6)) * Mathf.Clamp01(1 - v * v);
                t.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        t.Apply();
        return t;
    }

    static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++) SetLayer(go.transform.GetChild(i).gameObject, layer);
    }
}
