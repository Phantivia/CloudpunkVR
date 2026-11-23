using System.Collections.Generic;
using Cinemachine;
using UnityEngine;

namespace CloudpunkVR;

// First person on foot with a visible body, in the way of social VR apps: the game's own first-person virtual camera
// (PlayerFirstPersonCam, present in the game but never switched on by it) is given priority, the character model is
// kept under the headset with its head hidden, the body turns after the head, and the arms reach for the controllers.
//
// The character (Rania) is built from rigid voxel parts on a transform hierarchy (Hips, Spine, Head, upper and lower
// arms) that MeshAnimator poses; nothing is skinned. Everything here is applied right before rendering and put back
// at the end of the frame, like the camera poses, so the game's animation, physics and logic never see it.
internal sealed class VRBody
{
    const int FirstPersonPriority = 50;

    readonly VRCameras cams;

    struct Saved { public Transform T; public Vector3 Pos; public Quaternion Rot; }
    readonly List<Saved> saved = new();
    readonly List<(Renderer r, UnityEngine.Rendering.ShadowCastingMode mode)> hiddenHead = new();

    int boundPlayer;
    Transform model, head;
    readonly Arm[] arms = { new Arm(-1), new Arm(1) };
    internal Arm[] Arms => arms;
    int fpOriginalPriority = int.MinValue;
    bool fpWasActive, fpWasEnabled;
    float bodyYaw, rigYawBefore;
    public float BodyYaw => bodyYaw;
    bool bodyYawValid, posed;
    int occlusionScanned = -1, boundScene;

    public bool FirstPersonLive { get; private set; }

    // Switched by the camera button (X): the game's own overhead camera on foot for the rest of the session.
    public static bool ThirdPerson;

    public VRBody(VRCameras cams) => this.cams = cams;

    // The game turns the character toward the stick direction at a limited rate and walks along its facing, so a
    // sidestep starts as a curve. In first person the character faces the stick direction at once, and while standing
    // it faces where the player looks, so what the game offers to interact with follows the gaze.
    public static void Patch(HarmonyLib.Harmony h) =>
        h.Patch(HarmonyLib.AccessTools.Method(typeof(Player), nameof(Player.Movement)),
            prefix: new HarmonyLib.HarmonyMethod(typeof(VRBody), nameof(MovementPrefix)));

    static void MovementPrefix(Player __instance)
    {
        using var __pt = PluginTiming.Measure("patch:movement");
        var body = VRHost.Body;
        if (body == null || !body.FirstPersonLive) return;
        var pt = __instance.moveToPoint;
        if (pt == null) return;
        if (Mathf.Abs(__instance.leftRight) + Mathf.Abs(__instance.upDown) < 0.05f)
        {
            var g = Global.Instance;
            var cams = VRCameras.Instance;
            if (g == null || g.mainCamera == null || cams == null || g.IsInMenu() || g.IsInDialog(false)) return;
            __instance.transform.rotation = Quaternion.Euler(0, g.mainCamera.transform.eulerAngles.y + cams.HeadYaw, 0);
            return;
        }
        // The game steers toward MoveToPoint's local offset, read as a world direction.
        var d = pt.localPosition;
        d.y = 0;
        if (d.sqrMagnitude > 1e-4f) __instance.transform.rotation = Quaternion.LookRotation(d);
    }

    static CinemachineVirtualCamera FirstPersonCam(Global g) => g != null && g.player != null ? g.player.firstPersonCamera : null;

