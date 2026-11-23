using System;
using System.Text.Json.Nodes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR.Coo;

// 可缇 on the game's own call channel. The game shows a call in two places: in the cockpit on the dashboard screen left
// of the wheel (a world-space canvas inside the car interior copy, Global/_Vehicle_Interior_Local/Canvas/
// Dialog_Interior_Global) and otherwise in the HUD at the lower left (Global/UI/Canvas-UI/HUD/Dialog/Canvas_Group_
// Exterior_Global). Both are copied once into boxes of our own (the copies carry no DialogHUD, so the game's dialogue
// logic never touches them): the portrait shows the VTube Studio picture received over Spout through the game's
// "Dialog UI Holo" material (the pixelation, scan lines and glitches of the NPC portraits; each box has its own copy,
// because the game drives the shared one's `_Phase` to 0 when an NPC call fades out, which would blank 可缇 too, and
// each copy's phase follows its box's fade, the way an NPC call materialises), the name reads 可缇 and the
// text is her current line from the vtuber World's subtitles, typed out over its speaking time. The game's CJK fonts are
// subsets (no 缇, for one) and TextMesh Pro here cannot make a fallback from a Windows font, so the name and the line
// are drawn with uGUI text on the system font in the same places, colours and sizes, at three times the resolution.
// On foot in VR a third copy sits on the back of the left wrist.
//
// When an NPC calls or talks (the game's own dialogue box is up) the channel is the NPC's: our boxes hide and the
// World hears about it, so it can tell 可缇 she was cut off. Her voice is not ours to stop.
internal sealed class CooChannel
{
    sealed class Box
    {
        public GameObject Root;
        public CanvasGroup Group;
        public RawImage Portrait;
        public Material HoloMaterial;
        public TMP_Text Name, Text;       // the game's text fields, hidden; only their place and style are used
        public Text NameOut, LineOut;
        public float Alpha;
    }

    static readonly int PhaseId = Shader.PropertyToID("_Phase");
    static readonly Color NameColor = new(0.45f, 0.82f, 1f, 1f);
    readonly CooSpout spout;
    readonly CooBridge bridge;
    Box exterior, interior, wrist;
    readonly CooWristScreen wristScreen = new();
    bool wristWanted;
    bool built, failed;
    int interiorOwner;

    // from the World
    public bool OnAir;
    public bool InCar = true, OnFoot = true, OnWrist = true, Holo = true, PopOut = true;
    public readonly CooHoloFigure Figure;
    public string DisplayName = "可缇";

    string line = "";
    float lineStart, lineSpeak, lineUntil;
    bool busy;
    public string Where { get; private set; } = "off";

    public CooChannel(CooSpout spout, CooBridge bridge)
    {
        this.spout = spout;
        this.bridge = bridge;
        Figure = new CooHoloFigure(spout);
    }

    public bool Speaking => Time.unscaledTime < lineStart + lineSpeak + 0.3f && line.Length > 0;

    public void Say(string text, float seconds)
    {
        line = (text ?? "").Trim();
        lineStart = Time.unscaledTime;
        lineSpeak = Mathf.Max(0.3f, seconds);
        lineUntil = lineStart + lineSpeak + Mathf.Clamp(line.Length * 0.06f, 2f, 5f);
    }

    public void Cut()
    {
        lineUntil = Mathf.Min(lineUntil, Time.unscaledTime + 1.5f);
        lineSpeak = Mathf.Min(lineSpeak, Time.unscaledTime - lineStart);
    }

    // ---- building the copies

