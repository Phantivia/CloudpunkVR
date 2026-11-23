using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using InControl;
using UnityEngine;
using UnityEngine.AI;

namespace CloudpunkVR.Coo;

// Navigation for Coo: a route to a quest marker, a parking spot or a point, drawn on the minimap and the full map;
// optionally driven (autopilot) or walked. Driving goes through the game's own car controls, as a player would on a
// pad: throttle, brake, steering, climb and strafe on the virtual pad, so speeds, limits, floors and fuel are the
// game's. The car parks with the interact button once it is inside the parking spot. In VR, touching the sticks or
// triggers hands the car back to the player at once.
internal sealed class CooPilot
{
    readonly VirtualPad pad;
    readonly CooBridge bridge;
    readonly CooRoutes routes = new();
    readonly CooRouteLine line = new();

    // Lift: the car is in one of the game's lifts on the way to a quest on another level; the route to the quest is
    // planned again once it is out on the other side.
    public enum Phase { Off, Guide, Drive, Approach, Parking, Walk, Lift, Arrived }
    Phase phase = Phase.Off;
    List<Vector3> route;
    int progress;
    Vector3 goal;
    // where the route ends: the parking spot, the lift's trigger, or the goal itself
    Vector3 routeEnd;
    string goalLabel = "";
    ParkVolume goalPark;
    // the quest is on another level: the route goes into the lift the game's own quest marker points at
    bool viaLift;
    JsonObject navArgs;
    int liftHops;
    float liftSince, outOfLiftSince = -1;
    bool drive;
    float stuckSince = -1, reverseUntil, lastReplan, startedAt, parkPressedAt, offSince = -1;
    bool lastReplanFailed;
    // the end can be reached in a straight line from here (checked a few times a second)
    bool endInSight;
    float nextSightCheck, endBlockedSince = -1, heightStuckSince = -1;

    // a route being planned, a slice of it each frame
    CooRoutes.Job planning;
    Action<List<Vector3>, string> planned;
    public static double PlanBudgetMs = 1.5;
    // turn prompts already given on this route (by where along it they are), and the arrival one
    readonly HashSet<int> announced = new();
    bool announcedArrival;
    float travelled;
    public float SpeedFactor = 0.85f;
    int carMask;
    float nextLog;

    // what the autopilot asks of the car this frame
    float gas, brake, steer, climb, strafe;
    Vector2 walkStick;

    public CooPilot(VirtualPad pad, CooBridge bridge)
    {
        this.pad = pad;
        this.bridge = bridge;
    }

    public JsonObject Describe()
    {
        var o = new JsonObject { ["phase"] = phase.ToString().ToLowerInvariant() };
        if (planning != null) o["planning"] = true;
        if (phase == Phase.Off) return o;
        o["target"] = goalLabel;
        if (viaLift) o["viaLift"] = true;
        o["goal"] = CooGame.V(goal);
        o["remainingM"] = Mathf.RoundToInt(Remaining());
        if (route != null && phase == Phase.Guide)
        {
            var m = NextManeuver(CooGame.PlayerPos(), 800f);
            if (m.kind != null) o["next"] = new JsonObject { ["kind"] = m.kind, ["inM"] = Mathf.RoundToInt(m.at) };
        }
        return o;
    }

    float Remaining()
    {
        if (route == null || route.Count == 0) return Vector3.Distance(CooGame.PlayerPos(), goal);
        float s = Vector3.Distance(CooGame.PlayerPos(), route[Mathf.Min(progress + 1, route.Count - 1)]);
        for (int i = progress + 1; i < route.Count - 1; i++) s += Vector3.Distance(route[i], route[i + 1]);
        return s;
    }

    // ---- requests

    // Plans a route and starts showing (or driving) it. In the car the route is planned over several frames and done
    // is called when it is ready; the route shown before stays until then. On foot done is called right away.
    public void Navigate(JsonObject args, Action<JsonNode, string> done) => Navigate(args, done, false);

    void Navigate(JsonObject args, Action<JsonNode, string> done, bool afterLift)
    {
        try { Begin(args, done, afterLift); }
        catch (CooError e) { done(null, e.Message); }
    }