    // Per frame from Update: keep the first-person camera on while on foot.
    public void Update()
    {
        var g = Global.Instance;
        var fp = FirstPersonCam(g);
        if (fp == null) { FirstPersonLive = false; return; }
        bool want = Plugin.Cfg.FirstPersonOnFoot.Value && !ThirdPerson && !Global.IsInVehicle && g.player.gameObject.activeInHierarchy && !SceneLoader.IsLoading();
        if (want)
        {
            if (fpOriginalPriority == int.MinValue) { fpOriginalPriority = fp.Priority; fpWasActive = fp.gameObject.activeSelf; fpWasEnabled = fp.enabled; }
            if (!fp.gameObject.activeSelf) fp.gameObject.SetActive(true);
            if (!fp.enabled) fp.enabled = true;
            if (fp.Priority != FirstPersonPriority)
            {
                fp.Priority = FirstPersonPriority;
                Plugin.L.LogInfo("body: first-person camera on");
            }
        }
        else if (fpOriginalPriority != int.MinValue && fp.Priority == FirstPersonPriority)
        {
            fp.Priority = fpOriginalPriority;
            // The game never uses this camera; at its own priority it would still outrank the overhead camera.
            if (!fpWasEnabled) fp.enabled = false;
            if (!fpWasActive) fp.gameObject.SetActive(false);
            Plugin.L.LogInfo("body: first-person camera off");
        }
        var brain = g.cameraBrain;
        var live = brain != null ? brain.ActiveVirtualCamera : null;
        FirstPersonLive = want && live != null && live.VirtualCameraGameObject != null &&
                          live.VirtualCameraGameObject.GetInstanceID() == fp.gameObject.GetInstanceID();
    }

    // Hand the first-person camera back to the game.
    public void ReleaseFirstPerson()
    {
        if (fpOriginalPriority == int.MinValue) { FirstPersonLive = false; return; }
        bool was = Plugin.Cfg.FirstPersonOnFoot.Value;
        var fp = FirstPersonCam(Global.Instance);
        if (fp != null && fp.Priority == FirstPersonPriority)
        {
            fp.Priority = fpOriginalPriority;
            if (!fpWasEnabled) fp.enabled = false;
            if (!fpWasActive) fp.gameObject.SetActive(false);
            Plugin.L.LogInfo("body: first-person camera off");
        }
        FirstPersonLive = false;
    }

    bool Bind(Player p)
    {
        boundScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex;
        if (boundPlayer == p.GetInstanceID() && model != null) return head != null;
        boundPlayer = p.GetInstanceID();
        RestoreHead();
        model = p.anim != null ? p.anim.transform : p.transform.Find("thin_factory");
        if (model == null) return false;
        Transform F(string n) => FindDeep(model, n);
        head = F("Head");
        arms[0].Bind(F("LeftUpperArm"), F("LeftLowerArm"), F("LeftHand"), model);
        arms[1].Bind(F("RightUpperArm"), F("RightLowerArm"), F("RightHand"), model);
        Plugin.L.LogInfo($"body: bound {Diag.PathOf(model)} head={head != null} arms={arms[0].Ok}/{arms[1].Ok}");
        return head != null;
    }

    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var r = FindDeep(t.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }

    void Save(Transform t)
    {
        if (t != null) saved.Add(new Saved { T = t, Pos = t.localPosition, Rot = t.localRotation });
    }