    void Build()
    {
        var g = Global.Instance;
        var d = g != null ? g.dialogUI : null;
        if (d == null || d.exteriorCanvasGroupGlobal == null) return;
        // the HUD copy sits where the game's dialogue box sits, next to it under the HUD
        var dialogRt = d.transform.TryCast<RectTransform>();
        exterior = Copy(d.exteriorCanvasGroupGlobal.gameObject, d.transform.parent, "CloudpunkVR_CortiCall");
        var er = exterior.Root.transform.TryCast<RectTransform>();
        if (dialogRt != null && er != null)
        {
            er.anchorMin = dialogRt.anchorMin;
            er.anchorMax = dialogRt.anchorMax;
            er.pivot = dialogRt.pivot;
            er.sizeDelta = dialogRt.sizeDelta;
            er.anchoredPosition = dialogRt.anchoredPosition;
            er.localScale = dialogRt.localScale;
        }
        exterior.Root.transform.SetSiblingIndex(d.transform.GetSiblingIndex());   // before (under) the game's dialogue: an NPC's box draws over 可缇's
        foreach (var junk in new[] { "Trade Offers", "Buttons" }) Destroy(exterior.Root.transform.Find(junk));
        exterior.Portrait = ToRaw(exterior.Root.transform.Find("Portrait/Image"), out exterior.HoloMaterial);
        exterior.Name = exterior.Root.transform.Find("Name")?.GetComponent<TMP_Text>();
        exterior.Text = exterior.Root.transform.Find("Text/Text")?.GetComponent<TMP_Text>();
        Prepare(exterior);

        // the wrist screen: an upright screen of its own (CooWristScreen), 可缇 on top, the line below
        var holo = exterior.HoloMaterial != null ? new Material(exterior.HoloMaterial) { hideFlags = HideFlags.DontUnloadUnusedAsset } : null;
        wristScreen.Build(font, NameColor, holo);
        wrist = new Box { Root = wristScreen.CanvasRoot, Group = wristScreen.Group, Portrait = wristScreen.Portrait, HoloMaterial = holo, NameOut = wristScreen.Name, LineOut = wristScreen.Line };
        built = true;
        Plugin.L.LogInfo($"corti: call boxes ready (hud {Diag.PathOf(exterior.Root.transform)}, holo material {Diag.Name(exterior.HoloMaterial)})");
    }

    // The interior copy is rebuilt whenever the game swaps the car interior (another car, a new scene).
    void BuildInterior()
    {
        var g = Global.Instance;
        var grp = g != null && g.dialogUI != null ? g.dialogUI.interiorCanvasGroupGlobal : null;
        if (grp == null) return;
        int owner = grp.GetInstanceID();
        if (interior != null && interior.Root != null && owner == interiorOwner) return;
        if (interior?.Root != null) UnityEngine.Object.Destroy(interior.Root);
        interiorOwner = owner;
        Darken(grp.transform.Find("Dialog_Interior"));
        interior = Copy(grp.gameObject, grp.transform.parent, "CloudpunkVR_CortiCall");
        interior.Root.transform.SetSiblingIndex(grp.transform.GetSiblingIndex());   // under the NPC's box
        var inner = interior.Root.transform.Find("Dialog_Interior");
        if (inner == null) return;
        foreach (var cg in inner.GetComponents<CanvasGroup>()) cg.alpha = 1;
        interior.Portrait = ToRaw(inner.Find("Interior_Portrait_Image"), out interior.HoloMaterial);
        interior.Name = inner.Find("Interior_Name")?.GetComponent<TMP_Text>();
        interior.Text = inner.Find("Interior_Text")?.GetComponent<TMP_Text>();
        Prepare(interior);
    }

    public static float BackingAlpha = 0.82f, ScanlineAlpha = 0.45f;

