using BepInEx.Configuration;

namespace CloudpunkVR;

internal class Settings
{
    public readonly ConfigEntry<int> ResolutionPercent;
    public readonly ConfigEntry<bool> ForceCockpit;
    public readonly ConfigEntry<float> CockpitTilt;
    public readonly ConfigEntry<bool> HeadRelativeWalk;
    public readonly ConfigEntry<string> TurnMode;
    public readonly ConfigEntry<float> SmoothTurnSpeed;
    public readonly ConfigEntry<float> SnapTurnDegrees;
    public readonly ConfigEntry<float> HandPitch;
    public readonly ConfigEntry<float> HandYaw;
    public readonly ConfigEntry<float> HandRoll;
    public readonly ConfigEntry<bool> DisableComfortEffects;
    public readonly ConfigEntry<float> UIDistance;
    public readonly ConfigEntry<float> UIWidth;
    public readonly ConfigEntry<float> UIHeight;
    public readonly ConfigEntry<float> UIMenuDistance;
    public readonly ConfigEntry<int> VRShadows;
    public readonly ConfigEntry<float> DrawDistance;
    public readonly ConfigEntry<bool> AutoRecenter;
    public readonly ConfigEntry<bool> FirstPersonOnFoot;
    public readonly ConfigEntry<bool> ShowBody;
    public readonly ConfigEntry<float> WindshieldBlur;
    public readonly ConfigEntry<bool> HeadlightGlow;
    public readonly ConfigEntry<float> ClimbSmoothing;
    public readonly ConfigEntry<float> HeadlightGlowIntensity;

    public readonly ConfigEntry<int> MinimapEvery;
    public readonly ConfigEntry<int> MirrorEvery;
    public readonly ConfigEntry<int> RainMaskEvery;
    public readonly ConfigEntry<int> ParkingEvery;
    public readonly ConfigEntry<int> TuningVersion;
    public readonly ConfigEntry<bool> FullSpeedInBackground;
    public readonly ConfigEntry<int> JobWorkerThreads;

    public readonly ConfigEntry<bool> DiagnosticsKeys;
    public readonly ConfigEntry<bool> FrameTimingLog;

    public readonly ConfigEntry<bool> CooEnabled;
    public readonly ConfigEntry<int> CooPort;
    public readonly ConfigEntry<string> CooView;

    public readonly ConfigFile File;

