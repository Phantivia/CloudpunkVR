using System;
using InControl;
using UnityEngine;
using Pad = InControl.InputControlType;

namespace CloudpunkVR;

// Maps the controller layout (ControlLayout, edited with the layout tool) onto the virtual pad, using the game's own
// pad bindings, per situation:
//   menu/dialog: navigation, scroll and tabs
//   car (Aircar layout by default): gas, brake, steer, climb, strafe, cockpit/chase view
//   on foot: walk relative to where the head looks, smooth or snap turn, sneak
internal sealed class VRControls
{
    readonly SteamVRInput vr;
    readonly VirtualPad pad;
    readonly VRCameras cams;
    readonly ControlLayout layout;

    bool turnLatched;
    internal static string DebugLine = "";
    float climb;
    float recenterHeldFor, cockpitRetryAt, viewPulseUntil;
    bool wantCockpit = true;

    public VRControls(SteamVRInput vr, VirtualPad pad, VRCameras cams)
    {
        this.vr = vr;
        this.pad = pad;
        this.cams = cams;
        layout = new ControlLayout(vr);
    }

    public void Update()
    {
        if (!pad.TryAttach()) return;
        // InControl stops updating when the desktop window loses focus; in VR it rarely has focus.
        if (InputManager.SuspendInBackground) InputManager.SuspendInBackground = false;

        vr.Poll();
        layout.Update();
        pad.Clear();
        var g = Global.Instance;
        if (g == null) return;

        bool menu = g.IsInMenu() || g.HasBigWindowOpen() || g.IsInDialog(false);
        bool car = Global.IsInVehicle;
        float now = Time.unscaledTime;
        var L = layout;

        pad.Set(Pad.Action1, L.Button("common.interact") ? 1 : 0);
        if (DevCommands.Enabled) DebugLine = $"vr.a={vr.Value("a"):F1} interact={L.Button("common.interact")} menu={menu} car={car}";
        pad.Set(Pad.Action2, L.Button("common.back") ? 1 : 0);
        pad.Set(Pad.Action4, L.Button("common.inventory") ? 1 : 0);
        pad.Set(Pad.Start, L.Button("common.pause") ? 1 : 0);
        pad.Set(Pad.DPadRight, L.Button("common.hud") ? 1 : 0);
        // The map shares the left grip with the menus' previous-tab; outside menus it opens the map, and it closes it.
        if (L.Pressed("common.map") && (!menu || Global.FullMapOpen)) ActionInjector.Press("FullMap");
        if (L.Pressed("common.camera") && !menu)
        {
            if (car) { wantCockpit = !wantCockpit; viewPulseUntil = now + 0.15f; cockpitRetryAt = now + 3f; }
            else
            {
                VRBody.ThirdPerson = !VRBody.ThirdPerson;
                Plugin.L.LogInfo($"controls: {(VRBody.ThirdPerson ? "third" : "first")} person on foot");
            }
        }

        if (menu || !car) { climb = 0; VRAltitude.LastClimb = 0; }
        if (menu || car) turnRate = 0;
        if (menu)
        {
            float nx = L.Axis("menu.nav_x"), ny = L.Axis("menu.nav_y");
            pad.SetLeftStick(nx, ny);
            pad.SetRightStick(0, L.Axis("menu.scroll"));
            // The d-pad's right is also the game's show / hide HUD button, which still works in dialogues: a stick
            // pushed right while browsing a shop's offers hid the whole HUD. The stick moves the d-pad only in menus
            // and big windows, never in a dialogue.
            bool dpadNav = g.IsInMenu() || g.HasBigWindowOpen();
            pad.Set(Pad.DPadLeft, dpadNav && nx < -0.6f ? 1 : 0);
            pad.Set(Pad.DPadRight, dpadNav && nx > 0.6f || L.Button("common.hud") ? 1 : 0);
            pad.Set(Pad.LeftBumper, L.Button("menu.tab_left") ? 1 : 0);
            pad.Set(Pad.RightBumper, L.Button("menu.tab_right") ? 1 : 0);
        }
        else if (car)
        {
            pad.Set(Pad.RightTrigger, Mathf.Max(0, L.Axis("car.gas")));
            pad.Set(Pad.LeftTrigger, Mathf.Max(0, L.Axis("car.brake")));
            pad.SetLeftStick(L.Axis("car.steer"), 0);
            // Climb follows the stick at Aircar's pace (UE FInterpTo, speed 0.95); the game's own climb is snappier.
            float wantClimb = L.Axis("car.climb"), k = Plugin.Cfg.ClimbSmoothing.Value;
            climb = k > 0 ? climb + (wantClimb - climb) * Mathf.Clamp01(Time.unscaledDeltaTime * k) : wantClimb;
            VRAltitude.LastClimb = wantClimb;
            pad.SetRightStick(0, climb);
            float strafe = Mathf.Clamp(L.Axis("car.strafe") + (L.Button("car.strafe_right") ? 1 : 0) - (L.Button("car.strafe_left") ? 1 : 0), -1, 1);
            pad.Set(Pad.LeftBumper, Mathf.Max(0, -strafe));
            pad.Set(Pad.RightBumper, Mathf.Max(0, strafe));
            if (L.Pressed("car.view")) { wantCockpit = !wantCockpit; viewPulseUntil = now + 0.15f; cockpitRetryAt = now + 3f; }
        }
        else
        {
            float x = L.Axis("foot.move_x"), y = L.Axis("foot.move_y");
            // The game walks relative to its MoveTo frame (one fixed direction per camera area of the overhead
            // camera), not relative to the camera. Turn the stick from where the player looks (game camera plus head),
            // or where the left controller points, into that frame.
            var p = g.player;
            MeasureWalkFrame(p);
            float camYaw = g.mainCamera != null ? g.mainCamera.transform.eulerAngles.y : WalkFrame;
            float look = Plugin.Cfg.HeadRelativeWalk.Value || !cams.Left.Valid ? cams.HeadYaw : Yaw(cams.Left.Rot);
            float a = Mathf.DeltaAngle(WalkFrame, camYaw + look) * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            (x, y) = (x * c + y * s, -x * s + y * c);
            pad.SetLeftStick(x, y);
            NoteSentStick(x, y);
            pad.Set(Pad.LeftTrigger, Mathf.Max(0, L.Axis("foot.sneak")));
            Turn(g, now);
            HandCalibration(now);
        }

        if (now < viewPulseUntil) pad.Set(Pad.DPadUp, 1);
        KeepCockpit(g, car, menu, now);

        if (L.Button("common.recenter"))
        {
            recenterHeldFor += Time.unscaledDeltaTime;
            if (recenterHeldFor >= 0.5f && recenterHeldFor - Time.unscaledDeltaTime < 0.5f)
            {
                cams.Recenter();
                VRHost.UI?.Snap();
                vr.Rumble(0.08f, 0.6f);
            }
        }
        else recenterHeldFor = 0;
    }

