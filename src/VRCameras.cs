using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.XR;

namespace CloudpunkVR;

// Head tracking on top of the game's own cameras. Unity's automatic XR camera tracking is disabled; instead, right
// before rendering, every camera that renders to the HMD gets "game pose * head pose" and is put back to the game
// pose at end of frame, so game logic (Cinemachine, movement, interaction) keeps seeing the camera it placed.
internal sealed class VRCameras
{
    struct Saved { public Transform T; public Vector3 Pos; public Quaternion Rot; }

    public static VRCameras Instance;

    readonly Il2CppReferenceArray<Camera> all = new(64);
    readonly HashSet<int> untracked = new();
    readonly List<Saved> saved = new();
    readonly HashSet<int> excluded = new();

    Vector3 neutralPos;
    Quaternion neutralYawInv = Quaternion.identity;
    bool calibrated;

    public Vector3 HeadPos { get; private set; }
    public Quaternion HeadRot { get; private set; } = Quaternion.identity;
    public float HeadYaw { get; private set; }
    public bool Rendering { get; private set; }

    // Controllers in the same calibrated space as the head; Valid is false while a controller is not tracked.
    public struct Hand { public bool Valid; public Vector3 Pos; public Quaternion Rot; }
    public Hand Left, Right;

    // Game pose of the main camera this frame and the basis the head pose was applied in (yaw, plus cockpit tilt).
    public Vector3 MainGamePos { get; private set; }
    public Quaternion MainBasis { get; private set; } = Quaternion.identity;
    public bool MainPoseValid { get; private set; }

    public Vector3 ToWorld(Vector3 tracked) => MainGamePos + MainBasis * tracked;
    public Quaternion ToWorld(Quaternion tracked) => MainBasis * tracked;

    // Game pose of the main camera this frame; the cockpit interior is rendered relative to it.
    Matrix4x4 mainGameLocalToWorld = Matrix4x4.identity;
    Matrix4x4 interiorGameLocalToWorld = Matrix4x4.identity;
    bool interiorPoseValid;

    public VRCameras() => Instance = this;

    public static float FarClipOverride;
    // Dev: log the main camera's game yaw for this many applications (turntrace).
    public static int TraceFrames;
    static bool traceLate;
    static bool loggedMono;
    // Dev only: extra head rotation, to test culling with head turns on a headset that does not move.
    public static Quaternion FakeHead = Quaternion.identity;
    public static bool FakeHeadAbsolute;   // dev: FakeHead replaces the headset's rotation (a headset lying on a desk)
    // Dev only: the head stepped this far from where it was calibrated (tracking space), for a headset that does not move.
    public static Vector3 FakeHeadOffset;
    // Dev only: (side, height, forward) offsets of pretend controllers when none are tracked.
    public static Vector3? FakeHands;
    public static Vector3 FakeHandEuler;

    public void Exclude(Camera c) => excluded.Add(c.GetInstanceID());

    public void Recenter() => calibrated = false;

    Vector3 rawHeadPos;

    // The game view is about to turn by this yaw (stick turning). Turning happens around the calibrated centre, so
    // move that centre around the head by the opposite yaw: the eyes and controllers then keep their place in the
    // world and only the facing changes, as when a VR game turns the play space around the player's head.
    public void TurnAroundHead(float degrees)
    {
        if (!XRSettings.enabled || !calibrated) return;
        neutralPos = rawHeadPos - Quaternion.Euler(0, -degrees, 0) * (rawHeadPos - neutralPos);
    }

