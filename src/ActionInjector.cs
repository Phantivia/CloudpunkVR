using System;
using System.Collections.Generic;
using InControl;
using Il2CppInterop.Runtime;

namespace CloudpunkVR;

// Presses game actions that have no pad binding (the full map is keyboard-only) for one InControl tick.
// Runs in InputManager.OnUpdate, after the player action sets updated, so game code reading WasPressed later in
// the frame sees the press.
internal static class ActionInjector
{
    static readonly HashSet<string> pending = new();
    static Il2CppSystem.Action<ulong, float> hook;

    public static void Press(string action)
    {
        if (hook == null)
        {
            hook = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ulong, float>>(new Action<ulong, float>(OnUpdate));
            InputManager.add_OnUpdate(hook);
        }
        pending.Add(action);
    }

    static void OnUpdate(ulong tick, float dt)
    {
        if (pending.Count == 0) return;
        var set = GameInput.Instance != null ? GameInput.Instance.playerActions : null;
        foreach (var name in pending)
        {
            PlayerAction action = name switch
            {
                "FullMap" => set?.FullMap,
                "Menu" => set?.Menu,
                _ => null,
            };
            action?.CommitWithState(true, tick, dt);
        }
        pending.Clear();
    }
}
