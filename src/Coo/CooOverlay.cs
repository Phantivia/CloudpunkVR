using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR.Coo;

// The stream's overlay layer, mirrored into the headset: the World renders each OBS browser source over the game
// capture (the vtuber World's stage overlay, the bilibili live-room overlay with its board and comments, the speech
// recognition subtitles) in a headless browser and sends a PNG whenever one changes. They are laid out as OBS lays them
// out on a canvas the size of the stream, on one transparent 16:9 panel fixed to the head (set distance, width, yaw,
// pitch; it never drifts), drawn by the VR UI's panel camera after everything else. The stream picture does not get
// it: OBS already lays the same pages over the game.
//
// PNGs are decoded on a worker thread (zlib from .NET, the filters here); the main thread only uploads, at most one
// layer per frame.
internal sealed class CooOverlay
{
    public bool OnAir;
    public bool On = true;
    public float Yaw, Pitch = -3f, Distance = 1.5f, Width = 1.8f, Opacity = 1f;
    float canvasW = 1920, canvasH = 1080;

    sealed class Layer
    {
        public RawImage Image;
        public Texture2D Tex;
        public float X, Y, W, H;
        public bool On = true;
        public long Seq;
    }

    readonly List<Layer> layers = new();
    readonly ConcurrentQueue<(int layer, long seq, int w, int h, byte[] rgba)> decoded = new();
    long seq;
    GameObject root;
    RectTransform canvasRt;
    bool built;

    bool Visible => OnAir && On && UnityEngine.XR.XRSettings.enabled && VRHost.UI != null && VRHost.UI.Ready;

    void Build()
    {
        var space = VRHost.UI.Space;
        if (space == null) return;
        root = new GameObject("CloudpunkVR_StreamOverlay");
        root.transform.SetParent(space, false);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 1;
        canvasRt = root.GetComponent<RectTransform>();
        canvasRt.pivot = new Vector2(0.5f, 0.5f);
        canvasRt.sizeDelta = new Vector2(canvasW, canvasH);
        root.layer = VRHost.UI.Layer;
        root.SetActive(false);
        built = true;
        Plugin.L.LogInfo("overlay: mirror panel ready");
    }

    // `overlay` in the config message: { on, canvas: [w, h], layers: [{ x, y, w, h, on }], yaw, pitch, distance, width, opacity }
    public void Configure(JsonNode n)
    {
        if (n == null) return;
        On = (bool?)n["on"] ?? On;
        Yaw = Mathf.Clamp((float?)n["yaw"] ?? Yaw, -60, 60);
        Pitch = Mathf.Clamp((float?)n["pitch"] ?? Pitch, -45, 45);
        Distance = Mathf.Clamp((float?)n["distance"] ?? Distance, 0.5f, 5f);
        Width = Mathf.Clamp((float?)n["width"] ?? Width, 0.3f, 6f);
        Opacity = Mathf.Clamp01((float?)n["opacity"] ?? Opacity);
        if (n["canvas"] is JsonArray c && c.Count == 2) { canvasW = Mathf.Max(100, (float)c[0]); canvasH = Mathf.Max(100, (float)c[1]); }
        if (n["layers"] is JsonArray ls)
        {
            if (!built && VRHost.UI != null && VRHost.UI.Ready) Build();
            for (int i = 0; i < ls.Count; i++)
            {
                var l = Ensure(i);
                if (l == null) break;
                l.X = (float?)ls[i]["x"] ?? 0;
                l.Y = (float?)ls[i]["y"] ?? 0;
                l.W = (float?)ls[i]["w"] ?? canvasW;
                l.H = (float?)ls[i]["h"] ?? canvasH;
                l.On = (bool?)ls[i]["on"] ?? true;
            }
            for (int i = ls.Count; i < layers.Count; i++) layers[i].On = false;
            Layout();
        }
    }