    void ReadHead()
    {
        if (!XRSettings.enabled)
        {
            HeadPos = Vector3.zero;
            HeadRot = Quaternion.identity;
            HeadYaw = 0;
            Left = Right = default;
            return;
        }
        var pos = InputTracking.GetLocalPosition(XRNode.CenterEye) + (calibrated ? FakeHeadOffset : Vector3.zero);
        var rot = InputTracking.GetLocalRotation(XRNode.CenterEye);
        rawHeadPos = pos;
        if (!calibrated && rot != new Quaternion(0, 0, 0, 0))
        {
            neutralPos = pos;
            neutralYawInv = Quaternion.Inverse(Quaternion.Euler(0, rot.eulerAngles.y, 0));
            calibrated = true;
            Plugin.L.LogInfo($"view recentered at {Diag.V(pos)}");
        }
        HeadPos = neutralYawInv * (pos - neutralPos);
        HeadRot = FakeHeadAbsolute ? FakeHead : FakeHead * neutralYawInv * rot;
        float yaw = HeadRot.eulerAngles.y;
        HeadYaw = yaw > 180 ? yaw - 360 : yaw;
        Left = ReadHand(XRNode.LeftHand);
        Right = ReadHand(XRNode.RightHand);
        if (FakeHands is Vector3 fh)
        {
            // dev: controllers held in front of the chest, relative to the head's facing
            var yawOnly = Quaternion.Euler(0, HeadYaw, 0);
            var e = FakeHandEuler;
            Left = new Hand { Valid = true, Pos = HeadPos + yawOnly * new Vector3(-fh.x, fh.y, fh.z), Rot = yawOnly * Quaternion.Euler(e.x, -e.y, -e.z) };
            Right = new Hand { Valid = true, Pos = HeadPos + yawOnly * new Vector3(fh.x, fh.y, fh.z), Rot = yawOnly * Quaternion.Euler(e.x, e.y, e.z) };
        }
    }

    Hand ReadHand(XRNode node)
    {
        var rot = InputTracking.GetLocalRotation(node);
        if (rot == new Quaternion(0, 0, 0, 0)) return default;
        return new Hand { Valid = true, Pos = neutralYawInv * (InputTracking.GetLocalPosition(node) - neutralPos), Rot = neutralYawInv * rot };
    }

    int headFrame = -1;
    readonly Dictionary<int, int> appliedFrame = new();

    void EnsureHead()
    {
        if (headFrame == Time.frameCount) return;
        headFrame = Time.frameCount;
        ReadHead();
        interiorPoseValid = false;
        MainPoseValid = false;
    }

    static bool Cockpit() => Global.Instance != null && Global.IsInVehicle && Global.IsInVehicleCockpit;

    // Right after a Cinemachine brain has put its camera at the game pose (PushStateToUnityCamera): add the head pose
    // there and then, so every script that looks at the camera afterwards in this frame (volumetric lights, culling,
    // image effects) sees the tracked view rather than the game's.
    public void AfterBrainPush(Camera c)
    {
        if (c == null || c.targetTexture != null || c.stereoTargetEye == StereoTargetEyeMask.None || excluded.Contains(c.GetInstanceID())) return;
        if (!XRSettings.enabled) return;
        if (RidesOnCamera(c)) return;
        EnsureHead();
        int id = c.GetInstanceID();
        if (appliedFrame.TryGetValue(id, out var f) && f == Time.frameCount)
        {
            // Pushed twice in one frame: the brain wrote a fresh game pose, forget the one saved before.
            for (int i = saved.Count - 1; i >= 0; i--)
                if (saved[i].T != null && saved[i].T.GetInstanceID() == c.transform.GetInstanceID()) saved.RemoveAt(i);
        }
        Track(c);
    }

    void Track(Camera c)
    {
        int id = c.GetInstanceID();
        appliedFrame[id] = Time.frameCount;
        if (untracked.Add(id)) XRDevice.DisableAutoXRCameraTracking(c, true);
        var g = Global.Instance;
        bool isMain = g != null && g.mainCamera != null && id == g.mainCamera.GetInstanceID();
        Apply(c, tilt: isMain && Cockpit() ? Plugin.Cfg.CockpitTilt.Value : 0, isMain);
    }

