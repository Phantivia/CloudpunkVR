using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using InControl;
using UnityEngine;

namespace CloudpunkVR.Coo;

// The live-stream companion: the bridge to the Cortico World (cortico-world-cloudpunk), the game state it reports,
// 可缇 on the call channel (picture over Spout, her lines from the vtuber World), the stream's overlay mirrored into the
// headset (the OBS browser sources, rendered by the World), the route planner and autopilot, and the stream view. Lives as long as the plugin;
// driven from VRHost's frame hooks in flat and VR mode.
internal sealed class CooHost
{
    public static CooHost Instance { get; private set; }

    readonly VirtualPad pad;
    readonly CooBridge bridge;
    readonly CooGame game;
    public readonly CooSpout Spout = new();
    public readonly CooChannel Channel;
    public readonly CooOverlay Overlay = new();
    public CooPilot Pilot { get; }
    public CooView View { get; }
    float nextPoll;
    bool onAir;
    readonly List<(InputControlType control, float until)> held = new();
    readonly List<(long id, Func<JsonNode> producer)> deferred = new();

    public bool Connected => bridge.Connected;

    public CooHost(VirtualPad pad)
    {
        Instance = this;
        this.pad = pad;
        bridge = new CooBridge(Plugin.Cfg.CooPort.Value);
        game = new CooGame(bridge);
        Pilot = new CooPilot(pad, bridge);
        View = new CooView();
        Channel = new CooChannel(Spout, bridge);
        bridge.Request = OnRequest;
        bridge.Message = OnMessage;
        bridge.OnConnected += () =>
        {
            game.Reset();
            bridge.Send(new JsonObject
            {
                ["type"] = "hello", ["protocol"] = CooBridge.ProtocolVersion, ["game"] = "cloudpunk",
                ["vr"] = UnityEngine.XR.XRSettings.enabled, ["plugin"] = Plugin.Version,
            });
        };
        try { bridge.Start(); }
        catch (Exception e) { Plugin.L.LogError($"coo: cannot listen on port {Plugin.Cfg.CooPort.Value}: {e.Message}"); }
    }

    // Nothing of the stream shows while the World is away: a dead link leaves the game as it was.
    bool OnAir => onAir && bridge.Connected || DevCommands.DevLive;

    public void Update()
    {
        float now = Time.unscaledTime;
        bridge.Pump();
        Channel.OnAir = Overlay.OnAir = OnAir;
        for (int i = held.Count - 1; i >= 0; i--)
            if (now >= held[i].until) { pad.SetOverride(held[i].control, 0); held.RemoveAt(i); }
        Pilot.Update();
        Spout.Update(OnAir || previewUntil > now);
        Channel.Update();
        if (now >= nextPoll && bridge.Connected)
        {
            nextPoll = now + 0.25f;
            game.Poll(now);
        }
    }

    // After the VR controls (or nothing, in flat mode) have written the pad: the autopilot's inputs win.
    public void AfterControls() => Pilot.ApplyInputs();

    public readonly CooFufu Fufu = new();

    public void BeforeRender(VRCameras cams)
    {
        Channel.BeforeRender(cams);
        Fufu.Update(Time.unscaledDeltaTime);
        // dev (cockpitlook): turn the flat-mode cockpit camera to look around the cabin
        if (DevCommands.CockpitLook != Vector2.zero && !UnityEngine.XR.XRSettings.enabled && Global.Instance?.vehicleInterior?.cameraCockpit != null)
        {
            var ct = Global.Instance.vehicleInterior.cameraCockpit.transform;
            var up = Global.Instance.vehicleInterior.interiorLocal.up;
            var fwd = Vector3.ProjectOnPlane(Global.Instance.vehicleInterior.interiorLocal.forward, up).normalized;
            ct.rotation = Quaternion.AngleAxis(DevCommands.CockpitLook.x, up) * Quaternion.AngleAxis(DevCommands.CockpitLook.y, Vector3.Cross(up, fwd)) * Quaternion.LookRotation(fwd, up);
        }
        Overlay.BeforeRender(cams);
        Pilot.BeforeRender();
    }

    // End of frame (rendering done): deferred replies such as snapshots.
    public void EndOfFrame()
    {
        if (deferred.Count == 0) return;
        foreach (var (id, producer) in deferred)
        {
            var reply = new JsonObject { ["type"] = "reply", ["id"] = id };
            try { reply["result"] = producer(); reply["ok"] = true; }
            catch (Exception e) { reply["ok"] = false; reply["error"] = e.Message; }
            bridge.Send(reply);
        }
        deferred.Clear();
    }

    // A reply sent when the work is done (route planning runs over several frames).
    void Reply(long id, JsonNode result, string error)
    {
        var reply = new JsonObject { ["type"] = "reply", ["id"] = id, ["ok"] = error == null };
        if (error == null) reply["result"] = result;
        else reply["error"] = error;
        bridge.Send(reply);
    }

    public void Press(InputControlType control, float seconds = 0.15f)
    {
        pad.TryAttach();
        pad.SetOverride(control, 1);
        held.Add((control, Time.unscaledTime + seconds));
    }

    float previewUntil;