    // Smooth turning as in VRChat: while the stick is pushed the player turns continuously, every rendered frame,
    // around the head, as far as wanted, faster the further the stick goes (gently near the centre for aiming at
    // something, the set speed at the edge). The rate eases in and out over a few frames so a flick does not jerk the
    // view. Snap turning stays available in the options.
    const float TurnCurve = 1.5f, TurnEase = 0.05f;
    float turnRate;

    void Turn(Global g, float now)
    {
        float tx = layout.Axis("foot.turn");
        if (!SnapTurning)
        {
            float want = Mathf.Sign(tx) * Mathf.Pow(Mathf.Abs(tx), TurnCurve) * Plugin.Cfg.SmoothTurnSpeed.Value;
            float dt = Time.unscaledDeltaTime;
            turnRate += (want - turnRate) * (1 - Mathf.Exp(-dt / TurnEase));
            if (want == 0 && Mathf.Abs(turnRate) < 1f) turnRate = 0;
            if (turnRate != 0) Rotate(turnRate * dt);
            return;
        }
        turnRate = 0;
        if (Mathf.Abs(tx) < 0.35f) { turnLatched = false; return; }
        if (turnLatched || Mathf.Abs(tx) < 0.7f) return;
        turnLatched = true;
        SnapTurn(Mathf.Sign(tx));
    }