    // The head pose read when a brain placed its camera (LateUpdate) is not the one this frame is shown with: the
    // runtime updates the headset and controller poses again just before rendering, and the compositor re-projects
    // the frame from that newer pose. A camera left on the older pose is off by the head's motion in between, which
    // on a real headset shakes the whole view as soon as the head moves. Read the pose again here and redo the
    // cameras placed with the old one, from the game poses saved then.
    void RefreshHead()
    {
        if (headFrame != Time.frameCount) { EnsureHead(); return; }
        var before = HeadRot;
        ReadHead();
        NoteLateMove(Quaternion.Angle(before, HeadRot));
        if (saved.Count == 0) return;
        var redo = new List<Camera>();
        for (int i = saved.Count - 1; i >= 0; i--)
        {
            var t = saved[i].T;
            if (t == null) continue;
            t.SetPositionAndRotation(saved[i].Pos, saved[i].Rot);
            var c = t.GetComponent<Camera>();
            if (c != null) redo.Add(c);
        }
        saved.Clear();
        MainPoseValid = false;
        for (int i = redo.Count - 1; i >= 0; i--) Track(redo[i]);
    }

    // How far the head pose moved between the brain push and rendering, logged every 30 s while it moves at all: on a
    // real headset this shows the error the camera would have had without the refresh above.
    int lateFrames, lateMoved;
    float lateSum, lateMax, lateLogAt;

    void NoteLateMove(float deg)
    {
        lateFrames++;
        if (deg > 0.01f) { lateMoved++; lateSum += deg; lateMax = Mathf.Max(lateMax, deg); }
        if (Time.unscaledTime < lateLogAt) return;
        lateLogAt = Time.unscaledTime + 30f;
        if (lateMoved > 0)
            Plugin.L.LogInfo($"head: pose newer at render than at the brain push in {lateMoved}/{lateFrames} frames, " +
                             $"by {lateSum / lateMoved:F2}° on average, {lateMax:F2}° at most (corrected)");
        lateFrames = lateMoved = 0;
        lateSum = lateMax = 0;
    }

    // First camera callback of the render phase: the freshest head pose goes on every camera, and cameras no brain
    // has pushed this frame get theirs now.
    public void BeforeRender(bool cockpit)
    {
        RefreshHead();

        var g = Global.Instance;
        var interior = g != null && g.vehicleInterior != null ? g.vehicleInterior.cameraCockpit : null;
        if (interior != null && cockpit)
        {
            var t = interior.transform;
            interiorGameLocalToWorld = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            interiorPoseValid = true;
        }

        int n = Camera.GetAllCameras(all);
        for (int i = 0; i < n; i++)
        {
            var c = all[i];
            if (c.targetTexture != null || c.stereoTargetEye == StereoTargetEyeMask.None) continue;
            // flat mode: only the game's main camera carries the simulated head (the UI camera draws to the screen too)
            if (!XRSettings.enabled && (g == null || g.mainCamera == null || c.GetInstanceID() != g.mainCamera.GetInstanceID())) continue;
            int id = c.GetInstanceID();
            if (excluded.Contains(id)) continue;
            if (untracked.Add(id)) XRDevice.DisableAutoXRCameraTracking(c, true);

            // Child cameras (e.g. the skybox camera under Main Camera) inherit the parent's head pose.
            var parent = c.transform.parent;
            if (parent != null && parent.GetComponent<Camera>() != null) continue;
            if (appliedFrame.TryGetValue(id, out var f) && f == Time.frameCount) continue;
            traceLate = true;
            Track(c);
            traceLate = false;
        }
        ApplyFakeStereo(g != null ? g.mainCamera : null);
        Rendering = true;
    }

    // Dev only (fakestereo): the null headset has both eyes at one point with the same symmetric field of view, so a
    // screen-space effect that is wrong for one eye looks right in every test. These give the main camera a real eye
    // spacing and Quest 3-like asymmetric eye frusta (outer 52°, inner 42°, up 44°, down 52°).
    public static float FakeIpd;
    public static bool FakeAsym;
    bool fakeStereoOn;