    void Begin(JsonObject args, Action<JsonNode, string> done, bool afterLift)
    {
        var g = Global.Instance;
        if (g == null) throw new CooError("游戏还没进入");
        bool wantDrive = (bool?)args["drive"] ?? false;
        string mode = CooGame.Mode();
        var from = CooGame.PlayerPos();
        ResolveTarget(args, from, out var tGoal, out var tLabel, out var tPark, out var tLift);
        bool inCar = mode == "car" || mode == "car_chase";
        if (!inCar && mode != "foot") throw new CooError($"现在不能导航（{mode}）");
        if (inCar)
        {
            // a car goes to a parking spot near a story marker; parking, garage and gas markers are spots already.
            // A quest on another level: into the lift the game's marker points at.
            if (tLift == null && tPark == null && (bool?)args["park"] != false) tPark = NearestPark(tGoal, 260f);
            var dest = tLift != null ? LiftCenter(tLift) : tPark != null ? ParkCenter(tPark) : tGoal;
            StartPlan(from, dest, (r, why) =>
            {
                if (r == null) { done(null, "规划失败：" + why); return; }
                if (!(CooGame.Mode() is "car" or "car_chase")) { done(null, "规划好时已经不在车里了"); return; }
                Start(args, wantDrive ? Phase.Drive : Phase.Guide, r, tGoal, dest, tLabel, tPark, tLift != null, afterLift);
                done(Result(from), null);
            });
            return;
        }
        var walk = WalkRoute(from, tGoal, out var whyWalk);
        if (walk == null) throw new CooError("规划失败：" + whyWalk);
        CancelPlanning("换成了步行路线");
        Start(args, wantDrive ? Phase.Walk : Phase.Guide, walk, tGoal, walk[walk.Count - 1], tLabel, null, false, afterLift);
        done(Result(from), null);
    }

    void Start(JsonObject args, Phase p, List<Vector3> r, Vector3 tGoal, Vector3 end, string label, ParkVolume park, bool lift, bool afterLift)
    {
        navArgs = (JsonObject)JsonNode.Parse(args.ToJsonString());
        if (!afterLift) liftHops = 0;
        drive = p == Phase.Drive || p == Phase.Walk;
        goal = tGoal;
        routeEnd = end;
        goalLabel = label;
        goalPark = park;
        viaLift = lift;
        route = r;
        progress = 0;
        phase = p;
        startedAt = lastReplan = Time.unscaledTime;
        stuckSince = offSince = endBlockedSince = -1;
        endInSight = false;
        lastReplanFailed = false;
        announced.Clear();
        announcedArrival = false;
        line.Show(route);
        Plugin.L.LogInfo($"coo: navigate to {goalLabel} {CooGame.V(goal).ToJsonString()} {(drive ? "driving" : "guiding")}{(viaLift ? " via a lift" : "")}, route {route.Count} points {CooRoutes.Length(route):F0} m" +
                         (DevCommands.Enabled && route.Count <= 12 ? ": " + string.Join(" ", route.ConvertAll(Diag.V)) : ""));
    }

    JsonObject Result(Vector3 from)
    {
        var res = new JsonObject
        {
            ["target"] = goalLabel,
            ["goal"] = CooGame.V(goal),
            ["routeM"] = Mathf.RoundToInt(CooRoutes.Length(route)),
            ["straightM"] = Mathf.RoundToInt(Vector3.Distance(from, routeEnd)),
            ["phase"] = phase.ToString().ToLowerInvariant(),
        };
        if (goalPark != null) res["parking"] = CooGame.V(ParkCenter(goalPark));
        if (viaLift) res["viaLift"] = true;
        return res;
    }

    public void WalkTo(JsonObject args, Action<JsonNode, string> done)
    {
        if (CooGame.Mode() != "foot") { done(null, "只能在步行时走过去"); return; }
        args["drive"] = true;
        Navigate(args, done);
    }

    void StartPlan(Vector3 from, Vector3 to, Action<List<Vector3>, string> then)
    {
        CancelPlanning("被新的导航请求取代");
        var car = Global.Instance != null ? Global.Instance.playerCar : null;
        planning = routes.Plan(from, to, car != null ? car.layerMinY : -1000f, car != null ? car.layerMaxY : 1000f);
        planned = then;
        StepPlanning();
    }

    void CancelPlanning(string why)
    {
        if (planning == null) return;
        var cb = planned;
        planning = null;
        planned = null;
        cb(null, why);
    }

    void StepPlanning()
    {
        if (planning == null) return;
        planning.Step(PlanBudgetMs);
        if (!planning.Done) return;
        var j = planning;
        var cb = planned;
        planning = null;
        planned = null;
        Plugin.L.LogInfo($"coo: planning took {j.Frames} frames, {j.Ms:F0} ms in all{(j.Route == null ? $" (no route: {j.Why})" : "")}");
        cb(j.Route, j.Why ?? "规划失败");
    }

    static Vector3 LiftCenter(Lift l)
    {
        var c = l.GetComponent<Collider>();
        return c != null ? c.bounds.center : l.transform.position;
    }

    // The lift the game's quest marker stands at (the game moves the marker of a quest on another level to the lift
    // that leads toward it, MinimapObject.Setup).
    static Lift LiftAt(Vector3 p, float max)
    {
        Lift best = null;
        float bd = max;
        foreach (var l in UnityEngine.Object.FindObjectsOfType<Lift>())
        {
            float d = Mathf.Min(Vector3.Distance(l.transform.position, p), Vector3.Distance(LiftCenter(l), p));
            if (d < bd) { bd = d; best = l; }
        }
        return best;
    }

