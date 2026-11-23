using System;
using System.Text.Json.Nodes;
using UnityEngine;

namespace CloudpunkVR.Coo;

// What the stream sees.
//   spectator (VR): the desktop window shows a steadied single view of the player's head instead of the raw eye mirror
//     (no roll, smoothed turns, 16:9, a calmer field of view), with the game's HUD on top, for OBS to capture.
//   off: the eye mirror.
internal sealed class CooView
{
    public string Mode { get; private set; } = "off";

    public CooView()
    {
        SetMode(Plugin.Cfg.CooView.Value);
    }

    public void SetMode(string m)
    {
        m = m == "spectator" ? m : "off";
        if (m == Mode) return;
        Mode = m;
        Plugin.L.LogInfo("coo: view " + m);
    }

    // ---- VR spectator: a steadied mono render of the player's view into the desktop window

    static RenderTexture spectatorRT;
    Quaternion specRot = Quaternion.identity;   // tracking space (rides with the car)
    Vector3 specPos;                            // tracking space
    bool specValid;
    int specFrame;
    public static int SpectatorEvery = 2;      // render the spectator view every N headset frames (45 fps at 90 Hz)
    public static float SpectatorFov = 78f;

    public void EndOfFrame(VRCameras cams)
    {
        if (Mode != "spectator" || !UnityEngine.XR.XRSettings.enabled || cams == null || !cams.MainPoseValid) return;
        var g = Global.Instance;
        var cam = g != null && g.mainCamera != null ? g.mainCamera.GetComponent<Camera>() : null;
        if (cam == null) return;
        float dt = Time.unscaledDeltaTime;
        // steady the head: no roll, follow head turns with a short lag, keep position almost exact. Smoothed in the
        // tracking space, which rides with the car: smoothing the world position made the view trail the moving car
        // by speed / 25 (0.7 m at 60 km/h), behind the seat back in the cockpit.
        if (!specValid) { specRot = cams.HeadRot; specPos = cams.HeadPos; specValid = true; }
        specRot = Quaternion.Slerp(specRot, cams.HeadRot, 1 - Mathf.Exp(-dt * 6f));
        specPos = Vector3.Lerp(specPos, cams.HeadPos, 1 - Mathf.Exp(-dt * 25f));
        var fwd = cams.MainBasis * specRot * Vector3.forward;
        var level = Quaternion.LookRotation(fwd, Vector3.up);
        var pos = cams.MainGamePos + cams.MainBasis * specPos;
        if (++specFrame % Math.Max(1, SpectatorEvery) == 0)
        {
            int w = Math.Max(640, Screen.width), h = Math.Max(360, Screen.height);
            if (spectatorRT == null || spectatorRT.width != w || spectatorRT.height != h)
            {
                if (spectatorRT != null) spectatorRT.Release();
                spectatorRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "CooSpectator" };
                spectatorRT.Create();
            }
            var t = cam.transform;
            var savedPos = t.position;
            var savedRot = t.rotation;
            var savedEye = cam.stereoTargetEye;
            var savedTarget = cam.targetTexture;
            try
            {
                Rendering = true;
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.targetTexture = spectatorRT;
                cam.fieldOfView = SpectatorFov;
                t.SetPositionAndRotation(pos, level);
                cam.Render();
            }
            finally
            {
                Rendering = false;
                cam.targetTexture = savedTarget;
                cam.stereoTargetEye = savedEye;
                t.SetPositionAndRotation(savedPos, savedRot);
            }
            // the game HUD goes on once per rendered picture (blending it again on the frames in between would thicken it);
            // the stream overlay mirrored into the headset is not drawn here: OBS lays the same pages over this picture
            CompositeHud(spectatorRT);
        }
        if (spectatorRT == null) return;
        // Overwrite the desktop window after the headset mirror has gone there (the mirror stays on: with it off this
        // build presents nothing to the window, and a camera targeting no eye stays black).
        Graphics.Blit(spectatorRT, (RenderTexture)null);
    }

    Material hudMat;

    // The game's HUD (minimap, quest list, prompts, dialogue) is drawn into its own texture for the VR panel;
    // lay it over the stream picture so viewers see what the player sees.
    void CompositeHud(RenderTexture target)
    {
        var ui = VRHost.UITexture;
        if (ui == null || VRHost.UI == null || !VRHost.UI.PanelVisible) return;
        if (hudMat == null)
        {
            var sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply") ?? Shader.Find("UI/Default");
            if (sh == null) return;
            hudMat = new Material(sh) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        }
        // a blend material over what is already there (premultiplied alpha, like the VR panel)
        Graphics.Blit(ui, target, hudMat);
    }

    // Dev: the last spectator picture as a PNG.
    public static void SaveSpectator(string path)
    {
        if (spectatorRT == null) { Plugin.L.LogWarning("specshot: no spectator picture yet"); return; }
        var prev = RenderTexture.active;
        RenderTexture.active = spectatorRT;
        var t = new Texture2D(spectatorRT.width, spectatorRT.height, TextureFormat.RGB24, false);
        t.ReadPixels(new Rect(0, 0, spectatorRT.width, spectatorRT.height), 0, 0);
        t.Apply();
        RenderTexture.active = prev;
        System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(t));
        UnityEngine.Object.Destroy(t);
    }

    // True while the spectator render runs (the cockpit interior then uses the camera's own mono matrices).
    public static bool Rendering;

    // ---- snapshots for the agent

    public JsonNode Snapshot(int width)
    {
        var shot = ScreenCapture.CaptureScreenshotAsTexture();
        try
        {
            width = Mathf.Clamp(width, 160, 1280);
            int height = Mathf.RoundToInt(width * (float)shot.height / Math.Max(1, shot.width));
            var rt = RenderTexture.GetTemporary(width, height, 0);
            Graphics.Blit(shot, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var small = new Texture2D(width, height, TextureFormat.RGB24, false);
            small.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            small.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            byte[] jpg = ImageConversion.EncodeToJPG(small, 72);
            UnityEngine.Object.Destroy(small);
            return new JsonObject { ["mime"] = "image/jpeg", ["width"] = width, ["height"] = height, ["data"] = Convert.ToBase64String(jpg) };
        }
        finally { UnityEngine.Object.Destroy(shot); }
    }
}