    void ApplyFakeStereo(Camera main)
    {
        if (main == null) return;
        if (FakeIpd <= 0 && !FakeAsym)
        {
            if (fakeStereoOn) { main.ResetStereoViewMatrices(); main.ResetStereoProjectionMatrices(); fakeStereoOn = false; }
            return;
        }
        fakeStereoOn = true;
        var w = main.worldToCameraMatrix;
        main.SetStereoViewMatrix(Camera.StereoscopicEye.Left, Matrix4x4.Translate(new Vector3(FakeIpd / 2, 0, 0)) * w);
        main.SetStereoViewMatrix(Camera.StereoscopicEye.Right, Matrix4x4.Translate(new Vector3(-FakeIpd / 2, 0, 0)) * w);
        if (!FakeAsym) { main.ResetStereoProjectionMatrices(); return; }
        float n = main.nearClipPlane, f = main.farClipPlane;
        float outer = Mathf.Tan(52 * Mathf.Deg2Rad), inner = Mathf.Tan(42 * Mathf.Deg2Rad), up = Mathf.Tan(44 * Mathf.Deg2Rad), down = Mathf.Tan(52 * Mathf.Deg2Rad);
        main.SetStereoProjectionMatrix(Camera.StereoscopicEye.Left, Frustum(-outer, inner, -down, up, n, f));
        main.SetStereoProjectionMatrix(Camera.StereoscopicEye.Right, Frustum(-inner, outer, -down, up, n, f));
    }

    // OpenGL-style projection from the tangents of the frustum's half-angles.
    public static Matrix4x4 Frustum(float l, float r, float b, float t, float n, float f)
    {
        var m = Matrix4x4.zero;
        m[0, 0] = 2 / (r - l); m[0, 2] = (r + l) / (r - l);
        m[1, 1] = 2 / (t - b); m[1, 2] = (t + b) / (t - b);
        m[2, 2] = -(f + n) / (f - n); m[2, 3] = -2 * f * n / (f - n);
        m[3, 2] = -1;
        return m;
    }

    void Apply(Camera c, float tilt, bool isMain)
    {
        var t = c.transform;
        var pos = t.position;
        var rot = t.rotation;
        saved.Add(new Saved { T = t, Pos = pos, Rot = rot });
        var level = Quaternion.Euler(0, rot.eulerAngles.y, 0);
        var basis = tilt > 0 ? Quaternion.Slerp(level, rot, tilt) : level;
        if (isMain)
        {
            if (TraceFrames > 0)
            {
                TraceFrames--;
                var gm = Global.Instance;
                var pov = gm != null && gm.player != null ? gm.player.firstPersonPOV : null;
                Plugin.L.LogInfo($"turn: f={Time.frameCount} dt={Time.unscaledDeltaTime * 1000:F1} via={(traceLate ? "render" : "push")}" +
                                 $"game={rot.eulerAngles.y:F2} pov={(pov != null ? pov.m_HorizontalAxis.Value : float.NaN):F2} head={HeadYaw:F1} " +
                                 $"view={(basis * HeadRot).eulerAngles.y:F2} eye={Diag.V(pos + basis * HeadPos)} headPos={Diag.V(HeadPos)} body={(VRHost.Body != null ? VRHost.Body.BodyYaw : float.NaN):F1}");
            }
            mainGameLocalToWorld = Matrix4x4.TRS(pos, rot, Vector3.one);
            MainGamePos = pos;
            MainBasis = basis;
            MainPoseValid = true;
            // The game keeps the far plane at about 600 m (set through Cinemachine every frame); push it out here.
            float far = FarClipOverride > 0 ? FarClipOverride : Plugin.Cfg.DrawDistance.Value;
            if (far > c.farClipPlane) c.farClipPlane = far;
        }
        t.SetPositionAndRotation(pos + basis * HeadPos, basis * HeadRot);
    }

    // The camera the world is seen through: the game's main camera, or on the title screen (main camera off) the
    // menu camera that draws the title's city.
    public static Camera WorldCamera(Camera main)
    {
        if (main != null && main.isActiveAndEnabled) return main;
        if (menuCam == null || !menuCam) { var go = GameObject.Find("Global/UI/Menu/MenuCamera"); menuCam = go != null ? go.GetComponent<Camera>() : null; }
        return menuCam != null && menuCam.isActiveAndEnabled ? menuCam : null;
    }
    static Camera menuCam;