    // The cockpit screen's text sits on a scan-line texture with the city showing through it (hard to read, a
    // screen-door look in the headset): a dark plate behind the portrait and the text, the scan lines made fainter.
    static void Darken(Transform inner)
    {
        if (inner == null || inner.Find("CloudpunkVR_Backing") != null) return;
        var scan = inner.Find("Scanlines Background (2)");
        var go = new GameObject("CloudpunkVR_Backing");
        go.layer = inner.gameObject.layer;
        go.transform.SetParent(inner, false);
        go.transform.SetSiblingIndex(0);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(-8, -60);   // the text runs below the group's own rectangle
        rt.offsetMax = new Vector2(8, 6);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.01f, 0.02f, 0.035f, BackingAlpha);
        img.raycastTarget = false;
        if (scan != null)
        {
            var si = scan.GetComponent<Image>();
            if (si != null) { var c = si.color; c.a *= ScanlineAlpha; si.color = c; }
        }
        Plugin.L.LogInfo($"corti: darker cockpit screen under {Diag.PathOf(inner)}");
    }

    public string Debug()
    {
        var sb = new System.Text.StringBuilder();
        if (interior?.Portrait != null)
        {
            var c = CooHoloFigure.WorldCorners(interior.Portrait.rectTransform);
            sb.Append($"portrait corners {Diag.V(c[0])} {Diag.V(c[1])} {Diag.V(c[2])} {Diag.V(c[3])}");
        }
        var vi = Global.Instance?.vehicleInterior;
        if (vi != null)
        {
            var eye = vi.cameraCockpit != null ? vi.cameraCockpit.transform : vi.interiorCameraTransform;
            sb.Append($" eye {Diag.V(eye.position)} socket {Diag.V(vi.interiorCameraTransform.position)} interiorLocal {Diag.V(vi.interiorLocal.position)} scale {Diag.V(vi.interiorLocal.lossyScale)} fwd {Diag.V(vi.interiorLocal.forward)}");
        }
        sb.Append($" main {Diag.V(Global.Instance.mainCamera.transform.position)}");
        return sb.ToString();
    }

    static Box Copy(GameObject source, Transform parent, string name)
    {
        var go = UnityEngine.Object.Instantiate(source, parent, false);
        go.name = name;
        go.SetActive(true);
        var b = new Box { Root = go, Group = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>() };
        b.Group.alpha = 0;
        b.Group.interactable = false;
        b.Group.blocksRaycasts = false;
        return b;
    }

    static Font font;

    void Prepare(Box b)
    {
        font ??= UiFont.Os(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei" }, 32);
        b.NameOut = Overlay(b.Name, false);
        b.LineOut = Overlay(b.Text, true);
        if (b.NameOut != null)
        {
            b.NameOut.text = DisplayName;
            // sky blue with a soft glow, apart from the NPCs' orange names
            b.NameOut.color = NameColor;
            var glow = b.NameOut.gameObject.AddComponent<Outline>();
            glow.effectColor = new Color(0.15f, 0.55f, 0.95f, 0.55f);
            glow.effectDistance = new Vector2(2.5f, -2.5f);
        }
    }

    // A uGUI text in the place of a TextMesh Pro field (which stays, switched off): same rectangle, colour and size,
    // drawn three times larger and scaled down so the system font's glyphs stay sharp on the small screens.
    static Text Overlay(TMP_Text t, bool body)
    {
        if (t == null) return null;
        const float k = 3f;
        foreach (var cg in t.GetComponents<CanvasGroup>()) cg.alpha = 1;
        var src = t.rectTransform;
        var go = new GameObject("CloudpunkVR_Text");
        go.layer = t.gameObject.layer;
        go.transform.SetParent(t.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = src.pivot;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = src.rect.size * k;
        rt.localScale = Vector3.one / k;
        var text = go.AddComponent<Text>();
        text.font = font;
        int size = Mathf.RoundToInt(t.fontSize * k);
        text.fontSize = size;
        var col = t.color;
        col.a = 1;   // the game fades its text out between calls
        text.color = col;
        text.alignment = TextAnchor.UpperLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = body ? VerticalWrapMode.Truncate : VerticalWrapMode.Overflow;
        text.supportRichText = false;
        text.raycastTarget = false;
        if (body)
        {
            // a long line shrinks to fit the box rather than running out of it
            text.resizeTextForBestFit = true;
            text.resizeTextMaxSize = size;
            text.resizeTextMinSize = Mathf.RoundToInt(size * 0.6f);
        }
        t.enabled = false;
        return text;
    }

    // The portrait is an Image (sprites); our picture is a render texture, so the Image becomes a RawImage with the
    // same material and colour.
    static RawImage ToRaw(Transform t, out Material holo)
    {
        holo = null;
        if (t == null) return null;
        var img = t.GetComponent<Image>();
        var color = Color.white;
        if (img != null)
        {
            // a copy: the game animates the shared material's _Phase with its own calls
            holo = img.material != null ? new Material(img.material) { name = "CloudpunkVR_CortiHolo", hideFlags = HideFlags.DontUnloadUnusedAsset } : null;
            color = img.color;
            UnityEngine.Object.DestroyImmediate(img);
        }
        var raw = t.gameObject.AddComponent<RawImage>();
        color.a = 1;   // copied while no call was up, the portrait may be faded out
        raw.color = color;
        raw.raycastTarget = false;
        raw.material = holo;
        return raw;
    }

    static void Destroy(Transform t)
    {
        if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
    }

    static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++) SetLayer(go.transform.GetChild(i).gameObject, layer);
    }

    // ---- every frame

    // After the game's own Update: is an NPC on the channel?
    public void Update()
    {
        var g = Global.Instance;
        if (g == null || g.dialogUI == null) return;
        if (!built && !failed)
        {
            try { Build(); }
            catch (Exception e) { failed = true; Plugin.L.LogError("corti: call boxes unavailable: " + e); }
        }
        if (!built) return;
        try { BuildInterior(); }
        catch (Exception e) { Plugin.L.LogWarning("corti: interior box: " + e.Message); }

        // the wrist screen's contents are drawn here, outside rendering; BeforeRender only places it
        Paint(wrist, wristWanted, Time.unscaledTime, Time.unscaledDeltaTime);
        wristScreen.Render(wrist != null ? wrist.Alpha : 0);

        var d = g.dialogUI;
        // an NPC on the channel: the dialogue is up, or one of its boxes shows at all (a call can come while the
        // dialogue object is already active, only its boxes fading in)
        bool npc = d.gameObject.activeInHierarchy && (d.IsActiveAndNotFadingOut()
            || d.exteriorCanvasGroupGlobal != null && d.exteriorCanvasGroupGlobal.alpha > 0.02f && d.exteriorCanvasGroupGlobal.gameObject.activeInHierarchy
            || d.interiorCanvasGroupGlobal != null && d.interiorCanvasGroupGlobal.alpha > 0.02f && d.interiorCanvasGroupGlobal.gameObject.activeInHierarchy);
        if (npc != busy)
        {
            busy = npc;
            if (OnAir && bridge.Connected)
            {
                string speaker = "";
                try { speaker = CooGame.Dialogue()?["speaker"]?.ToString() ?? ""; } catch { }
                bridge.SendEvent("channel", new JsonObject { ["busy"] = npc, ["speaker"] = speaker, ["speaking"] = Speaking });
            }
            Plugin.L.LogInfo($"corti: channel {(npc ? "taken by an NPC" : "free")}");
        }
    }

    // Before rendering: which box shows, its picture and its line.
    public void BeforeRender(VRCameras cams)
    {
        if (!built) return;
        var g = Global.Instance;
        float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
        string mode = CooGame.Mode();
        bool on = OnAir && !busy && (spout.Live || now < lineUntil);
        bool cockpit = Global.IsInVehicle && Global.IsInVehicleCockpit && mode == "car";
        bool vr = UnityEngine.XR.XRSettings.enabled;
        bool showInterior = on && InCar && cockpit && interior?.Root != null;
        bool showExterior = on && OnFoot && !cockpit && (mode == "foot" || mode == "car_chase" || mode == "car" || mode == "window");
        bool showWrist = on && OnWrist && vr && mode == "foot" && cams != null && cams.Left.Valid && cams.MainPoseValid;
        bool wristTest = on && DevCommands.WristTest && !vr;
        showWrist |= wristTest;
        Where = !OnAir ? "off" : busy ? "npc" : !on ? "idle" : showInterior ? "car_screen" : showWrist && showExterior ? "hud+wrist" : showExterior ? "hud" : showWrist ? "wrist" : "hidden";

        if (on && spout.Live) spout.Render();
        Paint(interior, showInterior, now, dt);
        Paint(exterior, showExterior, now, dt);
        // in the cockpit she also stands out of the screen as a hologram
        Figure.Update(interior?.Portrait != null ? interior.Portrait.rectTransform : null, showInterior && PopOut, dt);
        wristWanted = showWrist;
        if (wristTest)
        {
            var mc = g.mainCamera.transform;
            wristScreen.Place(mc.position + mc.forward * 0.35f + mc.up * 0.12f, Quaternion.LookRotation(mc.forward, mc.up), WristWidth, wrist.Alpha);
        }
        else if (showWrist || wrist != null && wrist.Alpha > 0 && cams != null && cams.Left.Valid) PlaceOnWrist(cams);
        else wristScreen.Hide();
    }

    void Paint(Box b, bool show, float now, float dt)
    {
        if (b == null || b.Root == null) return;
        b.Alpha = busy ? 0 : Mathf.MoveTowards(b.Alpha, show ? 1 : 0, dt / 0.25f);   // an NPC takes the channel at once
        if (b.Group != null && !Mathf.Approximately(b.Group.alpha, b.Alpha)) b.Group.alpha = b.Alpha;
        if (b.Alpha <= 0) return;
        if (b.Portrait != null)
        {
            Texture tex = spout.Box != null ? spout.Box : Texture2D.blackTexture;
            if (b.Portrait.texture != tex) b.Portrait.texture = tex;
            var mat = Holo ? b.HoloMaterial : null;
            if (b.Portrait.material != mat && !(mat == null && b.Portrait.material == b.Portrait.defaultMaterial)) b.Portrait.material = mat;
            // the picture materialises with the box, like an NPC call
            if (mat != null && mat.HasProperty(PhaseId)) mat.SetFloat(PhaseId, b.Alpha);
        }
        if (b.NameOut != null && b.NameOut.text != DisplayName) b.NameOut.text = DisplayName;
        if (b.LineOut != null)
        {
            string want = now < lineUntil ? line : "";
            // typed out over the speaking time, like the game's teletype
            int n = want.Length == 0 ? 0 : lineSpeak <= 0 ? want.Length : Mathf.Clamp(Mathf.CeilToInt((now - lineStart) / lineSpeak * want.Length), 0, want.Length);
            string shown = want.Substring(0, n);
            if (b.LineOut.text != shown) b.LineOut.text = shown;
        }
    }

    // Standing on the back of the left forearm like a fin, fixed to the arm: the hand frame is the controller's pose
    // times the calibrated grip (the same the first-person hand uses), so it moves rigidly with the arm and never turns
    // toward the head by itself. Raise the forearm across the chest as if reading a watch and the screen faces the
    // eyes, the portrait toward the elbow and the line toward the wrist.
    public static float WristAlong = 0.11f;   // centre behind the wrist joint, along the forearm, metres
    public static float WristLift = 0.105f;   // centre above the forearm's back (arm half-thickness + half the screen)
    public static float WristWidth = 0.11f;   // screen width along the forearm; 1.3 times as tall

    void PlaceOnWrist(VRCameras cams)
    {
        var grip = cams.ToWorld(cams.Left.Pos);
        var hand = cams.ToWorld(cams.Left.Rot) * VRBody.Arm.Grip(-1);
        var fingers = hand * Vector3.forward;       // wrist to knuckles, along the forearm
        var back = hand * Vector3.left;             // back of the left hand (hand frame: x times side)
        var wristJoint = grip - fingers * VRBody.Arm.WristBack;
        wristScreen.Place(wristJoint - fingers * WristAlong + back * WristLift, Quaternion.LookRotation(Vector3.Cross(fingers, back), back), WristWidth, wrist.Alpha);
    }

    public JsonObject Describe() => new()
    {
        ["where"] = Where,
        ["popOut"] = Figure.Visible,
        ["npc"] = busy,
        ["speaking"] = Speaking,
        ["spout"] = spout.Live ? new JsonObject { ["sender"] = spout.Sender, ["width"] = spout.Width, ["height"] = spout.Height, ["format"] = spout.Format } : null,
        ["problem"] = spout.Live ? null : spout.Problem,
    };
}
