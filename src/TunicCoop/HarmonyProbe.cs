using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;

namespace TunicCoop
{
    /// <summary>
    /// Diagnostics (Debug.HarmonyProbe): do plain Harmony postfixes fire in this BepInEx build? Patched exactly the
    /// way the TUNIC Randomizer does it (harmony.Patch + AccessTools + HarmonyMethod postfix), counting calls.
    /// While on, the mod's own native chest hook is left out so two detours never sit on one method.
    /// </summary>
    public static class HarmonyProbe
    {
        static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        static readonly List<string> _installed = new List<string>();

        public static void Install(Harmony harmony)
        {
            Try(harmony, typeof(PlayerCharacter), "Update", nameof(PlayerUpdate));
            Try(harmony, typeof(Chest), "IInteractionReceiver_Interact", nameof(ChestInteract));
            var seq = AccessTools.Inner(typeof(Chest), "_openSequence_d__35");
            if (seq != null) Try(harmony, seq, "MoveNext", nameof(OpenSeqMoveNext));
            Try(harmony, typeof(SaveFile), "SetInt", nameof(SaveFileSetInt));
        }

        static void Try(Harmony harmony, Type type, string method, string postfix)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null) { Plugin.L.LogWarning($"[harmony-probe] {type.Name}.{method} not found"); return; }
                harmony.Patch(target, null, new HarmonyMethod(AccessTools.Method(typeof(HarmonyProbe), postfix)));
                _installed.Add($"{type.Name}.{method}");
                Plugin.L.LogInfo($"[harmony-probe] postfix on {type.Name}.{method}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[harmony-probe] {type.Name}.{method}: {e.Message}"); }
        }

        static void Count(string key) { lock (_counts) { _counts.TryGetValue(key, out var n); _counts[key] = n + 1; } }

        public static void PlayerUpdate() => Count("PlayerCharacter.Update");
        public static void ChestInteract() => Count("Chest.IInteractionReceiver_Interact");
        public static void OpenSeqMoveNext() => Count("_openSequence_d__35.MoveNext");
        public static void SaveFileSetInt() => Count("SaveFile.SetInt");

        public static string Stats()
        {
            var parts = new List<string>();
            lock (_counts) foreach (var m in _installed) parts.Add($"{m}={(_counts.TryGetValue(m, out var n) ? n : 0)}");
            return string.Join(" ", parts);
        }
    }
}