    public static bool RidesOnCamera(Camera c)
    {
        var p = c.transform.parent;
        return p != null && p.GetComponent<Camera>() != null;
    }

    readonly Dictionary<int, bool> skyCams = new();

    // Per-camera fixups right before culling.
    public void OnPreCull(Camera cam)
    {
        // ViewToGlobalTexture renders the sky into a screen-space texture with Main Camera's mono projection; give it
        // a frustum that covers both eyes, from which each eye's part is cut right before that eye renders
        // (VRStereoFx). Every camera passes here several times a frame; its name is read once.
        int id = cam.GetInstanceID();
        if (!skyCams.TryGetValue(id, out bool sky)) skyCams[id] = sky = cam.name == "SkyboxCamera" && RidesOnCamera(cam);
        if (sky)
        {
            var main = cam.transform.parent.GetComponent<Camera>();
            cam.projectionMatrix = VRStereoFx.SkyUnionProjection(main, cam.nearClipPlane, cam.farClipPlane);
        }
    }

    // Called at end of frame, after all cameras rendered.
    public void AfterRender()
    {
        foreach (var s in saved)
            if (s.T != null) s.T.SetPositionAndRotation(s.Pos, s.Rot);
        saved.Clear();
        Rendering = false;
    }

    // The cockpit interior is a copy of the car interior placed near the world origin and drawn by a second camera
    // from MainCameraVehicleRenderer.OnPostRender, i.e. once per eye. Give it this eye's view and projection,
    // mapped from the car's world space into the interior's space, and this eye's buffer: the game points it at
    // Main Camera's target in its own OnPreRender, which is too late to take effect for that render, so with two
    // alternating eye buffers it would draw into the other eye's buffer on every other frame.
    public void SetupInteriorEye(Camera main, Camera interior)
    {
        if (!interiorPoseValid || interior == null) return;
        if (Coo.CooView.Rendering || !XRSettings.enabled)
        {
            // a mono render (the stream's spectator view, or flat first-person mode): the camera's own matrices
            if (interior.stereoTargetEye != StereoTargetEyeMask.None) interior.stereoTargetEye = StereoTargetEyeMask.None;
            // the spectator render goes through post-processing: draw into the buffer the camera renders into now
            // (the final texture is overwritten by the post-processing result afterwards)
            interior.targetTexture = Coo.CooView.Rendering ? main.activeTexture : main.targetTexture;
            var toCar = mainGameLocalToWorld * interiorGameLocalToWorld.inverse;
            interior.worldToCameraMatrix = main.worldToCameraMatrix * toCar;
            interior.projectionMatrix = main.projectionMatrix;
            if (!loggedMono && DevCommands.Enabled)
            {
                loggedMono = true;
                Plugin.L.LogInfo($"interior mono: target={Diag.Name(interior.targetTexture)} mainTarget={Diag.Name(main.targetTexture)} active={Diag.Name(main.activeTexture)} spectator={Coo.CooView.Rendering} mask={interior.cullingMask} clear={interior.clearFlags} enabled={interior.enabled}");
            }
            return;
        }
        if (!Rendering) return;
        var eye = main.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
        if (interior.stereoTargetEye != StereoTargetEyeMask.None) interior.stereoTargetEye = StereoTargetEyeMask.None;
        interior.targetTexture = main.activeTexture;
        // world(car) -> interior: interiorGame * mainGame^-1; the eye view of an interior point is view * (that)^-1
        var interiorToCarWorld = mainGameLocalToWorld * interiorGameLocalToWorld.inverse;
        interior.worldToCameraMatrix = main.GetStereoViewMatrix(eye) * interiorToCarWorld;
        interior.projectionMatrix = main.GetStereoProjectionMatrix(eye);
    }
}