    void ResolveTarget(JsonObject args, Vector3 from, out Vector3 pos, out string label, out ParkVolume park, out Lift lift)
    {
        park = null;
        lift = null;
        string questId = (string)args["quest"];
        string place = (string)args["place"];
        if (args["pos"] is JsonArray p && p.Count == 3)
        {
            pos = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            label = (string)args["label"] ?? "指定位置";
            // dev: treat the lift nearest the point as the way to another level
            if ((bool?)args["devLift"] == true) lift = LiftAt(pos, 80f);
            return;
        }
        if (place == "parking")
        {
            park = NearestPark(from, 2000f) ?? throw new CooError("附近没有停车位");
            pos = ParkCenter(park);
            label = "最近的停车位";
            return;
        }
        if (!string.IsNullOrEmpty(place) && place != "car")
        {
            // a place on the map: an id from the place search, or a name / kind ("最近的加油站")
            var hit = CooPlaces.Resolve(place, from) ?? throw new CooError($"地图上没找到「{place}」");
            if (hit.Quest) { questId = hit.Id.Substring("quest:".Length); place = null; }
            else
            {
                pos = hit.Pos;
                label = hit.Name == hit.Kind || hit.Kind.Length == 0 ? hit.Name : $"{hit.Name}({hit.Kind})";
                return;
            }
        }
        if (place == "car")
        {
            var car = Global.Instance.playerCar;
            if (car == null) throw new CooError("找不到车");
            pos = car.transform.position;
            label = "车";
            return;
        }
        // a quest: the one named, or the nearest with a marker
        QuestText best = null;
        float bestD = float.MaxValue;
        foreach (var q in CooGame.ActiveQuests())
        {
            if (!CooGame.HasMarker(q)) continue;
            if (questId != null && q.id != questId) continue;
            float d = Vector3.Distance(from, CooGame.MarkerPos(q, out _));
            if (d < bestD) { bestD = d; best = q; }
        }
        if (best == null) throw new CooError(questId != null ? $"没有带标记的任务 {questId}" : "当前没有带地图标记的任务");
        pos = CooGame.MarkerPos(best, out bool otherLevel);
        label = CooGame.Clean(best.text);
        if (otherLevel)
        {
            lift = LiftAt(pos, 80f);
            label += "(在另一层，先进升降梯)";
            Plugin.L.LogInfo($"coo: quest {best.id} is in scene {best.markerInScene}, the game's marker is at {Diag.V(pos)}, lift {(lift != null ? $"{Diag.PathOf(lift.transform)} to scene {lift.destination} at {Diag.V(LiftCenter(lift))}" : "none within 80 m")}");
            return;
        }
        if (best.markerType == QuestText.Marker.Parking || best.markerType == QuestText.Marker.Garage || best.markerType == QuestText.Marker.GasStation)
            park = NearestPark(pos, 40f);
    }

    static Vector3 ParkCenter(ParkVolume v) => v.volumeCollider != null ? v.volumeCollider.bounds.center : v.transform.position;

    static ParkVolume NearestPark(Vector3 p, float max)
    {
        ParkVolume best = null;
        float bd = max;
        foreach (var v in UnityEngine.Object.FindObjectsOfType<ParkVolume>())
        {
            if (v.volumeCollider == null || !v.isActiveAndEnabled) continue;
            float d = Vector3.Distance(ParkCenter(v), p);
            if (d < bd) { bd = d; best = v; }
        }
        return best;
    }

    static List<Vector3> WalkRoute(Vector3 from, Vector3 to, out string why)
    {
        why = null;
        var path = new NavMeshPath();
        // this build keeps only NavMesh.CalculatePath: try the target, the ground under it, then points around it
        var tries = new List<Vector3> { to };
        if (Physics.Raycast(to + Vector3.up * 2f, Vector3.down, out var hit, 30f, ~0, QueryTriggerInteraction.Ignore)) tries.Add(hit.point);
        for (int r = 2; r <= 8; r += 3)
            for (int k = 0; k < 8; k++)
                tries.Add(tries[tries.Count > 1 ? 1 : 0] + Quaternion.Euler(0, k * 45, 0) * Vector3.forward * r);
        bool ok = false;
        foreach (var t in tries)
            if (NavMesh.CalculatePath(from, t, -1, path) && path.status != NavMeshPathStatus.PathInvalid && path.corners.Length > 0) { ok = true; break; }
        if (!ok) { why = "找不到走过去的路"; return null; }
        var corners = path.corners;
        var list = new List<Vector3>();
        for (int i = 0; i < corners.Length; i++) list.Add(corners[i]);
        if (path.status == NavMeshPathStatus.PathPartial) Plugin.L.LogInfo("coo: walking route is partial");
        return list;
    }

