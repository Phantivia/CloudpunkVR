using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CloudpunkVR;

// SteamVR Input (IVRInput_010) read through the openvr_api.dll that Unity's built-in OpenVR support already loaded
// and initialised. Only the table entries listed in Slot are called; their order follows openvr.h of IVRInput_010.
internal sealed class SteamVRInput
{
    enum Slot { SetActionManifestPath = 0, GetActionSetHandle = 1, GetActionHandle = 2, UpdateActionState = 4,
                GetDigitalActionData = 5, GetAnalogActionData = 6, TriggerHapticVibrationAction = 21 }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetGenericInterfaceFn([MarshalAs(UnmanagedType.LPStr)] string version, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StringFn([MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int HandleFn([MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref ulong handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int UpdateFn([In, Out] ActiveActionSet[] sets, uint sizeOfSet, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DigitalFn(ulong action, ref DigitalData data, uint size, ulong restrict);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AnalogFn(ulong action, ref AnalogData data, uint size, ulong restrict);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int HapticFn(ulong action, float start, float duration, float frequency, float amplitude, ulong restrict);

    [StructLayout(LayoutKind.Sequential)]
    struct ActiveActionSet { public ulong ActionSet, RestrictedToDevice, SecondaryActionSet; public uint Padding; public int Priority; }

    [StructLayout(LayoutKind.Sequential)]
    struct DigitalData { public byte Active; public ulong ActiveOrigin; public byte State, Changed; public float UpdateTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct AnalogData { public byte Active; public ulong ActiveOrigin; public float X, Y, Z, DeltaX, DeltaY, DeltaZ, UpdateTime; }

    // Raw controls: the action manifest has one action per physical control, bound 1:1 in the default bindings;
    // ControlMap decides what each one does.
    static readonly string[] Sticks = { "left_stick", "right_stick" };
    static readonly string[] Pulls = { "left_trigger", "right_trigger", "left_grip", "right_grip" };
    static readonly string[] Buttons = { "a", "b", "x", "y", "left_stick_click", "right_stick_click", "left_menu" };

    UpdateFn update;
    DigitalFn digital;
    AnalogFn analog;
    HapticFn haptic;
    readonly ActiveActionSet[] sets = new ActiveActionSet[1];
    readonly ulong[] stickActions = new ulong[2], pullActions = new ulong[4], buttonActions = new ulong[7];
    ulong rumble;
    readonly System.Collections.Generic.Dictionary<string, float> values = new();

    public bool Ready { get; private set; }

    // Current value of a raw control by ControlMap input id: sticks -1..1 per axis, triggers and grips 0..1,
    // buttons 0 or 1.
    public float Value(string input) =>
        Fake.TryGetValue(input, out var f) && UnityEngine.Time.unscaledTime < f.until ? f.value : values.TryGetValue(input, out var v) ? v : 0;

    // Dev: pretend a control is held (vrinput command), as if it came from the headset's controllers.
    public static readonly System.Collections.Generic.Dictionary<string, (float value, float until)> Fake = new();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RecommendedSizeFn(ref uint width, ref uint height);

    // IVRSystem's first entry is GetRecommendedRenderTargetSize: the per-eye size SteamVR wants, including the
    // user's SteamVR / streaming-app resolution setting.
    public static bool TryRecommendedSize(out uint width, out uint height)
    {
        width = height = 0;
        var module = GetModuleHandleW("openvr_api.dll");
        if (module == IntPtr.Zero) return false;
        var getInterface = Marshal.GetDelegateForFunctionPointer<GetGenericInterfaceFn>(GetProcAddress(module, "VR_GetGenericInterface"));
        int err = 0;
        var table = getInterface("FnTable:IVRSystem_019", ref err);
        if (table == IntPtr.Zero || err != 0) return false;
        Marshal.GetDelegateForFunctionPointer<RecommendedSizeFn>(Marshal.ReadIntPtr(table, 0))(ref width, ref height);
        return width > 0 && height > 0;
    }

    public bool Init(string manifestPath)
    {
        var module = GetModuleHandleW("openvr_api.dll");
        if (module == IntPtr.Zero) { Plugin.L.LogError("openvr_api.dll is not loaded"); return false; }
        var getInterface = Marshal.GetDelegateForFunctionPointer<GetGenericInterfaceFn>(GetProcAddress(module, "VR_GetGenericInterface"));
        int err = 0;
        var table = getInterface("FnTable:IVRInput_010", ref err);
        if (table == IntPtr.Zero || err != 0) { Plugin.L.LogError($"IVRInput_010 unavailable (error {err})"); return false; }

        T Fn<T>(Slot s) => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, (int)s * IntPtr.Size));
        update = Fn<UpdateFn>(Slot.UpdateActionState);
        digital = Fn<DigitalFn>(Slot.GetDigitalActionData);
        analog = Fn<AnalogFn>(Slot.GetAnalogActionData);
        haptic = Fn<HapticFn>(Slot.TriggerHapticVibrationAction);
        var getSet = Fn<HandleFn>(Slot.GetActionSetHandle);
        var getAction = Fn<HandleFn>(Slot.GetActionHandle);

        int e = Fn<StringFn>(Slot.SetActionManifestPath)(Path.GetFullPath(manifestPath));
        if (e != 0) { Plugin.L.LogError($"SetActionManifestPath({manifestPath}) failed: {e}"); return false; }

        ulong Action(string name)
        {
            ulong h = 0;
            int r = getAction("/actions/cloudpunk/" + name, ref h);
            if (r != 0) Plugin.L.LogWarning($"action {name}: error {r}");
            return h;
        }
        ulong set = 0;
        getSet("/actions/cloudpunk", ref set);
        sets[0].ActionSet = set;
        for (int i = 0; i < Sticks.Length; i++) stickActions[i] = Action("in/" + Sticks[i]);
        for (int i = 0; i < Pulls.Length; i++) pullActions[i] = Action("in/" + Pulls[i]);
        for (int i = 0; i < Buttons.Length; i++) buttonActions[i] = Action("in/" + Buttons[i]);
        rumble = Action("out/haptic");
        Ready = set != 0;
        Plugin.L.LogInfo($"SteamVR input ready={Ready} manifest={manifestPath}");
        return Ready;
    }

    public void Poll()
    {
        if (!Ready) return;
        if (update(sets, (uint)Marshal.SizeOf<ActiveActionSet>(), 1) != 0) return;
        uint asz = (uint)Marshal.SizeOf<AnalogData>();
        for (int i = 0; i < Sticks.Length; i++)
        {
            var a = new AnalogData();
            analog(stickActions[i], ref a, asz, 0);
            values[Sticks[i] + "_x"] = a.Active != 0 ? a.X : 0;
            values[Sticks[i] + "_y"] = a.Active != 0 ? a.Y : 0;
        }
        for (int i = 0; i < Pulls.Length; i++)
        {
            var a = new AnalogData();
            analog(pullActions[i], ref a, asz, 0);
            values[Pulls[i]] = a.Active != 0 ? a.X : 0;
        }
        uint dsz = (uint)Marshal.SizeOf<DigitalData>();
        for (int i = 0; i < Buttons.Length; i++)
        {
            var d = new DigitalData();
            digital(buttonActions[i], ref d, dsz, 0);
            values[Buttons[i]] = d.Active != 0 && d.State != 0 ? 1 : 0;
        }
    }

    public void Rumble(float seconds, float amplitude)
    {
        if (Ready && rumble != 0) haptic(rumble, 0, seconds, 80, Math.Clamp(amplitude, 0, 1), 0);
    }
}