    Layer Ensure(int i)
    {
        if (!built) return null;
        while (layers.Count <= i)
        {
            var go = new GameObject("Layer" + layers.Count);
            go.transform.SetParent(root.transform, false);
            go.layer = root.layer;
            var img = go.AddComponent<RawImage>();
            img.raycastTarget = false;
            img.enabled = false;
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);   // OBS positions from the top left
            rt.pivot = new Vector2(0, 1);
            layers.Add(new Layer { Image = img });
        }
        return layers[i];
    }

    void Layout()
    {
        if (!built) return;
        canvasRt.sizeDelta = new Vector2(canvasW, canvasH);
        foreach (var l in layers)
        {
            var rt = l.Image.rectTransform;
            rt.anchoredPosition = new Vector2(l.X, -l.Y);
            rt.sizeDelta = new Vector2(l.W, l.H);
        }
    }

    // A frame for one layer, as base64 PNG (from the bridge thread's message, handled on the main thread).
    public void Frame(int layer, string pngBase64)
    {
        if (string.IsNullOrEmpty(pngBase64) || layer < 0 || layer > 16) return;
        long mine = ++seq;
        Task.Run(() =>
        {
            try
            {
                var png = Convert.FromBase64String(pngBase64);
                var (w, h, rgba) = Png.Decode(png);
                decoded.Enqueue((layer, mine, w, h, rgba));
            }
            catch (Exception e) { Plugin.L.LogWarning($"overlay: layer {layer} frame unreadable: {e.Message}"); }
        });
    }

    public void Clear()
    {
        foreach (var l in layers) { if (l.Image != null) l.Image.enabled = false; }
    }

    // Before rendering: upload at most one decoded layer, place the panel in front of the eyes.
    public void BeforeRender(VRCameras cams)
    {
        if (!built)
        {
            if (!Visible) return;
            Build();
            if (!built) return;
            Layout();
        }
        while (decoded.TryDequeue(out var f))
        {
            var l = Ensure(f.layer);
            if (l == null || f.seq < l.Seq) continue;
            l.Seq = f.seq;
            if (l.Tex == null || l.Tex.width != f.w || l.Tex.height != f.h)
            {
                if (l.Tex != null) UnityEngine.Object.Destroy(l.Tex);
                l.Tex = new Texture2D(f.w, f.h, TextureFormat.RGBA32, false, false) { name = "CloudpunkVR_Overlay" + f.layer, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontUnloadUnusedAsset };
            }
            l.Tex.LoadRawTextureData(f.rgba);
            l.Tex.Apply(false);
            l.Image.texture = l.Tex;
            break;   // one upload a frame; the rest wait their turn
        }
        bool vis = Visible && cams != null;
        if (root.activeSelf != vis) root.SetActive(vis);
        if (!vis) return;
        foreach (var l in layers)
        {
            bool show = l.On && l.Tex != null;
            if (l.Image.enabled != show) l.Image.enabled = show;
            var c = l.Image.color;
            if (!Mathf.Approximately(c.a, Opacity)) l.Image.color = new Color(1, 1, 1, Opacity);
        }
        var t = root.transform;
        var aim = Quaternion.Euler(-Pitch, Yaw, 0);
        t.localPosition = cams.HeadPos + cams.HeadRot * (aim * Vector3.forward * Distance);
        t.localRotation = cams.HeadRot * aim;
        t.localScale = Vector3.one * (Width / canvasW);
    }

    public JsonObject Describe()
    {
        int live = 0;
        foreach (var l in layers) if (l.Tex != null && l.On) live++;
        return new JsonObject { ["built"] = built, ["visible"] = Visible, ["layers"] = layers.Count, ["live"] = live };
    }
}

// A minimal PNG decoder for what Chromium's screencast writes: 8-bit RGBA or RGB, not interlaced. Returns rows bottom
// first (Unity's texture order).
internal static class Png
{
    public static (int w, int h, byte[] rgba) Decode(byte[] png)
    {
        if (png.Length < 33 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G') throw new InvalidDataException("not a PNG");
        int at = 8, w = 0, h = 0, bpp = 0;
        var idat = new MemoryStream();
        while (at + 8 <= png.Length)
        {
            int len = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            int body = at + 8;
            if (type == "IHDR")
            {
                w = (png[body] << 24) | (png[body + 1] << 16) | (png[body + 2] << 8) | png[body + 3];
                h = (png[body + 4] << 24) | (png[body + 5] << 16) | (png[body + 6] << 8) | png[body + 7];
                int depth = png[body + 8], ctype = png[body + 9], interlace = png[body + 12];
                if (depth != 8 || interlace != 0 || (ctype != 6 && ctype != 2)) throw new InvalidDataException($"PNG depth {depth} type {ctype} interlace {interlace}");
                bpp = ctype == 6 ? 4 : 3;
            }
            else if (type == "IDAT") idat.Write(png, body, len);
            else if (type == "IEND") break;
            at = body + len + 4;
        }
        idat.Position = 0;
        int stride = w * bpp;
        var raw = new byte[(stride + 1) * h];
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n <= 0) break;
                read += n;
            }
        }
        var outp = new byte[w * h * 4];
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int src = y * (stride + 1);
            byte filter = raw[src];
            Buffer.BlockCopy(raw, src + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int v = cur[i];
                switch (filter)
                {
                    case 1: v += a; break;
                    case 2: v += b; break;
                    case 3: v += (a + b) >> 1; break;
                    case 4:
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                        break;
                }
                cur[i] = (byte)v;
            }
            int dst = (h - 1 - y) * w * 4;
            if (bpp == 4) Buffer.BlockCopy(cur, 0, outp, dst, stride);
            else
                for (int x = 0; x < w; x++)
                {
                    outp[dst + x * 4] = cur[x * 3];
                    outp[dst + x * 4 + 1] = cur[x * 3 + 1];
                    outp[dst + x * 4 + 2] = cur[x * 3 + 2];
                    outp[dst + x * 4 + 3] = 255;
                }
            (prev, cur) = (cur, prev);
        }
        return (w, h, outp);
    }
}
