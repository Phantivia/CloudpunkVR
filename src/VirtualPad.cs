using System;
using InControl;
using Il2CppInterop.Runtime;

namespace CloudpunkVR;

// An InControl device the game treats like an Xbox pad. VR controllers (and dev commands) write into it;
// values are pushed inside InputManager.OnUpdateDevices so they are committed on the same InControl tick.
internal sealed class VirtualPad
{
    static readonly InputControlType[] Controls =
    {
        InputControlType.LeftStickUp, InputControlType.LeftStickDown, InputControlType.LeftStickLeft, InputControlType.LeftStickRight,
        InputControlType.RightStickUp, InputControlType.RightStickDown, InputControlType.RightStickLeft, InputControlType.RightStickRight,
        InputControlType.LeftStickButton, InputControlType.RightStickButton,
        InputControlType.DPadUp, InputControlType.DPadDown, InputControlType.DPadLeft, InputControlType.DPadRight,
        InputControlType.LeftTrigger, InputControlType.RightTrigger, InputControlType.LeftBumper, InputControlType.RightBumper,
        InputControlType.Action1, InputControlType.Action2, InputControlType.Action3, InputControlType.Action4,
        InputControlType.Back, InputControlType.Start, InputControlType.View, InputControlType.Menu,
    };

    public InputDevice Device { get; private set; }
    readonly float[] values = new float[(int)InputControlType.Count];
    // Dev commands hold buttons here; VR controls rewrite `values` every frame and must not cancel them.
    readonly float[] overrides = new float[(int)InputControlType.Count];
    Il2CppSystem.Action<ulong, float> onUpdate;

    public bool Attached => Device != null;

    public void Set(InputControlType control, float value) => values[(int)control] = value;
    public void SetOverride(InputControlType control, float value) => overrides[(int)control] = value;
    public float Get(InputControlType control) => values[(int)control];
    public void Clear() => Array.Clear(values, 0, values.Length);

    public void SetLeftStick(float x, float y)
    {
        Set(InputControlType.LeftStickLeft, Math.Max(0, -x));
        Set(InputControlType.LeftStickRight, Math.Max(0, x));
        Set(InputControlType.LeftStickUp, Math.Max(0, y));
        Set(InputControlType.LeftStickDown, Math.Max(0, -y));
    }

    public void SetRightStick(float x, float y)
    {
        Set(InputControlType.RightStickLeft, Math.Max(0, -x));
        Set(InputControlType.RightStickRight, Math.Max(0, x));
        Set(InputControlType.RightStickUp, Math.Max(0, y));
        Set(InputControlType.RightStickDown, Math.Max(0, -y));
    }

    public bool TryAttach()
    {
        if (Device != null) return true;
        if (!InputManager.IsSetup) return false;
        Device = new InputDevice("Cloudpunk VR Controller");
        Device.DeviceClass = InputDeviceClass.Controller;
        Device.DeviceStyle = InputDeviceStyle.XboxOne;
        foreach (var c in Controls) Device.AddControl(c, c.ToString());
        InputManager.AttachDevice(Device);
        onUpdate = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ulong, float>>(new Action<ulong, float>(Push));
        InputManager.add_OnUpdateDevices(onUpdate);
        Plugin.L.LogInfo("virtual pad attached");
        return true;
    }

    void Push(ulong tick, float dt)
    {
        try
        {
            foreach (var c in Controls)
            {
                float o = overrides[(int)c];
                Device.UpdateWithValue(c, o != 0 ? o : values[(int)c], tick, dt);
            }
        }
        catch (Exception e)
        {
            Plugin.L.LogError("virtual pad update failed: " + e);
            InputManager.remove_OnUpdateDevices(onUpdate);
        }
    }
}
