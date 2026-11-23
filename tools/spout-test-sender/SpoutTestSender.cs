// A minimal Spout2 sender for testing the plugin's receiver without VTube Studio: a D3D11 texture with the legacy
// shared flag, registered the way Spout does it (the "SpoutSenderNames" list, the sender's SharedTextureInfo section,
// "ActiveSenderName"). The picture: transparent background, a green band along the top, a blue band along the bottom,
// a red square in the top-left corner, and a white disc circling the middle (so orientation, alpha and motion show).
// The texture is made on the adapter with the most video memory (on a hybrid laptop the discrete GPU the game runs on:
// shared textures do not cross adapters).
// usage: SpoutTestSender [seconds=120] [name=CloudpunkVR Test] [width=1280] [height=720]
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class SpoutTestSender
{
    [DllImport("d3d11")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels, uint sdk, out IntPtr device, out int level, out IntPtr context);
    [DllImport("dxgi")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc1Fn(IntPtr self, IntPtr desc);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr CreateFileMappingA(IntPtr file, IntPtr attrs, uint protect, uint sizeHigh, uint sizeLow, string name);
    [DllImport("kernel32")] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);

    [StructLayout(LayoutKind.Sequential)] struct Desc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture2DFn(IntPtr self, ref Desc desc, IntPtr init, out IntPtr tex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr obj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSharedHandleFn(IntPtr self, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UpdateSubresourceFn(IntPtr self, IntPtr dst, uint sub, IntPtr box, IntPtr src, uint rowPitch, uint depthPitch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void FlushFn(IntPtr self);

    static T Slot<T>(IntPtr com, int i) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), i * IntPtr.Size));

    static IntPtr Section(string name, int size)
    {
        var map = CreateFileMappingA(new IntPtr(-1), IntPtr.Zero, 4, 0, (uint)size, name);
        if (map == IntPtr.Zero) throw new Exception("CreateFileMapping " + name);
        return MapViewOfFile(map, 0xF001F, 0, 0, (UIntPtr)size);
    }

    static void WriteName(IntPtr at, string name)
    {
        var b = new byte[256];
        Encoding.Default.GetBytes(name, 0, name.Length, b, 0);
        Marshal.Copy(b, 0, at, 256);
    }

    static int Main(string[] args)
    {
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 120;
        string name = args.Length > 1 ? args[1] : "CloudpunkVR Test";
        int w = args.Length > 2 ? int.Parse(args[2]) : 1280, h = args.Length > 3 ? int.Parse(args[3]) : 720;
        var fiid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        CreateDXGIFactory1(ref fiid, out var factory);
        IntPtr best = IntPtr.Zero;
        long bestMem = -1;
        var descBuf = Marshal.AllocHGlobal(512);
        for (uint i = 0; Slot<EnumAdapters1Fn>(factory, 12)(factory, i, out var ad) >= 0; i++)
        {
            Slot<GetDesc1Fn>(ad, 10)(ad, descBuf);
            string adName = Marshal.PtrToStringUni(descBuf);
            long mem = Marshal.ReadInt64(descBuf, 272);
            Console.WriteLine($"adapter {i}: {adName} {mem >> 20} MB");
            if (mem > bestMem) { bestMem = mem; best = ad; }
        }
        int hr = D3D11CreateDevice(best, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out var dev, out _, out var ctx);
        if (hr < 0) { Console.WriteLine($"D3D11CreateDevice 0x{hr:X8}"); return 1; }
        var desc = new Desc { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, Usage = 0, BindFlags = 0x28, MiscFlags = 0x2 };
        hr = Slot<CreateTexture2DFn>(dev, 5)(dev, ref desc, IntPtr.Zero, out var tex);
        if (hr < 0) { Console.WriteLine($"CreateTexture2D 0x{hr:X8}"); return 1; }
        var iid = new Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
        Slot<QueryInterfaceFn>(tex, 0)(tex, ref iid, out var dxgi);
        Slot<GetSharedHandleFn>(dxgi, 8)(dxgi, out var shared);
        Console.WriteLine($"shared handle 0x{shared.ToInt64():X} {w}x{h}");

        // register: the sender list (first empty slot), the sender's info, the active sender
        var names = Section("SpoutSenderNames", 64 * 256);
        int slot = 0;
        for (; slot < 64; slot++)
        {
            byte first = Marshal.ReadByte(names, slot * 256);
            if (first == 0) break;
            var b = new byte[256];
            Marshal.Copy(names + slot * 256, b, 0, 256);
            if (Encoding.Default.GetString(b, 0, Array.IndexOf(b, (byte)0)) == name) break;
        }
        WriteName(names + slot * 256, name);
        var info = Section(name, 280);
        Marshal.WriteInt32(info, 0, (int)shared.ToInt64());
        Marshal.WriteInt32(info, 4, w);
        Marshal.WriteInt32(info, 8, h);
        Marshal.WriteInt32(info, 12, 87);
        Marshal.WriteInt32(info, 16, 0);
        WriteName(Section("ActiveSenderName", 256), name);

        var pixels = new byte[w * h * 4];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        var update = Slot<UpdateSubresourceFn>(ctx, 48);
        var flush = Slot<FlushFn>(ctx, 111);
        var start = DateTime.UtcNow;
        int frames = 0;
        while ((DateTime.UtcNow - start).TotalSeconds < seconds)
        {
            double t = (DateTime.UtcNow - start).TotalSeconds;
            double cx = w / 2 + Math.Cos(t * 1.5) * h * 0.25, cy = h / 2 + Math.Sin(t * 1.5) * h * 0.25, r = h * 0.12;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    byte bl = 0, g = 0, rd = 0, a = 0;
                    if (y < h / 10) { g = 220; a = 255; }                       // top band green
                    else if (y >= h - h / 10) { bl = 230; a = 255; }            // bottom band blue
                    if (x < h / 5 && y < h / 5) { rd = 230; g = 0; bl = 0; a = 255; } // red square top-left
                    double dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy < r * r) { rd = g = bl = 255; a = 255; }
                    pixels[i] = bl; pixels[i + 1] = g; pixels[i + 2] = rd; pixels[i + 3] = a;
                }
            update(ctx, tex, 0, IntPtr.Zero, pin.AddrOfPinnedObject(), (uint)(w * 4), 0);
            flush(ctx);
            frames++;
            Thread.Sleep(30);
        }
        WriteName(names + slot * 256, "");
        Console.WriteLine($"sent {frames} frames");
        return 0;
    }
}