    // Hand calibration: on foot, hold the left stick down for 2 s; one buzz a second for 3 s while the player holds the
    // controllers in front with the palms facing each other and the fingers pointing ahead (as if holding a box by
    // its sides); a long buzz when the pose is taken. From that pose each hand's back faces away from the other
    // controller and its knuckles point where the head faces, which gives the hand's rotation inside the controller.
    float calHeld, calAt = -1, calTick;

    void HandCalibration(float now)
    {
        if (calAt < 0)
        {
            calHeld = vr.Value("left_stick_click") > 0.5f ? calHeld + Time.unscaledDeltaTime : 0;
            if (calHeld < 2f) return;
            calHeld = 0;
            calAt = now + 3f;
            calTick = now + 1f;
            vr.Rumble(0.15f, 0.8f);
            Plugin.L.LogInfo("hands: calibration in 3 s (palms facing each other, fingers ahead)");
            return;
        }
        if (now >= calTick && now < calAt) { calTick += 1f; vr.Rumble(0.05f, 0.5f); }
        if (now < calAt) return;
        calAt = -1;
        bool ok = CalibrateHands(cams, out var report);
        Plugin.L.LogInfo("hands: " + report);
        vr.Rumble(ok ? 0.5f : 0.1f, ok ? 1f : 0.3f);
    }

    internal static bool CalibrateHands(VRCameras cams, out string report)
    {
        if (!cams.Left.Valid || !cams.Right.Valid) { report = "calibration skipped: a controller is not tracked"; return false; }
        if (!SolveGrip(cams.Left.Pos, cams.Left.Rot, cams.Right.Pos, cams.Right.Rot, cams.HeadRot, out var grip, out report)) return false;
        var c = Plugin.Cfg;
        c.HandPitch.Value = Mathf.Round(grip.x);
        c.HandYaw.Value = Mathf.Round(grip.y);
        c.HandRoll.Value = Mathf.Round(grip.z);
        report += $" -> pitch {c.HandPitch.Value} yaw {c.HandYaw.Value} roll {c.HandRoll.Value}";
        return true;
    }

    // The grip (pitch, yaw, roll as VRBody.Arm.Grip uses them, right-hand convention) that puts both hands palm to palm
    // with the knuckles pointing where the head faces, averaged over the two hands.
    internal static bool SolveGrip(Vector3 lp, Quaternion lr, Vector3 rp, Quaternion rr, Quaternion head, out Vector3 grip, out string report)
    {
        grip = default;
        var across = rp - lp;
        if (across.magnitude < 0.05f) { report = "calibration skipped: controllers on top of each other"; return false; }
        var ahead = Vector3.ProjectOnPlane(head * Vector3.forward, Vector3.up);
        if (ahead.sqrMagnitude < 1e-4f) ahead = Vector3.forward;
        var sum = Vector3.zero;
        var parts = new System.Text.StringBuilder("calibrated: ");
        foreach (float side in new[] { -1f, 1f })
        {
            var ctrl = side < 0 ? lr : rr;
            var back = (side < 0 ? -across : across).normalized;
            var fingers = Vector3.ProjectOnPlane(ahead, back).normalized;
            var want = Quaternion.LookRotation(fingers, Vector3.Cross(fingers, back * side));
            var e = (Quaternion.Inverse(ctrl) * want).eulerAngles;
            var g = new Vector3(Mathf.DeltaAngle(0, e.x), Mathf.DeltaAngle(0, e.y) * side, Mathf.DeltaAngle(0, e.z) * side);
            parts.Append($"{(side < 0 ? "L" : "R")} pitch {g.x:F1} yaw {g.y:F1} roll {g.z:F1}; ");
            sum += g;
        }
        grip = sum / 2;
        report = parts.ToString();
        return true;
    }