    JsonNode OnRequest(string method, JsonObject args, long id)
    {
        switch (method)
        {
            case "state": return game.State();
            case "navigate": Pilot.Navigate(args, (r, err) => Reply(id, r, err)); return CooBridge.Later;
            case "walk_to": Pilot.WalkTo(args, (r, err) => Reply(id, r, err)); return CooBridge.Later;
            case "places": return CooPlaces.Search(args);
            case "stop": Pilot.Stop("asked"); return new JsonObject { ["stopped"] = true };
            case "snapshot":
            {
                int w = (int?)args["width"] ?? 640;
                deferred.Add((id, () => View.Snapshot(w)));
                return CooBridge.Later;
            }
            case "live":
            {
                var senders = new JsonArray();
                foreach (var s in CooSpout.Senders()) senders.Add(s);
                return new JsonObject { ["onAir"] = OnAir, ["channel"] = Channel.Describe(), ["overlay"] = Overlay.Describe(), ["senders"] = senders };
            }
            case "preview":
            {
                // the Spout picture for the World's setup page; keeps the receiver open for a while even off air
                previewUntil = Time.unscaledTime + 10f;
                int w = (int?)args["width"] ?? 480;
                deferred.Add((id, () =>
                {
                    if (!Spout.Live) return new JsonObject { ["live"] = false, ["problem"] = Spout.Problem };
                    var full = Spout.PreviewJpeg(w, out int pw, out int ph);
                    if (!OnAir) Spout.Render();
                    var box = Spout.BoxJpeg();
                    var crop = Spout.Crop();
                    return new JsonObject
                    {
                        ["live"] = true, ["sender"] = Spout.Sender, ["width"] = Spout.Width, ["height"] = Spout.Height,
                        ["frame"] = new JsonObject { ["mime"] = "image/jpeg", ["width"] = pw, ["height"] = ph, ["data"] = Convert.ToBase64String(full) },
                        ["box"] = box != null ? new JsonObject { ["mime"] = "image/jpeg", ["data"] = Convert.ToBase64String(box) } : null,
                        ["crop"] = new JsonObject { ["x"] = crop.x, ["y"] = crop.y, ["w"] = crop.width, ["h"] = crop.height },
                    };
                }));
                return CooBridge.Later;
            }
            default: throw new CooError("unknown method " + method);
        }
    }

    void OnMessage(string type, JsonObject msg)
    {
        switch (type)
        {
            case "config": Configure(msg); break;
            case "corti_line": Channel.Say((string)msg["text"], (float?)msg["seconds"] ?? 3f); break;
            case "corti_cut": Channel.Cut(); break;
            case "overlay_frame": Overlay.Frame((int?)msg["layer"] ?? 0, (string)msg["png"]); break;
        }
    }

    void Configure(JsonObject msg)
    {
        if (msg["onAir"] is JsonNode air)
        {
            bool was = onAir;
            onAir = (bool)air;
            if (!onAir) Overlay.Clear();
            if (was != onAir) Plugin.L.LogInfo("coo: " + (onAir ? "on air" : "off air"));
        }
        if (msg["view"] is JsonNode view) View.SetMode((string)view);
        if (msg["autopilotSpeed"] is JsonNode sp) Pilot.SpeedFactor = Mathf.Clamp((float)sp, 0.3f, 1f);
        if (msg["corti"] is JsonObject c)
        {
            Spout.Wanted = (string)c["sender"] ?? "";
            Spout.Zoom = Mathf.Clamp((float?)c["zoom"] ?? Spout.Zoom, 0.5f, 6f);
            Spout.CenterX = Mathf.Clamp((float?)c["x"] ?? Spout.CenterX, -0.5f, 1.5f);
            Spout.CenterY = Mathf.Clamp((float?)c["y"] ?? Spout.CenterY, -0.5f, 1.5f);
            Spout.FlipY = (bool?)c["flip"] ?? Spout.FlipY;
            Channel.Holo = (bool?)c["holo"] ?? Channel.Holo;
            Channel.PopOut = (bool?)c["popOut"] ?? Channel.PopOut;
            Fufu.On = (bool?)c["fufu"] ?? Fufu.On;
            Channel.Figure.Zoom = Mathf.Clamp((float?)c["popZoom"] ?? Channel.Figure.Zoom, 0.3f, 4f);
            if (c["popSize"] is JsonNode ps) CooHoloFigure.Height = Mathf.Clamp((float)ps, 0.1f, 0.8f);
            Channel.InCar = (bool?)c["car"] ?? Channel.InCar;
            Channel.OnFoot = (bool?)c["foot"] ?? Channel.OnFoot;
            Channel.OnWrist = (bool?)c["wrist"] ?? Channel.OnWrist;
            Channel.DisplayName = (string)c["name"] ?? Channel.DisplayName;
        }
        if (msg["overlay"] is JsonObject o) Overlay.Configure(o);
        Plugin.L.LogInfo($"coo: config onAir={onAir} view={View.Mode} sender='{Spout.Wanted}' zoom={Spout.Zoom:F2} centre=({Spout.CenterX:F2},{Spout.CenterY:F2}) " +
                         $"holo={Channel.Holo} car/foot/wrist={Channel.InCar}/{Channel.OnFoot}/{Channel.OnWrist} overlay={Overlay.On} {Overlay.Width:F2} m at {Overlay.Distance:F2} m");
    }
}
