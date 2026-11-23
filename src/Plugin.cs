using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;

namespace CloudpunkVR;

[BepInPlugin(Guid, "CloudpunkVR", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "palai.cloudpunkvr";
    public const string Version = "0.1.0";

    internal static ManualLogSource L;
    internal static Settings Cfg;
    internal static string Dir;
    internal static HarmonyLib.Harmony Harmony;

    public override void Load()
    {
        L = Log;
        Dir = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        Cfg = new Settings(Config);
        CpuTopology.Load();
        if (Cfg.FullSpeedInBackground.Value) CpuTopology.SetFullSpeed(true);
        Harmony = new HarmonyLib.Harmony(Guid);
        Patches.Apply(Harmony);
        ClassInjector.RegisterTypeInIl2Cpp<VRHost>();
        AddComponent<VRHost>();
        L.LogInfo($"CloudpunkVR {Version} loaded from {Dir}");
    }
}
