using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CloudpunkVR;

// Dev (ptime <seconds>): time spent inside the plugin's own hooks, per hook, per frame. Off it costs one bool check per
// scope. Scopes nest (a Guard inside a camera callback is counted in both). The entry into a hook from IL2CPP (the
// interop trampoline) is outside the scopes; calls per frame show how often that is paid.
internal static class PluginTiming
{
    static bool on;
    static float until;
    static int frames;
    static readonly Dictionary<string, (long ticks, int calls)> acc = new();

    public readonly struct Scope : IDisposable
    {
        readonly string key;
        readonly long start;
        public Scope(string key) { this.key = key; start = on ? Stopwatch.GetTimestamp() : 0; }
        public void Dispose()
        {
            if (start == 0 || !on) return;
            long t = Stopwatch.GetTimestamp() - start;
            acc[key] = acc.TryGetValue(key, out var v) ? (v.ticks + t, v.calls + 1) : (t, 1);
        }
    }

    public static Scope Measure(string key) => new(key);

    public static void Start(float seconds)
    {
        acc.Clear();
        frames = 0;
        until = UnityEngine.Time.unscaledTime + seconds;
        on = true;
    }

    // Once per frame from VRHost.Update.
    public static void Tick()
    {
        if (!on) return;
        frames++;
        if (UnityEngine.Time.unscaledTime < until) return;
        on = false;
        double msPerTick = 1000.0 / Stopwatch.Frequency;
        double total = 0;
        Plugin.L.LogInfo($"ptime: {frames} frames");
        foreach (var kv in acc.OrderByDescending(kv => kv.Value.ticks))
        {
            double ms = kv.Value.ticks * msPerTick / frames;
            Plugin.L.LogInfo("ptime " + ms.ToString("F3").PadLeft(7) + " ms/frame " + (kv.Value.calls / (double)frames).ToString("F1").PadLeft(7) + " calls/frame  " + kv.Key);
        }
    }
}
