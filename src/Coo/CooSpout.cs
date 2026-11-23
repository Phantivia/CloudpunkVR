using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace CloudpunkVR.Coo;

// A Spout2 receiver (VTube Studio's Spout output). Spout senders publish a D3D11 texture created with the legacy shared
// flag: the shared-memory section "SpoutSenderNames" lists the senders (256-byte ANSI names, back to back, until an
// empty one), and a section named after each sender holds its SharedTextureInfo (the 32-bit share handle, width,
// height, DXGI format, usage, description, partner id). The share handle is opened on Unity's own D3D11 device, wrapped
// in a shader resource view and handed to Unity as an external texture. Every frame the part of it shown in the box is
// drawn into a small render texture of our own, over a dark background (VTube Studio sends a transparent background),
// so the game samples a steady copy, never the sender's texture while it is being written.
internal sealed class CooSpout
{
    const uint FILE_MAP_READ = 4;
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)] static extern IntPtr OpenFileMappingA(uint access, bool inherit, string name);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);
    [DllImport("kernel32")] static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32")] static extern UIntPtr VirtualQuery(IntPtr address, out MemoryInfo info, UIntPtr length);

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryInfo { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public UIntPtr RegionSize; public uint State, Protect, Type; }

    [StructLayout(LayoutKind.Sequential)]
    struct Texture2DDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDeviceFn(IntPtr self, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenSharedResourceFn(IntPtr self, IntPtr handle, ref Guid iid, out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateSrvFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDescFn(IntPtr self, out Texture2DDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);

    static Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    static T Slot<T>(IntPtr com, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), index * IntPtr.Size));

    static void Release(ref IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        Slot<ReleaseFn>(com, 2)(com);
        com = IntPtr.Zero;
    }

    // ---- senders

    public static List<string> Senders()
    {
        var list = new List<string>();
        var map = OpenFileMappingA(FILE_MAP_READ, false, "SpoutSenderNames");
        if (map == IntPtr.Zero) return list;
        var view = MapViewOfFile(map, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
        try
        {
            if (view == IntPtr.Zero) return list;
            VirtualQuery(view, out var info, (UIntPtr)Marshal.SizeOf<MemoryInfo>());
            long size = (long)info.RegionSize.ToUInt64();
            var buf = new byte[256];
            for (long off = 0; off + 256 <= size; off += 256)
            {
                Marshal.Copy(view + (int)off, buf, 0, 256);
                int len = Array.IndexOf(buf, (byte)0);
                if (len <= 0) break;
                list.Add(Encoding.Default.GetString(buf, 0, len));
            }
        }
        finally
        {
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            CloseHandle(map);
        }
        return list;
    }

    public static string ActiveSender()
    {
        var map = OpenFileMappingA(FILE_MAP_READ, false, "ActiveSenderName");
        if (map == IntPtr.Zero) return null;
        var view = MapViewOfFile(map, FILE_MAP_READ, 0, 0, (UIntPtr)256);
        try
        {
            if (view == IntPtr.Zero) return null;
            var buf = new byte[256];
            Marshal.Copy(view, buf, 0, 256);
            int len = Array.IndexOf(buf, (byte)0);
            return len > 0 ? Encoding.Default.GetString(buf, 0, len) : null;
        }
        finally
        {
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            CloseHandle(map);
        }
    }

    struct SenderInfo { public uint Handle, Width, Height, Format; }

    static bool ReadSender(string name, out SenderInfo s)
    {
        s = default;
        var map = OpenFileMappingA(FILE_MAP_READ, false, name);
        if (map == IntPtr.Zero) return false;
        var view = MapViewOfFile(map, FILE_MAP_READ, 0, 0, (UIntPtr)20);
        try
        {
            if (view == IntPtr.Zero) return false;
            s.Handle = (uint)Marshal.ReadInt32(view, 0);
            s.Width = (uint)Marshal.ReadInt32(view, 4);
            s.Height = (uint)Marshal.ReadInt32(view, 8);
            s.Format = (uint)Marshal.ReadInt32(view, 12);
            return s.Handle != 0 && s.Width > 0 && s.Height > 0;
        }
        finally
        {
            if (view != IntPtr.Zero) UnmapViewOfFile(view);
            CloseHandle(map);
        }
    }

    // ---- receiving

    public string Wanted = "";              // sender name; empty = VTube Studio's, else the active or first one
    public string Sender { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint Format { get; private set; }
    public string Problem { get; private set; } = "还没开始接收";
    public Texture2D Source { get; private set; }
    public bool Live => Source != null;

    // The part of the frame shown in the square box: zoom 1 = the full frame height, centre in frame UV (0..1, y up).
    public float Zoom = 1.6f, CenterX = 0.5f, CenterY = 0.62f;
    public bool FlipY = true;
    public Color Background = new(0.05f, 0.09f, 0.12f, 1f);

    IntPtr device, texture, view;
    uint handle;
    float nextCheck;
    RenderTexture box;
    Material blend;

    public RenderTexture Box => box;

    IntPtr Device()
    {
        if (device != IntPtr.Zero) return device;
        // Unity's D3D11 device, from any texture it made (GetNativeTexturePtr is the ID3D11Texture2D)
        var probe = new Texture2D(4, 4, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        probe.Apply();
        var native = probe.GetNativeTexturePtr();
        if (native == IntPtr.Zero) throw new CooError("拿不到游戏的 D3D11 设备");
        Slot<GetDeviceFn>(native, 3)(native, out device);
        UnityEngine.Object.Destroy(probe);
        Plugin.L.LogInfo($"spout: D3D11 device {device.ToInt64():X}, color space {QualitySettings.activeColorSpace}, renderer {SystemInfo.graphicsDeviceType}");
        return device;
    }

    // Once a second: the sender list, and whether our sender changed its texture (VTube Studio makes a new one when
    // the output size changes).
    public void Update(bool wanted)
    {
        if (!wanted)
        {
            if (Live) Close("没在用");
            return;
        }
        float now = Time.unscaledTime;
        if (now < nextCheck) return;
        nextCheck = now + 1f;
        try
        {
            string name = Pick();
            if (name == null)
            {
                if (Live) Close("发送端没了");
                Problem = string.IsNullOrEmpty(Wanted) ? "没有 Spout 发送端(VTube Studio 里打开 Spout2 输出)" : $"找不到 Spout 发送端「{Wanted}」";
                return;
            }
            if (!ReadSender(name, out var info)) { if (Live) Close("读不到发送端"); Problem = $"读不到发送端「{name}」"; return; }
            if (Live && name == Sender && info.Handle == handle && info.Width == Width && info.Height == Height) return;
            Open(name, info);
        }
        catch (Exception e)
        {
            Close(e.Message);
            Plugin.L.LogWarning("spout: " + e.Message);
        }
    }

    string Pick()
    {
        var senders = Senders();
        if (senders.Count == 0) return null;
        if (!string.IsNullOrEmpty(Wanted)) return senders.Contains(Wanted) ? Wanted : null;
        foreach (var s in senders) if (s.IndexOf("VTube", StringComparison.OrdinalIgnoreCase) >= 0) return s;
        var active = ActiveSender();
        return active != null && senders.Contains(active) ? active : senders[0];
    }

    void Open(string name, SenderInfo info)
    {
        Close(null);
        var dev = Device();
        var iid = IID_ID3D11Texture2D;
        int hr = Slot<OpenSharedResourceFn>(dev, 28)(dev, (IntPtr)(long)info.Handle, ref iid, out texture);
        if (hr < 0 || texture == IntPtr.Zero)
            throw new CooError(hr == unchecked((int)0x80070057)
                ? $"打不开 Spout 发送端「{name}」的共享贴图：发送端多半和游戏不在同一块显卡上(游戏在 {SystemInfo.graphicsDeviceName})。Windows 设置 → 系统 → 屏幕 → 显示卡，把 VTube Studio 设成高性能后重开它"
                : $"打不开 Spout 发送端「{name}」的共享贴图(0x{hr:X8})");
        Slot<GetDescFn>(texture, 10)(texture, out var desc);
        hr = Slot<CreateSrvFn>(dev, 7)(dev, texture, IntPtr.Zero, out view);
        if (hr < 0 || view == IntPtr.Zero) { Release(ref texture); throw new CooError($"Spout 贴图建不了着色器视图(格式 {desc.Format},0x{hr:X8})"); }
        var fmt = desc.Format == 28 || desc.Format == 29 ? TextureFormat.RGBA32 : TextureFormat.BGRA32;
        Source = Texture2D.CreateExternalTexture((int)desc.Width, (int)desc.Height, fmt, false, false, view);
        Source.wrapMode = TextureWrapMode.Clamp;
        Source.filterMode = FilterMode.Bilinear;
        Source.hideFlags = HideFlags.DontUnloadUnusedAsset;
        handle = info.Handle;
        Sender = name;
        Width = (int)desc.Width;
        Height = (int)desc.Height;
        Format = desc.Format;
        Problem = null;
        Plugin.L.LogInfo($"spout: receiving '{name}' {Width}x{Height} DXGI format {desc.Format} bind 0x{desc.BindFlags:X} misc 0x{desc.MiscFlags:X}");
    }

    public void Close(string why)
    {
        if (Source != null) { UnityEngine.Object.Destroy(Source); Source = null; }
        Release(ref view);
        Release(ref texture);
        handle = 0;
        if (why != null)
        {
            Problem = why;
            if (Sender != null) Plugin.L.LogInfo($"spout: stopped receiving '{Sender}': {why}");
            Sender = null;
        }
    }

    public Material BlendMaterial() => Blend();

    Material Blend()
    {
        if (blend != null) return blend;
        foreach (var n in new[] { "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended" })
        {
            var sh = Shader.Find(n);
            if (sh == null) continue;
            blend = new Material(sh) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            // colour = 2 × tint × texture: a tint of 0.5 passes the texture through. In linear colour space Unity
            // linearises colour properties, so the 0.5 has to be given in gamma.
            var tint = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            if (QualitySettings.activeColorSpace == ColorSpace.Linear) tint = new Color(0.7354f, 0.7354f, 0.7354f, 0.5f);
            blend.SetColor("_TintColor", tint);
            blend.mainTextureScale = Vector2.one;
            blend.mainTextureOffset = Vector2.zero;
            Plugin.L.LogInfo("spout: composite shader " + n);
            return blend;
        }
        return null;
    }

    // The box picture for this frame: the cropped square over the background. Cheap (a 256² draw).
    public void Render()
    {
        if (box == null)
        {
            box = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGB32) { name = "CloudpunkVR_Corti", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontUnloadUnusedAsset };
            box.Create();
        }
        Draw(box, Crop(), Background);
    }

    // UV rectangle of the square crop in the source frame (y up).
    public Rect Crop()
    {
        float aspect = Height > 0 ? (float)Width / Height : 16f / 9f;
        float h = 1f / Mathf.Max(0.2f, Zoom);
        float w = h / aspect;
        return new Rect(CenterX - w / 2, CenterY - h / 2, w, h);
    }

    // The background, then the frame's crop alpha-blended over it, as one quad with exact texture coordinates
    // (Graphics.Blit with a material resets the texture scale and offset). The game renders in linear colour space;
    // the sender's bytes are sRGB but its view reads them as plain values, so nothing is converted on the way in
    // (sRGB writes off): our sRGB texture ends up holding the sender's bytes, and the game decodes them when it samples
    // the box. The background is given as it should look, so it is written as is too.
    void Draw(RenderTexture target, Rect uv, Color bg)
    {
        var prev = RenderTexture.active;
        bool srgb = GL.sRGBWrite;
        try
        {
            RenderTexture.active = target;
            GL.sRGBWrite = false;
            GL.Clear(false, true, bg);
            var mat = Source != null ? Blend() : null;
            if (mat == null) return;
            mat.mainTexture = Source;
            float v0 = FlipY ? 1f - uv.y : uv.y, v1 = FlipY ? 1f - uv.yMax : uv.yMax;
            GL.PushMatrix();
            GL.LoadOrtho();
            mat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            GL.TexCoord2(uv.x, v0); GL.Vertex3(0, 0, 0);
            GL.TexCoord2(uv.x, v1); GL.Vertex3(0, 1, 0);
            GL.TexCoord2(uv.xMax, v1); GL.Vertex3(1, 1, 0);
            GL.TexCoord2(uv.xMax, v0); GL.Vertex3(1, 0, 0);
            GL.End();
            GL.PopMatrix();
        }
        finally
        {
            GL.sRGBWrite = srgb;
            RenderTexture.active = prev;
        }
    }

    // For the World's preview: the whole frame, scaled to the width, as JPEG; the crop goes along so the panel can draw it.
    public byte[] PreviewJpeg(int width, out int w, out int h)
    {
        w = Mathf.Clamp(width, 160, 960);
        h = Mathf.Max(90, Mathf.RoundToInt(w * (Height > 0 ? (float)Height / Width : 9f / 16f)));
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
        try
        {
            Draw(rt, new Rect(0, 0, 1, 1), new Color(0.12f, 0.12f, 0.14f, 1));
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(w, h, TextureFormat.RGB24, false);
            t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            var jpg = ImageConversion.EncodeToJPG(t, 80);
            UnityEngine.Object.Destroy(t);
            return jpg;
        }
        finally { RenderTexture.ReleaseTemporary(rt); }
    }

    // The box itself (what the game shows before its hologram material), for the preview.
    public byte[] BoxJpeg()
    {
        if (box == null) return null;
        var prev = RenderTexture.active;
        RenderTexture.active = box;
        var t = new Texture2D(box.width, box.height, TextureFormat.RGB24, false);
        t.ReadPixels(new Rect(0, 0, box.width, box.height), 0, 0);
        t.Apply();
        RenderTexture.active = prev;
        var jpg = ImageConversion.EncodeToJPG(t, 85);
        UnityEngine.Object.Destroy(t);
        return jpg;
    }
}