    public void Stop(string why)
    {
        CancelPlanning(why == "asked" ? "导航被叫停了" : why);
        if (phase == Phase.Off) return;
        var was = phase;
        phase = Phase.Off;
        viaLift = false;
        route = null;
        line.Hide();
        ReleaseInputs();
        Plugin.L.LogInfo($"coo: navigation stopped ({why})");
        if (why != "asked") bridge.SendEvent("autopilot", new JsonObject { ["state"] = "off", ["why"] = why, ["was"] = was.ToString().ToLowerInvariant() });
    }

    void ReleaseInputs()
    {
        gas = brake = steer = climb = strafe = 0;
        walkStick = Vector2.zero;
        if (!UnityEngine.XR.XRSettings.enabled) pad.Clear();
    }

    // ---- per frame

    public void Update()
    {
        var g = Global.Instance;
        string mode = CooGame.Mode();
        if (planning != null)
        {
            if (g == null || mode == "loading") CancelPlanning("场景切换");
            else StepPlanning();
        }
        if (phase == Phase.Off) return;
        float now = Time.unscaledTime;
        if (phase == Phase.Lift) { InLift(g, mode, now); return; }
        bool inCar = mode == "car" || mode == "car_chase";
        // the car has gone into the lift (the game drives it and loads the other level): wait, then plan on from there
        if (viaLift && (mode == "loading" || inCar && g.playerCar != null && g.playerCar.isInLift))
        {
            phase = Phase.Lift;
            liftSince = now;
            outOfLiftSince = -1;
            route = null;
            line.Hide();
            ReleaseInputs();
            Plugin.L.LogInfo("coo: in the lift, planning on once out");
            return;
        }
        if (g == null || mode == "loading") { Stop("场景切换"); return; }
        if (route == null) return;

        // the player takes over: any real stick or trigger input while the autopilot drives (VR only; flat mode has
        // no player on the pad when Coo drives)
        if ((phase == Phase.Drive || phase == Phase.Approach || phase == Phase.Walk) && UnityEngine.XR.XRSettings.enabled && PlayerIsSteering())
        {
            phase = Phase.Guide;
            ReleaseInputs();
            bridge.SendEvent("autopilot", new JsonObject { ["state"] = "guide", ["why"] = "玩家接管了操作，继续显示路线" });
        }
        var pos = CooGame.PlayerPos();
        AdvanceProgress(pos);

        if (phase == Phase.Guide)
        {
            ReleaseInputs();
            // a lift ends the route only once the car is in it
            if (!viaLift && Vector3.Distance(pos, route[route.Count - 1]) < (inCar ? 25f : 4f)) { Arrive(inCar ? "到达附近" : "走到了"); return; }
            GuideOffRoute(pos, inCar, now);
            if (route != null) Announce(pos, inCar, g);
            return;
        }
        if (phase == Phase.Walk)
        {
            if (!(mode == "foot")) { if (mode != "dialogue") Stop("不在步行状态"); return; }
            Walk(pos);
            return;
        }
        if (!inCar)
        {
            // parking ends with the game putting Rania on foot
            if (phase == Phase.Parking && mode == "foot") { Arrive("已停车"); return; }
            if (mode != "dialogue" && mode != "map") Stop("下车了");
            return;
        }
        var car = g.playerCar;
        if (car == null) { Stop("找不到车"); return; }
        if (phase == Phase.Parking)
        {
            DriveParking(car, now);
            return;
        }
        Drive(car, pos, now);
    }

    // In the lift and through the level load: once the car is out and flying for a moment, the same request again
    // from the new level (the quest marker may point at a further lift).
    void InLift(Global g, string mode, float now)
    {
        bool inCar = mode == "car" || mode == "car_chase";
        bool out_ = g != null && inCar && g.playerCar != null && !g.playerCar.isInLift;
        if (mode == "foot") { Stop("出升降梯时下车了"); return; }
        if (now - liftSince > 120f) { Stop("升降梯两分钟没出来"); return; }
        if (!out_) { outOfLiftSince = -1; return; }
        if (outOfLiftSince < 0) { outOfLiftSince = now; return; }
        if (now - outOfLiftSince < 1.5f || planning != null) return;
        if (++liftHops > 4) { Stop("连着换了太多次升降梯"); return; }
        var args = navArgs;
        string label = goalLabel;
        phase = Phase.Off;
        viaLift = false;
        Navigate(args, (r, err) =>
        {
            if (err != null)
            {
                Plugin.L.LogInfo($"coo: after the lift: {err}");
                bridge.SendEvent("autopilot", new JsonObject { ["state"] = "off", ["why"] = "出了升降梯后没规划出路线：" + err, ["was"] = "lift" });
                return;
            }
            bridge.SendEvent("rerouted", new JsonObject { ["target"] = goalLabel, ["routeM"] = (int?)r["routeM"], ["afterLift"] = true });
        }, true);
        if (phase == Phase.Off && planning != null) Plugin.L.LogInfo($"coo: out of the lift, planning on to {label}");
    }

