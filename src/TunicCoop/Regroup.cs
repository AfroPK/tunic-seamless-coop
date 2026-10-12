using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// "Go to teammate": jump to where another player is, even in another area. Every player tells the others
    /// which portal they arrived through; going to them uses TUNIC's own warp-to-portal (the dev cheat the game
    /// ships), then places us next to them once their position streams in.
    ///
    /// Also announces when a teammate walks into a boss arena, so nobody misses the fight (F5 to join them).
    /// </summary>
    public static class Regroup
    {
        public static readonly Dictionary<string, string> BossArenas = new Dictionary<string, string>
        {
            { "Forest Boss Room", "Guard Captain" }, { "Fortress Arena", "Siege Engine" }, { "Library Arena", "Librarian" },
            { "Spirit Arena", "The Heir" }, { "Cathedral Arena", "Cathedral gauntlet" }, { "ziggurat2020_3", "Boss Scavenger" },
        };

        class Where { public string Scene; public string Portal; public float At; }
        static readonly Dictionary<byte, Where> _where = new Dictionary<byte, Where>();
        static string _announcedScene;
        static byte _goTarget = 255; static float _goDeadline, _goArrivedAt = -1f; static string _goScene; static float _goStartedAt;
        static byte _promptFrom = 255; static float _promptAt = -100f;

        public static void Reset() { _where.Clear(); _announcedScene = null; _goTarget = 255; _promptFrom = 255; }

        public static void Tick(Net net, string scene)
        {
            if (net == null || !net.Connected || net.LocalId == 255) return;
            var pc = PlayerCharacter.instance;
            if (pc == null || scene == "Loading" || string.IsNullOrEmpty(scene)) return;

            // Tell everyone where we arrived (once per area visit).
            if (scene != _announcedScene)
            {
                _announcedScene = scene;
                Announce(net, scene);
            }

            // Finishing a "go to": after the warp, stand next to them as soon as we know where they are.
            if (_goTarget != 255)
            {
                if (Time.unscaledTime > _goDeadline) { _goTarget = 255; return; }
                // Only once the warp has really finished: the scene name flips before loading completes, and moving
                // the fox mid-transition makes the game fall back to the old area.
                if (scene != _goScene || SceneLoader.sceneNameToLoad != _goScene || Time.unscaledTime - _goStartedAt < 3f) { _goArrivedAt = -1f; return; }
                if (_goArrivedAt < 0f) { _goArrivedAt = Time.unscaledTime; return; }
                if (Time.unscaledTime - _goArrivedAt < 1.5f) return;
                var p = PuppetRegistry.Position(_goTarget);
                var latest = FindLatest(_goTarget);
                if (p != null && latest != null && latest.Scene == scene)
                {
                    Autopilot.Teleport(pc, p.Value + new Vector3(1.2f, 0.2f, 0f));
                    Plugin.L.LogInfo($"[regroup] placed next to player {_goTarget}");
                    _goTarget = 255;
                }
            }
        }

        public static void Announce(Net net, string scene)
        {
            string portal = "";
            try { portal = PlayerCharacterSpawn.portalIDToSpawnAt ?? ""; } catch { }
            net.SendFlag(FlagKind.Arrived, net.LocalId.ToString(CultureInfo.InvariantCulture), scene + "|" + portal);
        }

        /// <summary>A player arrived somewhere.</summary>
        public static void OnArrived(Net net, byte from, string value, string ourScene)
        {
            var v = value.Split('|');
            if (v.Length < 2) return;
            _where[from] = new Where { Scene = v[0], Portal = v[1], At = Time.unscaledTime };
            if (BossArenas.TryGetValue(v[0], out var boss) && v[0] != ourScene && !ReviveSystem.Downed)
            {
                _promptFrom = from; _promptAt = Time.unscaledTime;
                CoopUI.Toast($"{Name(net, from)} entered the {boss} arena  ·  press F5 to join them", CoopUI.Kind.Warn, 12f);
            }
        }

        /// <summary>F5: join whoever just entered a boss arena, else cycle through teammates.</summary>
        static int _cycle;
        public static void QuickGo(Net net)
        {
            if (net == null || !net.Connected) { CoopUI.Toast("Not in a session"); return; }
            if (_promptFrom != 255 && Time.unscaledTime - _promptAt < 60f && net.Players.ContainsKey(_promptFrom)) { GoTo(net, _promptFrom); _promptFrom = 255; return; }
            var ids = new List<byte>(); foreach (var id in net.Players.Keys) if (id != net.LocalId) ids.Add(id);
            if (ids.Count == 0) { CoopUI.Toast("Nobody else is here yet"); return; }
            ids.Sort();
            GoTo(net, ids[_cycle++ % ids.Count]);
        }

        public static void GoTo(Net net, byte id)
        {
            var pc = PlayerCharacter.instance;
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (pc == null || scene == "Loading") return;
            if (ReviveSystem.Downed) { CoopUI.Toast("You can't travel while you're down", CoopUI.Kind.Warn); return; }
            var latest = FindLatest(id);
            string name = Name(net, id);
            string theirScene = latest != null ? latest.Scene : (_where.TryGetValue(id, out var w0) ? w0.Scene : null);
            if (string.IsNullOrEmpty(theirScene) || theirScene == "Loading") { CoopUI.Toast($"{name} is loading, try again in a moment"); return; }

            if (theirScene == scene)
            {
                var p = PuppetRegistry.Position(id);
                if (p == null) { CoopUI.Toast($"Can't see {name} right now"); return; }
                Autopilot.Teleport(pc, p.Value + new Vector3(1.2f, 0.2f, 0f));
                CoopUI.Toast($"Joined {name}", CoopUI.Kind.Good, 3f);
                return;
            }
            if (!_where.TryGetValue(id, out var w) || w.Scene != theirScene || string.IsNullOrEmpty(w.Portal))
            {
                // Without an entry portal the warp would drop us into the void.
                CoopUI.Toast($"Can't find a way into {name}'s area yet. It updates as soon as they walk through a door.", CoopUI.Kind.Warn, 6f);
                return;
            }
            _goTarget = id; _goScene = theirScene; _goDeadline = Time.unscaledTime + 45f; _goArrivedAt = -1f; _goStartedAt = Time.unscaledTime;
            CoopUI.Toast($"Travelling to {name} ({GameInfo.FriendlyArea(theirScene)})…", CoopUI.Kind.Info, 4f);
            Plugin.L.LogInfo($"[regroup] warp to {theirScene} @ '{w.Portal}' to reach player {id}");
            Cheats.CheatWarpToPortal(theirScene, w.Portal);
        }

        public static string Describe()
        {
            var parts = new List<string>();
            foreach (var kv in _where) parts.Add($"{kv.Key}:{kv.Value.Scene}@{kv.Value.Portal}");
            return $"going={(_goTarget == 255 ? "-" : _goTarget.ToString())} prompt={_promptFrom} where=[{string.Join("; ", parts)}]";
        }

        static Snapshot FindLatest(byte id)
        {
            foreach (var p in PuppetRegistry.All()) if (p.PlayerId == id) return p.Latest;
            return null;
        }

        static string Name(Net net, byte id) => net.Players.TryGetValue(id, out var n) ? n : "Player " + id;
    }
}