    static bool SnapTurning => string.Equals(Plugin.Cfg.TurnMode.Value, "snap", StringComparison.OrdinalIgnoreCase);

    // The world direction the game walks for "stick up" (its MoveTo frame). It has been 0 everywhere looked at, but
    // it is measured from what the game does with a steady stick, so a camera area with another frame still works.
    public static float WalkFrame;
    Vector2 sent, sentBefore;
    int steadyFrames;

    void NoteSentStick(float x, float y)
    {
        var now = new Vector2(x, y);
        // Steady means the direction sent did not turn at all (turning while walking turns it a degree or two a frame,
        // and the game's MoveToPoint trails it by a frame).
        steadyFrames = now.magnitude > 0.3f && sent.magnitude > 0.3f && Vector2.Angle(now, sent) < 0.3f ? steadyFrames + 1 : 0;
        sentBefore = sent;
        sent = now;
    }

    void MeasureWalkFrame(Player p)
    {
        if (p == null || p.moveToPoint == null || steadyFrames < 3) return;
        var lp = p.moveToPoint.localPosition;
        if (new Vector2(lp.x, lp.z).magnitude < 0.3f) return;
        float measured = Mathf.Atan2(lp.x, lp.z) * Mathf.Rad2Deg - Mathf.Atan2(sent.x, sent.y) * Mathf.Rad2Deg;
        // Frames seen so far are whole directions (0°); a change of a few degrees is measurement noise.
        if (Mathf.Abs(Mathf.DeltaAngle(WalkFrame, measured)) > 8f)
        {
            Plugin.L.LogInfo($"controls: walking frame {WalkFrame:F0} -> {Mathf.Repeat(measured, 360):F0}");
            WalkFrame = Mathf.Repeat(measured, 360);
        }
    }

    static float Yaw(Quaternion q)
    {
        var f = q * Vector3.forward;
        return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
    }

    public static void SnapTurn(float direction) => Rotate(direction * Plugin.Cfg.SnapTurnDegrees.Value);

    // Rotates the game's first-person look (CinemachinePOV yaw) so walking and interaction follow the new facing, and
    // turns the tracking space around the head with it, so the eyes stay where they are instead of swinging around
    // the calibrated centre when the player stands away from it.
    static void Rotate(float degrees)
    {
        var g = Global.Instance;
        var pov = g != null && g.player != null ? g.player.firstPersonPOV : null;
        if (pov == null) return;
        var axis = pov.m_HorizontalAxis;
        axis.Value += degrees;
        pov.m_HorizontalAxis = axis;
        VRCameras.Instance?.TurnAroundHead(degrees);
        VRHost.UI?.Turned(degrees);
    }

    // In VR the car is driven from the cockpit; the game toggles that view with its CameraCockpit pad button.
    void KeepCockpit(Global g, bool car, bool menu, float now)
    {
        if (!car || menu || !Plugin.Cfg.ForceCockpit.Value || now < cockpitRetryAt || SceneLoader.IsLoading()) return;
        if (g.playerCar == null || !g.playerCar.gameObject.activeInHierarchy) return;
        if (Global.IsInVehicleCockpit == wantCockpit) return;
        viewPulseUntil = now + 0.15f;
        cockpitRetryAt = now + 3f;
    }

    public void LevelFirstPersonPitch()
    {
        var g = Global.Instance;
        var pov = g != null && g.player != null ? g.player.firstPersonPOV : null;
        if (pov == null) return;
        var axis = pov.m_VerticalAxis;
        if (axis.Value == 0) return;
        axis.Value = 0;
        pov.m_VerticalAxis = axis;
    }
}