    public Settings(ConfigFile cfg)
    {
        File = cfg;
        ResolutionPercent = cfg.Bind("VR", "ResolutionPercent", 70,
            "Eye resolution per axis as a percentage of what SteamVR recommends for the headset (which already includes " +
            "SteamVR's and the streaming app's resolution settings). Raise it for " +
            "sharpness, lower it if the headset drops frames.");
        ForceCockpit = cfg.Bind("VR", "ForceCockpit", true,
            "Drive from the cockpit. The view button (left stick click) still switches to the chase camera.");
        CockpitTilt = cfg.Bind("VR", "CockpitTilt", 0.5f,
            "How much of the car's bank and pitch the cockpit view follows: 0 keeps the horizon level, 1 rolls fully with the car.");
        HeadRelativeWalk = cfg.Bind("VR", "HeadRelativeWalk", true,
            "On foot, push the stick forward to walk where you look; off: where the left controller points.");
        TurnMode = cfg.Bind("VR", "TurnMode", "smooth", new ConfigDescription(
            "Right stick on foot. smooth: turns continuously around the head while the stick is pushed, faster the further it " +
            "goes (as in VRChat); snap: turns in steps of SnapTurnDegrees.", new AcceptableValueList<string>("smooth", "snap")));
        SmoothTurnSpeed = cfg.Bind("VR", "SmoothTurnSpeed", 150f, "Smooth turning speed with the stick pushed all the way, degrees per second.");
        SnapTurnDegrees = cfg.Bind("VR", "SnapTurnDegrees", 30f, "Angle of one snap turn.");
        const string handCal = " The hand calibration sets all three: on foot hold the left stick down for 2 s, then within 3 s hold the " +
                               "controllers in front of you with the palms facing each other and the fingers pointing ahead.";
        HandPitch = cfg.Bind("VR", "HandPitch", 25f, "How the first-person hand sits on the controller: the knuckles point this many degrees below the controller's pointing direction." + handCal);
        HandYaw = cfg.Bind("VR", "HandYaw", 0f, "The knuckles point this many degrees outward from the controller's pointing direction (mirrored for the left hand)." + handCal);
        HandRoll = cfg.Bind("VR", "HandRoll", 14f, "The back of the hand is turned this many degrees up from facing straight outward, around the knuckles (mirrored for the left hand)." + handCal);
        // TurnMode replaced the SmoothTurn switch (off by default); take its line out of existing files.
        DropOldEntry(cfg, new ConfigDefinition("VR", "SmoothTurn"));
        DisableComfortEffects = cfg.Bind("VR", "DisableComfortEffects", false,
            "Turn off depth of field, motion blur, chromatic aberration, lens distortion, vignette and grain in VR.");
        UIDistance = cfg.Bind("VR", "UIDistance", 1.6f, "Distance of the floating UI panel, meters.");
        UIWidth = cfg.Bind("VR", "UIWidth", 1.8f, "Width of the floating UI panel, meters.");
        UIHeight = cfg.Bind("VR", "UIHeight", -0.1f, "Height of the UI panel relative to the eyes, meters.");
        UIMenuDistance = cfg.Bind("VR", "UIMenuDistance", 1.3f, "Distance of the UI panel while a menu, dialog or inventory is open; it keeps the gameplay width so menus read larger.");
        DrawDistance = cfg.Bind("VR", "DrawDistance", 2500f,
            "How far the city shows in VR, metres: the far clip plane, the distance fog (clear to 30% of it, thickening to the far " +
            "plane), the game's global fog and the cull distance of buildings and floors. The game uses about 600. Larger values " +
            "cost render-thread time (every object is drawn once per eye). 0 keeps the game's values.");
        AutoRecenter = cfg.Bind("VR", "AutoRecenter", true,
            "Recenter the view (height, position and facing) when gameplay starts after a loading screen and when switching " +
            "between cockpit and on foot, so the eyes land at the driver's seat and at the character's eyes.");
        FirstPersonOnFoot = cfg.Bind("VR", "FirstPersonOnFoot", true,
            "On foot, look through the character's eyes instead of the game's overhead and orbit cameras.");
        ShowBody = cfg.Bind("VR", "ShowBody", true,
            "In first person on foot, show the character's body below you with the arms following the controllers (head hidden).");
        WindshieldBlur = cfg.Bind("VR", "WindshieldBlur", 0.35f,
            "Frosting of the car windows as a share of the game's own: 1 = as in the flat game, 0 = clear glass. The tint and the " +
            "rain stay either way.");
        HeadlightGlow = cfg.Bind("VR", "HeadlightGlow", true,
            "Glows on car headlights in place of the game's screen-space lens flares (those cannot line up in both eyes).");
        ClimbSmoothing = cfg.Bind("VR", "ClimbSmoothing", 0.95f,
            "How fast the car's climb/sink input follows the stick (UE FInterpTo speed; 0 = no smoothing). 0.95 is Aircar's " +
            "climb response; the game's own climb reacts in about 0.4 s, Aircar's in about 1 s. Speeds and limits stay the game's.");
        HeadlightGlowIntensity = cfg.Bind("VR", "HeadlightGlowIntensity", 1f, "Brightness of the headlight glows.");
        VRShadows = cfg.Bind("VR", "VRShadows", 1, "Shadow range in VR: 0 = game setting, 1 = 120 m with 2 cascades, 2 = 60 m with 2 cascades. Shadows are drawn once per eye.");

        MinimapEvery = cfg.Bind("Performance", "MinimapEvery", 3, "Render the minimap camera every N frames (1 = every frame).");
        MirrorEvery = cfg.Bind("Performance", "MirrorEvery", 6, "Render the rear-view mirror (a 64x32 picture of the city behind) every N frames.");
        RainMaskEvery = cfg.Bind("Performance", "RainMaskEvery", 4, "Render the windshield rain masks every N frames.");
        ParkingEvery = cfg.Bind("Performance", "ParkingEvery", 6, "Render the parking display camera every N frames.");
        FullSpeedInBackground = cfg.Bind("Performance", "FullSpeedInBackground", true,
            "Keep full CPU priority when the game window is not in front (in VR it usually is not). On CPUs with " +
            "performance and efficiency cores Windows otherwise moves the game to the slow cores.");
        // Read at engine start by the preloader patcher (BepInEx/patchers/CloudpunkVR); bound here so it shows up
        // with the other settings.
        // Settings still at an earlier default move to the current one; values the player changed stay.
        TuningVersion = cfg.Bind("Performance", "TuningVersion", 0, "Which set of defaults this file was last brought up to (do not edit).");
        if (TuningVersion.Value < 1)
        {
            if (ResolutionPercent.Value == 50) ResolutionPercent.Value = 70;
            if (MirrorEvery.Value == 2) MirrorEvery.Value = 6;
            if (RainMaskEvery.Value == 2) RainMaskEvery.Value = 4;
            if (ParkingEvery.Value == 2) ParkingEvery.Value = 6;
            TuningVersion.Value = 1;
        }
        JobWorkerThreads = cfg.Bind("Performance", "JobWorkerThreads", -1,
            "Number of Unity job worker threads: -1 = automatic (at most 8), 0 = engine default (one per logical processor minus one), " +
            "N = exactly N. Takes effect at the next start.");

        CooEnabled = cfg.Bind("Coo", "Enabled", true,
            "Let the Cortico World (cortico-world-cloudpunk) connect: quests and dialogue for the agent, navigation and autopilot, " +
            "可缇 on the call channel (Spout), the streamer HUD. Listens on the loopback address only.");
        CooPort = cfg.Bind("Coo", "Port", 47660, "TCP port on 127.0.0.1 the World connects to.");
        CooView = cfg.Bind("Coo", "View", "off",
            "Stream view until the World sends its own: off (the eye mirror); spectator (VR: a steadied single view in the desktop window for OBS).");
        DropOldEntry(cfg, new ConfigDefinition("Coo", "Skin"));
        DropOldEntry(cfg, new ConfigDefinition("Coo", "Hologram"));
        FrameTimingLog = cfg.Bind("Debug", "FrameTimingLog", false,
            "Every 30 s, log SteamVR compositor's per-frame record (presents, reprojection and throttle flags, game GPU time). " +
            "For diagnosing frame pacing; off by default.");
        DiagnosticsKeys = cfg.Bind("Debug", "DiagnosticsKeys", false,
            "F9 dumps cameras/canvases/bindings to the BepInEx log, F11 dumps call graphs of camera methods.");
    }

    // BepInEx keeps lines it has no binding for ("orphaned") and writes them back on every save.
    static void DropOldEntry(ConfigFile cfg, ConfigDefinition def)
    {
        try
        {
            var prop = typeof(ConfigFile).GetProperty("OrphanedEntries",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (prop?.GetValue(cfg) is System.Collections.IDictionary orphans && orphans.Contains(def))
            {
                orphans.Remove(def);
                cfg.Save();
            }
        }
        catch (System.Exception e) { Plugin.L.LogWarning($"settings: could not drop {def}: {e.Message}"); }
    }
}
