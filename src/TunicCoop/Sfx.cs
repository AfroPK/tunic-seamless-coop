using System;
using FMODUnity;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>Sounds borrowed from the game itself (FMOD events), so they fit TUNIC's soundscape.</summary>
    public static class Sfx
    {
        static EventReference _revive;
        static bool _haveRevive, _looked;
        public static int Played;

        /// <summary>The "rested at a campfire" chime: getting back up feels like that.</summary>
        public static void Revive(Vector3 at)
        {
            try
            {
                if (!_haveRevive && !_looked)
                {
                    _looked = true;
                    foreach (var o in Resources.FindObjectsOfTypeAll(UnhollowerRuntimeLib.Il2CppType.Of<Campfire>()))
                    {
                        var c = o.TryCast<Campfire>();
                        if (c == null) continue;
                        _revive = c.useCheckpointSFX; _haveRevive = true; break;
                    }
                    if (!_haveRevive) _looked = false;   // no campfire loaded in this area yet: try again next time
                }
                if (!_haveRevive) return;
                RuntimeManager.PlayOneShot(_revive, at);
                Played++;
            }
            catch (Exception e) { Plugin.L.LogInfo("[sfx] revive sound unavailable: " + e.Message); _looked = true; }
        }
    }
}