    float DistanceToRoute(Vector3 pos)
    {
        float best = float.MaxValue;
        for (int i = Mathf.Max(0, progress - 1); i < Mathf.Min(route.Count - 1, progress + 8); i++)
        {
            var a = route[i];
            var ab = route[i + 1] - a;
            float t = Mathf.Clamp01(Vector3.Dot(pos - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
            best = Mathf.Min(best, Vector3.Distance(a + ab * t, pos));
        }
        return best;
    }

    // The player drives (or walks) their own way: off the route for 3 s, a new one from here, at most every 6 s (20 s
    // after a plan that found nothing). The car's is planned over the next frames; the old route stays until then.
    void GuideOffRoute(Vector3 pos, bool inCar, float now)
    {
        if (DistanceToRoute(pos) < (inCar ? 55f : 12f)) { offSince = -1; return; }
        if (offSince < 0) { offSince = now; return; }
        if (now - offSince < 3f || now - lastReplan < (lastReplanFailed ? 20f : 6f) || planning != null) return;
        offSince = -1;
        if (inCar) { Replan(pos, true); return; }
        lastReplan = now;
        var r = WalkRoute(pos, goal, out _);
        lastReplanFailed = r == null;
        if (r == null) return;
        route = r;
        routeEnd = r[r.Count - 1];
        progress = 0;
        line.Show(route);
        Rerouted();
    }

    void Rerouted()
    {
        announced.Clear();
        Plugin.L.LogInfo($"coo: rerouted to {goalLabel}, {CooRoutes.Length(route):F0} m");
        bridge.SendEvent("rerouted", new JsonObject { ["target"] = goalLabel, ["routeM"] = Mathf.RoundToInt(CooRoutes.Length(route)) });
    }

    // The next turn or climb along the route: what and how far ahead. A turn is a change of heading of 40° or more
    // within 30 m; a climb or descent 15 m of height within 30 m.
    (string kind, float at) NextManeuver(Vector3 pos, float within)
    {
        const float step = 10f;
        var pts = new List<Vector3>();
        for (float d = 0; d <= within + 40f; d += step) pts.Add(Lookahead(pos, d));
        for (int i = 0; i + 4 < pts.Count; i++)
        {
            var h0 = Flat(pts[i + 1] - pts[i]);
            var h1 = Flat(pts[i + 4] - pts[i + 3]);
            float dy = pts[i + 3].y - pts[i].y;
            if (Mathf.Abs(dy) > 15f) return (dy > 0 ? "up" : "down", i * step);
            if (h0.sqrMagnitude < 1f || h1.sqrMagnitude < 1f) continue;
            float a = Vector3.SignedAngle(h0, h1, Vector3.up);
            if (Mathf.Abs(a) >= 40f) return (Mathf.Abs(a) > 135f ? "uturn" : a > 0 ? "right" : "left", i * step + 15f);
            if ((pts[i + 1] - route[route.Count - 1]).sqrMagnitude < 1f) break;
        }
        return (null, 0);
    }

    // Like a car navigator: a prompt for each turn or climb when it is about eight seconds (at least 120 m) ahead, and
    // one when the destination is close. The World turns them into events for the bot to say.
    void Announce(Vector3 pos, bool inCar, Global g)
    {
        float speed = inCar && g.playerCar != null ? g.playerCar.currentSpeed : 1.5f;
        float reach = inCar ? Mathf.Max(120f, speed * 8f) : 25f;
        float left = Remaining();
        travelled = CooRoutes.Length(route) - left;
        if (!announcedArrival && left < Mathf.Max(inCar ? 200f : 20f, reach))
        {
            announcedArrival = true;
            bridge.SendEvent("maneuver", new JsonObject { ["kind"] = viaLift ? "lift" : "arrive", ["inM"] = Mathf.RoundToInt(left), ["target"] = goalLabel });
            return;
        }
        var m = NextManeuver(pos, reach);
        if (m.kind == null || m.at > reach) return;
        int key = Mathf.RoundToInt((travelled + m.at) / 40f);
        if (announced.Contains(key) || announced.Contains(key - 1) || announced.Contains(key + 1)) return;
        announced.Add(key);
        bridge.SendEvent("maneuver", new JsonObject { ["kind"] = m.kind, ["inM"] = Mathf.RoundToInt(m.at), ["remainingM"] = Mathf.RoundToInt(left), ["target"] = goalLabel });
    }

    void AdvanceProgress(Vector3 pos)
    {
        // the route point the player has reached: the furthest point within reach along the next few segments
        int best = progress;
        float bestD = float.MaxValue;
        for (int i = progress; i < Mathf.Min(route.Count - 1, progress + 8); i++)
        {
            var a = route[i];
            var b = route[i + 1];
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(pos - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
            // height counts double: a stretch overhead (the level part after a climb) is not where the car is yet
            var off = a + ab * t - pos;
            float d = Flat(off).sqrMagnitude + off.y * off.y * 4f;
            if (d < bestD) { bestD = d; best = i; }
        }
        progress = best;
    }

    // The point of the current stretch nearest the car.
    Vector3 OnRoute(Vector3 pos)
    {
        var a = route[progress];
        var ab = route[Mathf.Min(progress + 1, route.Count - 1)] - a;
        return a + ab * Mathf.Clamp01(Vector3.Dot(pos - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
    }

    Vector3 Lookahead(Vector3 pos, float dist)
    {
        var a = route[progress];
        var b = route[Mathf.Min(progress + 1, route.Count - 1)];
        var ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(pos - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
        var p = a + ab * t;
        // distance 0 is the car's own point on the route (it fell through to the route's end, and the turn prompts
        // said "0 米后下降" for the height of the destination)
        if (dist <= 0) return p;
        int i = progress;
        while (dist > 0 && i < route.Count - 1)
        {
            var next = route[i + 1];
            float seg = Vector3.Distance(p, next);
            if (seg >= dist) return p + (next - p) * (dist / Mathf.Max(seg, 1e-4f));
            dist -= seg;
            p = next;
            i++;
        }
        return route[route.Count - 1];
    }

    // How sharply the route bends within the next stretch, in degrees, up and down included: a climb that levels
    // off over a deck is a corner too, and was flown past at full speed.
    float BendAhead(Vector3 pos, float within)
    {
        float total = 0;
        var prevDir = Lookahead(pos, 6f) - pos;
        for (float d = 15; d <= within; d += 15)
        {
            var dir = Lookahead(pos, d) - Lookahead(pos, d - 15);
            if (dir.sqrMagnitude < 1e-4f) continue;
            total = Mathf.Max(total, Vector3.Angle(prevDir, dir));
        }
        return total;
    }

    static Vector3 Flat(Vector3 v) { v.y = 0; return v; }

    void Drive(PlayerCar car, Vector3 pos, float now)
    {
        var t = car.transform;
        var rb = car.rBody;
        var vel = rb != null ? rb.velocity : Vector3.zero;
        float speed = Flat(vel).magnitude;
        float remaining = Remaining();

        // Close to the parking spot or the lift: straight in, but only once nothing is in between. Parking decks have
        // a roof and a floor; going straight from 30 m below or above flew through them (11-10). Until then the
        // route's own last stretch, planned through free space, leads there.
        bool intoEnd = goalPark != null || viaLift;
        if (intoEnd)
        {
            if (goalPark != null) routeEnd = ParkCenter(goalPark);
            float d = Vector3.Distance(pos, routeEnd);
            if (d < 30f && now >= nextSightCheck)
            {
                nextSightCheck = now + 0.2f;
                endInSight = ClearLine(pos, routeEnd);
            }
            if (d >= 30f) endInSight = false;
            if (phase == Phase.Drive && endInSight) { phase = Phase.Approach; endBlockedSince = -1; }
            else if (phase == Phase.Approach && !endInSight)
            {
                // something came between (or the line only grazed past): back to the route after a moment
                if (endBlockedSince < 0) endBlockedSince = now;
                else if (now - endBlockedSince > 0.6f) phase = Phase.Drive;
            }
            else endBlockedSince = -1;
        }
        if (!intoEnd && remaining < 20f) { Arrive("到达目的地附近"); return; }

        // a shorter look ahead near the end, so the last bends of the route are not cut through
        float look = Mathf.Clamp(8f + speed * 0.9f, remaining < 40f ? 5f : 10f, 45f);
        var target = phase == Phase.Approach ? routeEnd : Lookahead(pos, look);
        var to = target - pos;
        float heading = Vector3.SignedAngle(Flat(t.forward), Flat(to), Vector3.up);
        float yawRate = rb != null ? rb.angularVelocity.y * Mathf.Rad2Deg : 0;
        steer = Mathf.Clamp(heading / 35f - yawRate * 0.012f, -1, 1);
        // the next stretch is mostly up or down (out of a parking deck, down to a pad): climb on the spot
        bool vertical = Flat(to).magnitude < 7f && Mathf.Abs(to.y) > 2.5f;
        if (vertical) steer = 0;

        // speed: the game's limit here, less ahead of bends and when the end comes near
        float vmax = Mathf.Max(car.currentMaxSpeed, 10f) * SpeedFactor;
        float bend = BendAhead(pos, Mathf.Clamp(speed * 2.5f, 30f, 90f));
        float vt = vmax * (1f - Mathf.Clamp01(bend / 100f) * 0.65f);
        vt = Mathf.Min(vt, Mathf.Sqrt(2f * 3.5f * Mathf.Max(remaining - 6f, 0f)) + 3f);
        if (Mathf.Abs(heading) > 60f) vt = Mathf.Min(vt, 8f);
        if (vertical) vt = Mathf.Min(vt, 2f);
        // The car climbs and sinks at about 5 m/s: no faster forward than lets it keep to the slope ahead. Flying a
        // steep climb at lane speed put it under the deck it was climbing past (11-10).
        float run = Flat(to).magnitude;
        if (run > 0.5f && Mathf.Abs(to.y) > 0.25f * run) vt = Mathf.Min(vt, Mathf.Max(1.5f, 5f * run / Mathf.Abs(to.y)));
        // Well above or below the route where the car is: get to its height before going on. The car climbs slower
        // than it flies, and a route that rises beside a parking deck and then goes in over it was flown under the
        // deck and up into it (11-10).
        // (only on a stretch flatter than 45°: on a steep one the height along it is not the car's to match)
        float heightOff = 0f;
        if (phase != Phase.Approach && progress + 1 < route.Count)
        {
            var seg = route[progress + 1] - route[progress];
            if (Mathf.Abs(seg.y) < Flat(seg).magnitude) heightOff = OnRoute(pos).y - pos.y;
        }
        if (Mathf.Abs(heightOff) > 3f) vt = Mathf.Min(vt, Mathf.Abs(heightOff) > 6f ? 0.5f : 3f);
        if (phase == Phase.Approach) vt = Mathf.Min(vt, Mathf.Max(3f, Flat(to).magnitude * 0.35f));
        // something straight ahead: slow down, rise if there is room
        float probe = speed * 1.4f + 10f;
        if (carMask == 0) carMask = CooSpace.CarMask();
        bool blocked = Physics.SphereCast(pos, 2.0f, Flat(t.forward).normalized, out var hit, probe, carMask, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(t);
        if (blocked) vt = Mathf.Min(vt, Mathf.Max(4f, hit.distance * 0.4f));

        // the brake is also reverse: judge by the speed forward, or slowing down while rolling back reverses harder
        float forward = Vector3.Dot(Flat(vel), Flat(t.forward).normalized);
        gas = Mathf.Clamp01((vt - forward) / 4f + (vt > 1 ? 0.15f : 0));
        brake = forward > vt + 2.5f ? Mathf.Clamp01((forward - vt) / 6f) : 0;
        if (brake > 0) gas = 0;

        // height: the route's, kept inside the floor's allowed band
        float ty = target.y;
        if (blocked && hit.distance < probe * 0.6f && !vertical) ty = Mathf.Max(ty, pos.y + 5f);
        ty = Mathf.Clamp(ty, car.layerMinY + 3f, car.layerMaxY - 3f);
        climb = Mathf.Clamp((ty - pos.y) / 6f - vel.y * 0.25f, -1, 1);
        // held back to climb but not climbing (something overhead): treated like being stuck below
        if (Mathf.Abs(heightOff) > 3f && Mathf.Abs(climb) > 0.5f && Mathf.Abs(vel.y) < 0.3f && now - startedAt > 3f)
        {
            if (heightStuckSince < 0) heightStuckSince = now;
            else if (now - heightStuckSince > 3f)
            {
                heightStuckSince = -1;
                reverseUntil = now + 1.6f;
                if (now - lastReplan > 4f && planning == null) Replan(pos, false);
            }
        }
        else heightStuckSince = -1;
        // sideways error to the route, mostly useful when slow
        var right = Flat(t.right).normalized;
        float lateral = Vector3.Dot(target - pos, right);
        strafe = phase == Phase.Approach ? Mathf.Clamp(lateral / 4f, -1, 1) : speed < 12f ? Mathf.Clamp(lateral / 8f, -0.6f, 0.6f) : 0;

        // stuck against something: back off a moment, then plan again
        if (now < reverseUntil) { gas = 0; brake = 1; steer = -steer; climb = 0.6f; return; }
        if (gas > 0.5f && speed < 1.2f && now - startedAt > 3f)
        {
            if (stuckSince < 0) stuckSince = now;
            else if (now - stuckSince > 2.5f)
            {
                stuckSince = -1;
                reverseUntil = now + 1.6f;
                if (now - lastReplan > 4f && planning == null) Replan(pos, false);
            }
        }
        else stuckSince = -1;

        // in the spot: from the straight approach, or at the end of the route when the end never came in sight
        if ((phase == Phase.Approach || remaining < 10f) && goalPark != null && car.parkingVolume != null)
        {
            phase = Phase.Parking;
            parkPressedAt = -1;
        }
        if (DevCommands.Enabled && now >= nextLog)
        {
            nextLog = now + 0.5f;
            Plugin.L.LogInfo($"pilot: {phase} p={progress}/{route.Count} pos={Diag.V(pos)} v={speed:F1} vt={vt:F1} head={heading:F0} yawRate={yawRate:F0} tgt={Diag.V(target - pos)} " +
                             $"gas={gas:F2} brake={brake:F2} steer={steer:F2} climb={climb:F2} strafe={strafe:F2} blocked={blocked}{(blocked ? $"@{hit.distance:F0} {hit.collider.name}" : "")} band=[{car.layerMinY:F0},{car.layerMaxY:F0}] rem={remaining:F0}");
        }
    }

    void DriveParking(PlayerCar car, float now)
    {
        var rb = car.rBody;
        float speed = rb != null ? Flat(rb.velocity).magnitude : 0;
        gas = steer = climb = strafe = 0;
        brake = speed > 0.8f ? 1 : 0;
        if (car.parkingVolume == null && parkPressedAt < 0) { phase = Phase.Approach; return; }
        if (speed < 1.5f && parkPressedAt < 0)
        {
            parkPressedAt = now;
            CooHost.Instance?.Press(InputControlType.Action1, 0.2f);
        }
        if (parkPressedAt > 0 && (now - parkPressedAt > 2.5f || !Global.IsInVehicle)) Arrive("已停车");
    }

    void Walk(Vector3 pos)
    {
        var target = Lookahead(pos, 2.5f);
        var to = Flat(target - pos);
        if (to.magnitude < 0.3f || Vector3.Distance(Flat(pos), Flat(route[route.Count - 1])) < 1.4f)
        {
            Arrive("走到了");
            return;
        }
        // the game walks relative to its MoveTo frame (VRControls measures it); stick = world direction there
        var g = Global.Instance;
        float frame = VRControls.WalkFrame;
        var dir = Quaternion.Euler(0, -frame, 0) * to.normalized;
        walkStick = new Vector2(dir.x, dir.z);
    }

    void Arrive(string why)
    {
        var was = phase;
        phase = Phase.Off;
        viaLift = false;
        CancelPlanning("已经到了");
        ReleaseInputs();
        line.Hide();
        route = null;
        Plugin.L.LogInfo($"coo: arrived ({why}) at {goalLabel}");
        bridge.SendEvent("arrived", new JsonObject { ["target"] = goalLabel, ["how"] = why, ["was"] = was.ToString().ToLowerInvariant(), ["mode"] = CooGame.Mode() });
    }

    // A new route from here to the same end, planned over the next frames; the current one stays until it is ready.
    void Replan(Vector3 pos, bool announce)
    {
        lastReplan = Time.unscaledTime;
        if (goalPark != null) routeEnd = ParkCenter(goalPark);
        var keep = phase;
        StartPlan(pos, routeEnd, (r, why) =>
        {
            lastReplanFailed = r == null;
            if (r == null || phase == Phase.Off || phase == Phase.Lift) return;
            route = r;
            progress = 0;
            line.Show(route);
            if (announce) Rerouted();
            else announced.Clear();
        });
    }

    // Nothing the car would hit on the straight line to p: a box the car's size, facing along the way (a thinner
    // sphere let it try from under a deck's edge and get stuck on it).
    bool ClearLine(Vector3 from, Vector3 p)
    {
        if (carMask == 0) carMask = CooSpace.CarMask();
        var d = p - from;
        float len = d.magnitude;
        if (len < 2f) return true;
        var flat = Flat(d);
        var facing = flat.sqrMagnitude > 0.01f ? Quaternion.LookRotation(flat) : Quaternion.identity;
        var half = new Vector3(2.1f, 0.9f, 2.1f);
        // a cast does not see what the box already touches at its start (the car pressed under a deck)
        if (Physics.CheckBox(from, half, facing, carMask, QueryTriggerInteraction.Ignore)) return false;
        return !Physics.BoxCast(from, half, d / len, facing, len - 1f, carMask, QueryTriggerInteraction.Ignore);
    }

    bool PlayerIsSteering()
    {
        var L = ControlLayout.Instance;
        if (L == null) return false;
        if (Global.IsInVehicle)
            return L.Axis("car.gas") > 0.35f || L.Axis("car.brake") > 0.35f || Mathf.Abs(L.Axis("car.steer")) > 0.45f || Mathf.Abs(L.Axis("car.climb")) > 0.5f;
        return Mathf.Abs(L.Axis("foot.move_x")) > 0.4f || Mathf.Abs(L.Axis("foot.move_y")) > 0.4f;
    }

    // After the player's own inputs are on the pad: the autopilot's replace them while it drives.
    public void ApplyInputs()
    {
        if (phase != Phase.Drive && phase != Phase.Approach && phase != Phase.Parking && phase != Phase.Walk) return;
        if (!pad.TryAttach()) return;
        if (phase == Phase.Walk)
        {
            pad.SetLeftStick(walkStick.x, walkStick.y);
            return;
        }
        pad.Set(InputControlType.RightTrigger, gas);
        pad.Set(InputControlType.LeftTrigger, brake);
        pad.SetLeftStick(steer, 0);
        pad.SetRightStick(0, climb);
        VRAltitude.LastClimb = climb;
        pad.Set(InputControlType.LeftBumper, Mathf.Max(0, -strafe));
        pad.Set(InputControlType.RightBumper, Mathf.Max(0, strafe));
    }

    public void BeforeRender() => line.Update(route, progress);

    public string RouteInfo() => $"phase {phase} progress {progress}/{route?.Count ?? 0}; " + line.Describe();
}