    // Once per frame after the head pose is on the main camera, before culling.
    public void BeforeRender(Camera main)
    {
        posed = false;
        var g = Global.Instance;
        if (!FirstPersonLive || !Plugin.Cfg.ShowBody.Value || main == null || g == null || g.player == null || !Bind(g.player))
        {
            RestoreHead();
            bodyYawValid = false;
            return;
        }
        if (occlusionScanned != boundScene) { occlusionScanned = boundScene; KeepOutOfOcclusionCulling(g.player); }
        HideHead();
        saved.Clear();
        Save(model);
        foreach (var arm in arms) { Save(arm.Upper); Save(arm.Lower); Save(arm.Hand); }
        posed = true;

        var eye = main.transform.position;
        float headYaw = main.transform.eulerAngles.y;

        // Stick turning turns the whole player, body included (the game view's own yaw, before the head). Beyond
        // that the body turns after the head: at once while walking, otherwise only once the head is turned well away.
        var cc = g.player.charController;
        var v = cc != null ? cc.velocity : Vector3.zero;
        float speed = new Vector2(v.x, v.z).magnitude;
        float rigYaw = cams.MainBasis.eulerAngles.y;
        if (!bodyYawValid) { bodyYaw = headYaw; rigYawBefore = rigYaw; bodyYawValid = true; }
        bodyYaw = Mathf.Repeat(bodyYaw + Mathf.DeltaAngle(rigYawBefore, rigYaw), 360f);
        rigYawBefore = rigYaw;
        float diff = Mathf.DeltaAngle(bodyYaw, headYaw);
        float dt = Time.unscaledDeltaTime;
        if (speed > 0.3f) bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, headYaw, 360f * dt);
        else if (Mathf.Abs(diff) > 50f) bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, headYaw, Mathf.Max(120f, Mathf.Abs(diff) * 4f) * dt);
        var bodyRot = Quaternion.Euler(0, bodyYaw, 0);
        var fwd = bodyRot * Vector3.forward;

        // Feet stay where the game put them; the body slides horizontally so the neck is under the eyes, slightly
        // behind them, so looking down shows the chest instead of the inside of the head.
        model.rotation = bodyRot;
        var neck = head.position;
        var want = eye - fwd * 0.12f;
        model.position += new Vector3(want.x - neck.x, 0, want.z - neck.z);

        if (cams.MainPoseValid)
        {
            var neckNow = head.position;
            if (cams.Left.Valid) arms[0].Reach(cams.ToWorld(cams.Left.Pos), cams.ToWorld(cams.Left.Rot), bodyRot, neckNow);
            if (cams.Right.Valid) arms[1].Reach(cams.ToWorld(cams.Right.Pos), cams.ToWorld(cams.Right.Rot), bodyRot, neckNow);
            if (Arm.LogFrames > 0) Arm.LogFrames--;
        }
    }

    // One arm: upper arm, forearm and hand as rigid parts. The controller pose gives the hand's position and
    // orientation; the shoulder gives a little when reaching far, the elbow hangs down and out and turns with the
    // wrist's roll, it stays clear of the chest, the forearm takes most of the roll between elbow and hand, and the
    // wrist itself only bends and rolls as far as a real one.
    internal sealed class Arm
    {
        public Transform Upper, Lower, Hand;
        public readonly float Side;            // -1 left, +1 right
        Vector3 upperAxis, lowerAxis;          // each bone's direction toward its child, in its own space
        Vector3 fingerAxis, backAxis;          // hand space: wrist to knuckles, back of the hand
        public Vector3 FingerAxis => fingerAxis;
        public Vector3 BackAxis => backAxis;
        float l1, l2;
        public bool Ok { get; private set; }

        // How the hand sits on the controller (controller space, Unity XR axes: +z points ahead, +y the face buttons).
        public static float WristBack = 0.085f; // wrist joint behind the grip centre, metres
        // MaxWristSwing/MaxWristTwist below 180 bring back the anatomical wrist limits (dev: armcfg); at 180 the hand
        // follows the controller exactly.
        public static float MaxShoulderGive = 0.06f, MaxWristSwing = 180f, MaxWristTwist = 180f, ForearmTwistShare = 0.65f;
        public static int LogFrames;   // dev: log the solve for this many frames

        public Arm(float side) => Side = side;

        // Controller space -> hand frame (z: wrist to knuckles, x times side: back of the hand); mirrored for the left.
        public static Quaternion Grip(float side) =>
            Quaternion.Euler(Plugin.Cfg.HandPitch.Value, Plugin.Cfg.HandYaw.Value * side, Plugin.Cfg.HandRoll.Value * side);

        public void Bind(Transform upper, Transform lower, Transform hand, Transform model)
        {
            Upper = upper; Lower = lower; Hand = hand;
            Ok = upper != null && lower != null && hand != null;
            if (!Ok) return;
            l1 = Vector3.Distance(upper.position, lower.position);
            l2 = Vector3.Distance(lower.position, hand.position);
            Ok = l1 > 1e-3f && l2 > 1e-3f;
            if (!Ok) return;
            upperAxis = upper.InverseTransformDirection(lower.position - upper.position).normalized;
            lowerAxis = lower.InverseTransformDirection(hand.position - lower.position).normalized;

            // The hand part reaches from the wrist joint toward the fingers; its mesh centre gives that direction.
            var bounds = new Bounds();
            bool any = false;
            foreach (var r in hand.GetComponentsInChildren<Renderer>(true))
            {
                if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
            }
            var tip = any ? bounds.center - hand.position : Vector3.zero;
            fingerAxis = tip.magnitude > 0.01f ? hand.InverseTransformDirection(tip).normalized
                                               : hand.InverseTransformDirection(lower.TransformDirection(lowerAxis)).normalized;
            // The back of the hand: with the arms at the sides it faces away from the body. Take the hand's own axis
            // that does so now, square to the fingers.
            var outward = model.right * Side;
            Vector3 best = Vector3.zero;
            float bestDot = -2;
            foreach (var ax in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
            {
                if (Mathf.Abs(Vector3.Dot(ax, fingerAxis)) > 0.6f) continue;
                float d = Vector3.Dot(hand.TransformDirection(ax), outward);
                if (d > bestDot) { bestDot = d; best = ax; }
            }
            backAxis = Vector3.ProjectOnPlane(best, fingerAxis).normalized;
            Plugin.L.LogInfo($"body: arm {(Side < 0 ? "L" : "R")} l1={l1:F3} l2={l2:F3} upperAxis={Diag.V(upperAxis)} lowerAxis={Diag.V(lowerAxis)} finger={Diag.V(fingerAxis)} back={Diag.V(backAxis)} tip={tip.magnitude:F3}");
        }

        // Swing-twist split of q about a unit axis: q = swing * twist.
        static void SwingTwist(Quaternion q, Vector3 axis, out Quaternion swing, out Quaternion twist)
        {
            var p = Vector3.Project(new Vector3(q.x, q.y, q.z), axis);
            float n = Mathf.Sqrt(p.sqrMagnitude + q.w * q.w);
            twist = n < 1e-6f ? Quaternion.identity : new Quaternion(p.x / n, p.y / n, p.z / n, q.w / n);
            swing = q * Quaternion.Inverse(twist);
        }

        static float SignedAngle(Quaternion q, Vector3 axis)
        {
            q.ToAngleAxis(out float a, out Vector3 ax);
            if (a > 180) a -= 360;
            return Vector3.Dot(ax, axis) < 0 ? -a : a;
        }

        static Quaternion LimitedWrist(Quaternion rel, Vector3 axisInHand)
        {
            SwingTwist(rel, axisInHand, out var swing, out var twist);
            return Limit(swing, MaxWristSwing) * Limit(twist, MaxWristTwist);
        }

        static Quaternion Limit(Quaternion q, float maxDeg)
        {
            q.ToAngleAxis(out float a, out Vector3 ax);
            if (a > 180) { a = 360 - a; ax = -ax; }
            return a <= maxDeg || float.IsNaN(ax.x) || float.IsInfinity(ax.x) ? q : Quaternion.AngleAxis(maxDeg, ax);
        }

        public void Reach(Vector3 ctrlPos, Quaternion ctrlRot, Quaternion bodyRot, Vector3 neck)
        {
            if (!Ok) return;
            var fwd = bodyRot * Vector3.forward;
            var outward = bodyRot * Vector3.right * Side;

            // Hand frame from the controller (HandPitch/HandYaw/HandRoll, set by the hand calibration): by default the
            // knuckles a little below the controller's pointing axis, the back of the hand on the outside of the grip.
            var grip = ctrlRot * Grip(Side);
            var fingers = grip * Vector3.forward;
            var back = grip * new Vector3(Side, 0, 0);
            var handTarget = Quaternion.LookRotation(fingers, back) * Quaternion.Inverse(Quaternion.LookRotation(fingerAxis, backAxis));
            var wrist = ctrlPos - fingers * WristBack;

            // Shoulder: gives up to a few centimetres toward a target out of reach.
            var s = Upper.position;
            var toW = wrist - s;
            float reach = (l1 + l2) * 0.98f;
            if (toW.magnitude > reach)
            {
                s += toW.normalized * Mathf.Min(toW.magnitude - reach, MaxShoulderGive);
                Upper.position = s;
                toW = wrist - s;
            }
            float d = Mathf.Clamp(toW.magnitude, Mathf.Abs(l1 - l2) + 0.01f, (l1 + l2) * 0.999f);
            var dir = toW.normalized;
            float a = (l1 * l1 - l2 * l2 + d * d) / (2 * d);
            float h = Mathf.Sqrt(Mathf.Max(0, l1 * l1 - a * a));

            // Elbow: hangs down, out and back, and turns about the shoulder-wrist line with half of the hand's roll
            // away from thumb-up (palm up tucks the elbow in, palm down lifts it out).
            var pole = Vector3.down + outward * 0.5f - fwd * 0.35f;
            var po = Vector3.ProjectOnPlane(outward, dir);
            var pb = Vector3.ProjectOnPlane(back, dir);
            if (po.sqrMagnitude > 1e-4f && pb.sqrMagnitude > 1e-4f)
                pole = Quaternion.AngleAxis(Mathf.Clamp(Vector3.SignedAngle(po, pb, dir) * 0.5f, -70f, 70f), dir) * pole;
            var bend = Vector3.ProjectOnPlane(pole, dir);
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(Vector3.down, dir);
            var mid = s + dir * a;
            var elbow = mid + bend.normalized * h;
            // Keep the elbow outside the chest.
            float lateral = Vector3.Dot(elbow - neck, outward);
            if (lateral < 0.1f)
            {
                var pushed = Vector3.ProjectOnPlane(elbow + outward * (0.1f - lateral) - mid, dir);
                if (pushed.sqrMagnitude > 1e-6f) elbow = mid + pushed.normalized * h;
            }

            Upper.rotation = Quaternion.FromToRotation(Upper.rotation * upperAxis, elbow - s) * Upper.rotation;
            var foreDir = s + dir * d - Lower.position;
            var fore = Quaternion.FromToRotation(Lower.rotation * lowerAxis, foreDir) * Lower.rotation;

            // The forearm takes most of the roll between it and the hand, so a turned wrist does not look wrung. The
            // hand itself always takes the controller's orientation exactly: in VR the hand has to be where and how the
            // real one is, so the wrist bends and rolls as far as the controller does.
            var handLocal = Hand.localRotation;
            var foreAxisW = foreDir.normalized;
            var rel = Quaternion.Inverse(fore * handLocal) * handTarget;
            var axisInHand = Quaternion.Inverse(fore * handLocal) * foreAxisW;
            SwingTwist(rel, axisInHand, out _, out var twist);
            fore = Quaternion.AngleAxis(SignedAngle(twist, axisInHand) * ForearmTwistShare, foreAxisW) * fore;
            Lower.rotation = fore;
            if (MaxWristSwing >= 180f && MaxWristTwist >= 180f) Hand.rotation = handTarget;
            else Hand.localRotation = handLocal * LimitedWrist(Quaternion.Inverse(fore * handLocal) * handTarget, Quaternion.Inverse(fore * handLocal) * foreAxisW);
            if (LogFrames > 0)
            {
                var o = neck;
                Plugin.L.LogInfo($"arm {(Side < 0 ? "L" : "R")}: rel to neck: shoulder={Diag.V(Quaternion.Inverse(bodyRot) * (s - o))} elbowWant={Diag.V(Quaternion.Inverse(bodyRot) * (elbow - o))} " +
                                 $"elbowGot={Diag.V(Quaternion.Inverse(bodyRot) * (Lower.position - o))} wristWant={Diag.V(Quaternion.Inverse(bodyRot) * (s + dir * d - o))} wristGot={Diag.V(Quaternion.Inverse(bodyRot) * (Hand.position - o))} " +
                                 $"fingerWant={Diag.V(Quaternion.Inverse(bodyRot) * fingers)} fingerGot={Diag.V(Quaternion.Inverse(bodyRot) * Hand.TransformDirection(fingerAxis))} " +
                                 $"parents: {Upper.parent?.name}>{Upper.name} {Lower.parent?.name}>{Lower.name} {Hand.parent?.name}>{Hand.name}");
            }
        }
    }

    // GDOC (the game's occlusion culling) hides what it thinks is occluded by scaling it to zero, and a body around
    // the camera always looks occluded to it. Take the character out of its care.
    static void KeepOutOfOcclusionCulling(Player p)
    {
        var g = Global.Instance;
        var gdoc = g != null && g.mainCamera != null ? g.mainCamera.GetComponent<Bearroll.GDOC>() : null;
        if (gdoc == null) return;
        var root = p.transform;
        bool Mine(Bearroll.GDOC_Occludee o)
        {
            if (o == null) return false;
            if (o.transform.IsChildOf(root)) return true;
            if (o.renderer != null && o.renderer.transform.IsChildOf(root)) return true;
            var rs = o.renderers;
            if (rs != null)
                for (int i = 0; i < rs.Count; i++)
                    if (rs[i] != null && rs[i].transform.IsChildOf(root)) return true;
            return false;
        }
        var found = new List<Bearroll.GDOC_Occludee>();
        var all = Bearroll.GDOC.occludees;
        if (all != null)
            for (int i = 0; i < all.Length; i++)
                if (Mine(all[i])) found.Add(all[i]);
        foreach (var occ in found)
        {
            try { Bearroll.GDOC.RemoveOccludee(occ); } catch (System.Exception e) { Plugin.L.LogWarning("body: GDOC remove failed: " + e.Message); }
            Plugin.L.LogInfo($"body: {Diag.PathOf(occ.transform)} (state {occ.currentState}, renderer {Diag.Name(occ.renderer)}) taken out of occlusion culling");
        }
        if (found.Count > 0 || !loggedRenderers)
        {
            loggedRenderers = true;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
                if (!r.enabled || r.transform.lossyScale.sqrMagnitude < 1e-6f || !r.isVisible)
                    Plugin.L.LogInfo($"body: renderer {r.name} enabled={r.enabled} scale={Diag.V(r.transform.localScale)} visible={r.isVisible} shadow={r.shadowCastingMode}");
        }
    }
    static bool loggedRenderers;

    void HideHead()
    {
        if (hiddenHead.Count > 0 || head == null) return;
        foreach (var r in head.GetComponentsInChildren<Renderer>(true))
        {
            hiddenHead.Add((r, r.shadowCastingMode));
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        }
    }

    void RestoreHead()
    {
        foreach (var (r, mode) in hiddenHead)
            if (r != null) r.shadowCastingMode = mode;
        hiddenHead.Clear();
    }

    // End of frame: give the game its pose back.
    public void AfterRender()
    {
        if (!posed) return;
        for (int i = saved.Count - 1; i >= 0; i--)
        {
            var s = saved[i];
            if (s.T != null) { s.T.localPosition = s.Pos; s.T.localRotation = s.Rot; }
        }
        saved.Clear();
        posed = false;
    }

    // Dev: what the first-person body sees.
    public string Describe()
    {
        var g = Global.Instance;
        if (g == null || g.player == null) return "no player";
        Bind(g.player);
        var root = g.player.transform.position;
        var cam = g.mainCamera != null ? g.mainCamera.transform.position : Vector3.zero;
        string R(Transform t)
        {
            if (t == null) return "-";
            var r = t.GetComponentInChildren<Renderer>(true);
            return $"{t.name} y={t.position.y - root.y:F2} r={(r != null ? $"{r.name} on={r.enabled}/{r.gameObject.activeInHierarchy} layer={LayerMask.LayerToName(r.gameObject.layer)} shadow={r.shadowCastingMode} visible={r.isVisible}" : "-")}";
        }
        var lcd = g.mainCamera != null ? g.mainCamera.GetComponent<Camera>().layerCullDistances : null;
        int pl = LayerMask.NameToLayer("Player");
        string layerCull = lcd != null && pl >= 0 && pl < lcd.Length ? lcd[pl].ToString("F1") : "?";
        return $"body: live={FirstPersonLive} layerCull[Player]={layerCull} root={Diag.V(root)} cam={Diag.V(cam)} model={Diag.V(model != null ? model.position : Vector3.zero)} cam y={cam.y - root.y:F2} model fwd={Diag.V(model != null ? model.forward : Vector3.zero)} active={(model != null && model.gameObject.activeInHierarchy)} " +
               $"{R(head)} | {R(arms[0].Upper)} | {R(arms[0].Lower)} | {R(arms[0].Hand)} | " +
               $"hands L={cams.Left.Valid} R={cams.Right.Valid} lHandInCam={(arms[0].Hand != null && g.mainCamera != null ? Diag.V(g.mainCamera.transform.InverseTransformPoint(arms[0].Hand.position)) : "-")} " +
               $"lTarget={(cams.Left.Valid ? Diag.V(cams.ToWorld(cams.Left.Pos) - cam) : "-")} posed={posed}";
    }
}
