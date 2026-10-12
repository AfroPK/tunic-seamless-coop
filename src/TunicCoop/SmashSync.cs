using System;
using System.Collections.Generic;
using System.Globalization;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// Pots, grass, crates (SmashableObject) are not saved by TUNIC; they are live scene objects that reset on
    /// reload. TUNIC identifies them by position (SmashableObject : PermanentStateByPosition), and so do we:
    /// poll `smashed` 10x/s, send "scene|x|y|z"; receivers smash the object at that spot with coin drops zeroed
    /// (coins are shared inventory: the breaker's machine already dropped them).
    /// </summary>
    public static class SmashSync
    {
        const float PollInterval = 0.1f, RescanInterval = 2f, MatchRadius = 0.6f;

        static readonly List<SmashableObject> _objs = new List<SmashableObject>();
        static readonly HashSet<IntPtr> _knownSmashed = new HashSet<IntPtr>();
        static string _scannedScene;
        static float _nextPoll, _nextScan;

        public static void Tick(Net net, string scene)
        {
            float now = Time.unscaledTime;
            if (now < _nextPoll) return;
            _nextPoll = now + PollInterval;
            if (net == null || !net.Connected || net.LocalId == 255 || PlayerCharacter.instance == null) return;

            if (scene != _scannedScene) { Rescan(scene, true); return; }

            var vanished = new List<IntPtr>();
            foreach (var o in _objs)
            {
                if (o == null)
                {
                    // Some smashables destroy themselves on smash; that can happen between polls.
                    if (!_knownSmashed.Contains(o.Pointer)) vanished.Add(o.Pointer);
                    continue;
                }
                bool smashed;
                try { smashed = o.smashed; } catch { continue; }
                if (!smashed || !_knownSmashed.Add(o.Pointer)) continue;
                net.SendFlag(FlagKind.Smash, Key(scene, o.transform.position), "");
            }
            // Many vanishing at once = scene unloading, not smashing.
            bool unloading = vanished.Count > 3;
            foreach (var ptr in vanished)
            {
                if (!_knownSmashed.Add(ptr) || unloading) continue;
                if (_pos.TryGetValue(ptr, out var p)) net.SendFlag(FlagKind.Smash, Key(scene, p), "");
            }

            // Rescan after checking, so nothing destroyed since the last poll is dropped unseen.
            if (now >= _nextScan) Rescan(scene, false);
        }

        static readonly Dictionary<IntPtr, Vector3> _pos = new Dictionary<IntPtr, Vector3>();

        static void Rescan(string scene, bool sceneChanged)
        {
            _nextScan = Time.unscaledTime + RescanInterval;
            if (sceneChanged) { _knownSmashed.Clear(); _pos.Clear(); _scannedScene = scene; }
            _objs.Clear();
            try
            {
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SmashableObject>()))
                {
                    var s = obj.TryCast<SmashableObject>();
                    if (s == null) continue;
                    _objs.Add(s);
                    _pos[s.Pointer] = s.transform.position;
                    // Already-broken objects at scan time (scene entered with them smashed) are not news.
                    if (sceneChanged && s.smashed) _knownSmashed.Add(s.Pointer);
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[smash] scan failed: " + e.Message); }
        }

        public static void Apply(string key, string localScene)
        {
            var parts = key.Split('|');
            if (parts.Length != 4 || parts[0] != localScene) return; // only live in the same scene
            var pos = new Vector3(F(parts[1]), F(parts[2]), F(parts[3]));
            if (localScene != _scannedScene) Rescan(localScene, true);

            SmashableObject best = null; float bestD = MatchRadius * MatchRadius;
            foreach (var o in _objs)
            {
                if (o == null) continue;
                try
                {
                    if (o.smashed) continue;
                    float d = (o.transform.position - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = o; }
                }
                catch { }
            }
            if (best == null) return;
            try
            {
                best.minCoinDrop = 0; best.maxCoinDrop = 0;   // no duplicate money
                _knownSmashed.Add(best.Pointer);              // don't echo it back
                best.smash(Vector3.zero);
            }
            catch (Exception e) { Plugin.L.LogWarning("[smash] apply failed: " + e.Message); }
        }

        /// <summary>For tests: smashables within radius → "x|y|z|smashed|name;..." nearest first.</summary>
        public static string Describe(Vector3 from, float radius)
        {
            var found = new List<(float d, string s)>();
            try
            {
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SmashableObject>()))
                {
                    var s = obj.TryCast<SmashableObject>();
                    if (s == null) continue;
                    var p = s.transform.position;
                    float d = (p - from).magnitude;
                    if (d <= radius)
                        found.Add((d, string.Format(CultureInfo.InvariantCulture, "{0:F2}|{1:F2}|{2:F2}|{3}|{4}", p.x, p.y, p.z, s.smashed ? 1 : 0, s.gameObject.name)));
                }
            }
            catch (Exception e) { return "error " + e.Message; }
            found.Sort((a, b) => a.d.CompareTo(b.d));
            var parts = new List<string>();
            foreach (var f in found) parts.Add(f.s);
            return string.Join(";", parts);
        }

        static string Key(string scene, Vector3 p) =>
            string.Format(CultureInfo.InvariantCulture, "{0}|{1:F2}|{2:F2}|{3:F2}", scene, p.x, p.y, p.z);

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    }
}
