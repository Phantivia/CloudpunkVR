using UnityEngine;
using UnityEngine.XR;

namespace CloudpunkVR;

// The game's 2D UI (Screen Space - Camera canvases drawn by Global.uiCamera) is redirected into one mono render
// texture, shown on a floating panel. The panel lives in a private space far below the city and is drawn by our own
// stereo camera after everything else, so it is never hidden by the cockpit and costs one quad per eye.
internal sealed class VRUI
{
    const int Width = 1920, Height = 1080;
    static readonly Vector3 SpaceOrigin = new(0, -6000, 0);

    RenderTexture rt;
    Camera gameUiCam;
    Camera overlay;
    Transform panel;
    int layer = -1;
    float panelYaw, nextCanvasScan, dist = 1.6f, nextTurnLog;
    int untrackedUiCam;

    public bool Ready => overlay != null;

    public void Setup()
    {
        layer = FreeLayer();
        rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "CloudpunkVR_UI" };
        rt.Create();

        var root = new GameObject("CloudpunkVR_UISpace");
        Object.DontDestroyOnLoad(root);
        root.transform.position = SpaceOrigin;

        var camGo = new GameObject("CloudpunkVR_UIOverlayCamera");
        camGo.transform.SetParent(root.transform, false);
        overlay = camGo.AddComponent<Camera>();
        overlay.clearFlags = CameraClearFlags.Depth;
        overlay.cullingMask = 1 << layer;
        overlay.depth = 100;
        overlay.nearClipPlane = 0.05f;
        overlay.farClipPlane = 20f;
        overlay.useOcclusionCulling = false;
        overlay.allowHDR = false;
        overlay.allowMSAA = false;
        XRDevice.DisableAutoXRCameraTracking(overlay, true);
        VRCameras.Instance.Exclude(overlay);

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "CloudpunkVR_UIPanel";
        Object.Destroy(quad.GetComponent<Collider>());
        quad.layer = layer;
        panel = quad.transform;
        panel.SetParent(root.transform, false);
        var mr = quad.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.sharedMaterial = new Material(PanelShader()) { mainTexture = rt };
        Plugin.L.LogInfo($"VR UI ready: layer {layer}, shader {mr.sharedMaterial.shader.name}");
    }

    static Shader PanelShader()
    {
        // The UI render texture holds premultiplied colour; prefer a premultiplied-alpha shader when the build has one.
        foreach (var name in new[] { "Legacy Shaders/Particles/Alpha Blended Premultiply", "Particles/Alpha Blended Premultiply", "UI/Default", "Sprites/Default" })
        {
            var s = Shader.Find(name);
            if (s != null) return s;
        }
        return Shader.Find("Unlit/Transparent");
    }

    // A layer no camera in the scene draws and that has no name; layer names can still read empty this early, so
    // the camera masks decide.
    static int FreeLayer()
    {
        int used = 0;
        foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
            if (cam.cullingMask != -1) used |= cam.cullingMask;
        for (int i = 30; i >= 8; i--)
            if ((used & (1 << i)) == 0 && string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
        for (int i = 30; i >= 8; i--)
            if ((used & (1 << i)) == 0) return i;
        return 30;
    }

    public RenderTexture Texture => rt;
    public bool PanelVisible => panel != null && panel.gameObject.activeSelf;
    // The private space and its layer: other head-anchored things drawn by the panel camera live here (Coo.CooHud).
    public Transform Space => panel != null ? panel.parent : null;
    public int Layer => layer;
    public Transform Panel => panel;

    bool followHead, catchingUp;
    Vector3 anchor;
    bool anchorValid, cleared;

    // menu: a menu, dialog or inventory is open; onFoot: walking (the player turns around freely, so the HUD comes
    // along); otherwise seated in the car, where the HUD stays ahead of the seat.
    public void Update(VRCameras cams, bool menu, bool onFoot)
    {
        if (!Ready) return;
        ClearWhenWorldIsOff();
        followHead = menu;
        var g = Global.Instance;
        var cam = g != null ? g.uiCamera : null;
        if (cam != null && (gameUiCam == null || gameUiCam.GetInstanceID() != cam.GetInstanceID())) gameUiCam = cam;

        if (Time.unscaledTime >= nextCanvasScan)
        {
            nextCanvasScan = Time.unscaledTime + 1f;
            if (gameUiCam != null) RedirectOverlayCanvases(gameUiCam);
        }

        // In the car the panel stays ahead of the seat. On foot and in menus it follows the head lazily: it starts
        // turning once the head is a set angle away and keeps turning until it is nearly straight ahead again, faster
        // the further off it is.
        float target = menu || onFoot ? cams.HeadYaw : 0;
        float off = Mathf.Abs(Mathf.DeltaAngle(panelYaw, target));
        if (!(menu || onFoot)) catchingUp = off > 0.5f;
        else if (off > (menu ? 35f : 22f)) catchingUp = true;
        else if (off < 3f) catchingUp = false;
        if (catchingUp) panelYaw = Mathf.MoveTowardsAngle(panelYaw, target, Mathf.Max(90f, off * 4f) * Time.unscaledDeltaTime);
    }

    // The game's UI camera must draw into our texture with a clear every frame. The game switches it back to its own
    // setup (clear depth only, onto the screen) in places; without a colour clear every moving element leaves trails
    // in the texture, and a UI camera that stops rendering leaves its last frame frozen on the panel.
    void KeepUiCamera()
    {
        var cam = gameUiCam;
        if (cam == null) return;
        // While the game has the camera on the screen (it switches it back in places, getting out of the car among
        // them) Unity's XR tracking turns it with the head, and the turn stays after the camera is back on our
        // texture. The HUD canvas follows the camera and looks right, but the minimap's icons are set to a world
        // rotation of identity on foot (Minimap.DrawRadarMarkers) and stood up out of the map. The camera is never
        // meant to turn: no tracking, rotation back to the game's (identity, as in flat mode).
        if (cam.GetInstanceID() != untrackedUiCam)
        {
            untrackedUiCam = cam.GetInstanceID();
            XRDevice.DisableAutoXRCameraTracking(cam, true);
        }
        var tr = cam.transform;
        if (tr.localRotation != Quaternion.identity)
        {
            float off = Quaternion.Angle(tr.localRotation, Quaternion.identity);
            if (off > 1f && Time.unscaledTime >= nextTurnLog)
            {
                nextTurnLog = Time.unscaledTime + 30f;
                Plugin.L.LogInfo($"UI: the game's UI camera was turned {off:F1}°, set back");
            }
            tr.localRotation = Quaternion.identity;
        }
        if (cam.targetTexture == null || cam.targetTexture.GetInstanceID() != rt.GetInstanceID()) cam.targetTexture = rt;
        if (cam.stereoTargetEye != StereoTargetEyeMask.None) cam.stereoTargetEye = StereoTargetEyeMask.None;
        if (cam.clearFlags != CameraClearFlags.SolidColor) cam.clearFlags = CameraClearFlags.SolidColor;
        if (cam.backgroundColor != Color.clear) cam.backgroundColor = Color.clear;
    }

    // Loading screens and fades use Screen Space - Overlay canvases, which never reach the headset.
    static void RedirectOverlayCanvases(Camera uiCam)
    {
        foreach (var c in Object.FindObjectsOfType<Canvas>())
        {
            if (!c.isRootCanvas || c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
            c.renderMode = RenderMode.ScreenSpaceCamera;
            c.worldCamera = uiCam;
            c.planeDistance = 50;
            Plugin.L.LogInfo($"UI: redirected overlay canvas {Diag.PathOf(c.transform)} into the VR panel");
        }
    }

    // When no camera draws the world into the headset (the full map switches the main camera off) nothing clears the
    // eye buffers: the panel is drawn over the last frame again and again and smears into a stack of copies as the
    // head moves. Then the panel camera clears to black itself. Any other camera drawing to the headset counts as the
    // world, not only the main one: the title screen is drawn by the menu camera (Global/UI/Menu/MenuCamera) while the
    // main camera is off, and checking only the main camera blacked out the title and its 3D menu.
    readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Camera> cameraBuffer = new(64);

    void ClearWhenWorldIsOff()
    {
        bool worldDrawn = false;
        int n = Camera.GetAllCameras(cameraBuffer);
        for (int i = 0; i < n && !worldDrawn; i++)
        {
            var c = cameraBuffer[i];
            if (c == null || c.GetInstanceID() == overlay.GetInstanceID()) continue;
            worldDrawn = c.targetTexture == null && c.stereoTargetEye != StereoTargetEyeMask.None && c.depth < overlay.depth;
        }
        var want = worldDrawn ? CameraClearFlags.Depth : CameraClearFlags.SolidColor;
        if (overlay.clearFlags != want)
        {
            overlay.clearFlags = want;
            overlay.backgroundColor = Color.black;
            if (DevCommands.Enabled) Plugin.L.LogInfo($"UI: {(worldDrawn ? "a camera draws the world" : "no camera draws the world")}, panel camera clears {want}");
        }
    }

    public void BeforeRender(VRCameras cams)
    {
        if (!Ready) return;
        ClearWhenWorldIsOff();
        overlay.transform.localPosition = cams.HeadPos;
        overlay.transform.localRotation = cams.HeadRot;
        KeepUiCamera();

        bool uiDrawn = gameUiCam != null && gameUiCam.isActiveAndEnabled;
        if (!uiDrawn && !cleared)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = prev;
        }
        cleared = !uiDrawn;
        if (panel.gameObject.activeSelf != uiDrawn) panel.gameObject.SetActive(uiDrawn);

        // The panel sits in front of the eyes wherever the head is (seated or standing, leaning), following head
        // movement with a short lag; its facing follows the body, or the head in menus (Update).
        if (!anchorValid) { anchor = cams.HeadPos; anchorValid = true; }
        anchor = Vector3.Lerp(anchor, cams.HeadPos, 1 - Mathf.Exp(-Time.unscaledDeltaTime / 0.15f));
        if ((anchor - cams.HeadPos).sqrMagnitude > 0.5f * 0.5f) anchor = cams.HeadPos;
        float width = Plugin.Cfg.UIWidth.Value;
        float targetDist = followHead ? Mathf.Min(Plugin.Cfg.UIMenuDistance.Value, Plugin.Cfg.UIDistance.Value) : Plugin.Cfg.UIDistance.Value;
        dist = Mathf.MoveTowards(dist, targetDist, 2f * Time.unscaledDeltaTime);
        var yawRot = Quaternion.Euler(0, panelYaw, 0);
        panel.localPosition = anchor + yawRot * new Vector3(0, Plugin.Cfg.UIHeight.Value, dist);
        panel.localRotation = yawRot;
        panel.localScale = new Vector3(width, width * Height / Width, 1);
    }

    // After a recenter the head jumps to the origin; let the panel jump with it.
    public void Snap() => anchorValid = false;

    // Stick turning turns the tracking space around the head (VRCameras.TurnAroundHead), which moves the head's
    // tracked position by the opposite yaw; move the lagging anchor the same way so the panel turns along with the
    // view instead of sliding after it.
    public void Turned(float degrees)
    {
        if (anchorValid) anchor = Quaternion.Euler(0, -degrees, 0) * anchor;
    }
}
