using System;
using System.Collections.Generic;
using System.Globalization;
using LiteNetLib.Utils;

namespace TunicCoop
{
    /// <summary>
    /// Shared world + shared inventory.
    ///
    /// TUNIC keeps world progress in SaveFile's int/float/string stores. Hooking SaveFile.SetInt etc. does
    /// NOT work: IL2CPP inlines those tiny setters into their callers, so the hooks never fire (verified:
    /// opening a chest produced zero hook calls). Instead we poll the stores 4x/s and send what changed.
    ///
    /// Inventory (Item.Quantity) is polled the same way and sent as deltas, so simultaneous pickups on two
    /// machines both count ("everything shared": money and consumables too).
    /// </summary>
    public static class WorldSync
    {
        const float PollInterval = 0.25f;

        static readonly Dictionary<string, int> _ints = new Dictionary<string, int>();
        static readonly Dictionary<string, float> _floats = new Dictionary<string, float>();
        static readonly Dictionary<string, string> _strings = new Dictionary<string, string>();
        static readonly Dictionary<string, int> _items = new Dictionary<string, int>();
        static bool _baselined;           // first poll after connecting only records, never sends
        static float _nextPoll;
        static bool _dirty;               // remote change applied → notify StateVariable subscribers
        static readonly HashSet<string> _seenKeys = new HashSet<string>();
        static readonly HashSet<string> _muted = new HashSet<string>();
        static readonly Dictionary<string, (float start, int count)> _rate = new Dictionary<string, (float start, int count)>();

        // Per-player / per-machine keys (respawn point, timers, settings). Refine from "[world] key" log lines.
        static readonly string[] DenySubstrings =
        {
            "playtime", "Playtime", "seconds played", "spawn", "Spawn", "last scene", "Last scene",
            "last campfire", "position", "Position", "settings", "Settings", "SaveFile", "timestamp",
            "Timestamp", "randomizer", "speedrun", "last page viewed",
            // Inventory mirrors itself into 'inventory quantity <Item>' save keys. Items are synced as deltas
            // (ItemDelta); also syncing the absolute key made the two fight (host 47 vs client 51 money).
            "inventory quantity",
            // Per-player statistics.
            "hp_lost", "bloodstain", "Has Ever Died",
        };

        public static bool ShouldSync(string key)
        {
            if (string.IsNullOrEmpty(key) || !Plugin.SyncWorldFlags.Value) return false;
            foreach (var d in DenySubstrings) if (key.Contains(d)) return false;
            return true;
        }

        /// <summary>Call when a session starts/ends: next poll re-baselines silently.</summary>
        public static void Reset()
        {
            _ints.Clear(); _floats.Clear(); _strings.Clear(); _items.Clear();
            _baselined = false;
        }

        // ---------------- polling (every frame; does work 4x/s) ----------------

        public static void Tick(Net net)
        {
            if (_dirty)
            {
                _dirty = false;
                try { StateVariable.NotifyAllSubscribers(); }
                catch (Exception e) { Plugin.L.LogWarning("[world] NotifyAllSubscribers: " + e.Message); }
            }
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextPoll) return;
            _nextPoll = now + PollInterval;
            if (!Plugin.SyncWorldFlags.Value || net == null || !net.Connected || net.LocalId == 255) return;
            if (PlayerCharacter.instance == null) return; // menus / loading: stores may be half-filled

            bool send = _baselined;
            try
            {
                if (SaveFile.intStore != null)
                    foreach (var kv in SaveFile.intStore)
                        if (!_ints.TryGetValue(kv.Key, out var old) || old != kv.Value)
                        {
                            bool fresh = !_ints.ContainsKey(kv.Key);
                            _ints[kv.Key] = kv.Value;
                            // A key that just appeared with 0 is a scene initialising its defaults, not progress;
                            // sending it would reset a teammate who already has that thing done.
                            if (send && !(fresh && kv.Value == 0)) Changed(net, FlagKind.Int, kv.Key, kv.Value.ToString(CultureInfo.InvariantCulture));
                        }
                if (SaveFile.floatStore != null)
                    foreach (var kv in SaveFile.floatStore)
                        if (!_floats.TryGetValue(kv.Key, out var old) || old != kv.Value)
                        { _floats[kv.Key] = kv.Value; if (send) Changed(net, FlagKind.Float, kv.Key, kv.Value.ToString("R", CultureInfo.InvariantCulture)); }
                if (SaveFile.stringStore != null)
                    foreach (var kv in SaveFile.stringStore)
                        if (!_strings.TryGetValue(kv.Key, out var old) || old != kv.Value)
                        { _strings[kv.Key] = kv.Value; if (send) Changed(net, FlagKind.String, kv.Key, kv.Value ?? ""); }

                var list = Inventory.itemList;
                if (list != null)
                    foreach (var it in list)
                    {
                        if (it == null) continue;
                        string name = it.name; int q = it.Quantity;
                        _items.TryGetValue(name, out var old);
                        if (_items.ContainsKey(name) && old == q) continue;
                        bool known = _items.ContainsKey(name);
                        _items[name] = q;
                        if (send && known && !HostSettings.IsPersonal(it))
                        {
                            Plugin.L.LogInfo($"[inv] {name} {old} -> {q}");
                            net.SendFlag(FlagKind.ItemDelta, name, (q - old).ToString(CultureInfo.InvariantCulture));
                        }
                    }
            }
            catch (Exception e) { Plugin.L.LogWarning("[world] poll failed: " + e.Message); }

            if (!_baselined)
            {
                _baselined = true;
                Plugin.L.LogInfo($"[world] baseline: {_ints.Count} ints, {_floats.Count} floats, {_strings.Count} strings, {_items.Count} items");
            }
        }

        static void Changed(Net net, FlagKind kind, string key, string value)
        {
            if (_seenKeys.Add(key)) Plugin.L.LogInfo($"[world] key '{key}' ({kind}) = {value}  sync={ShouldSync(key)}");
            if (!ShouldSync(key) || _muted.Contains(key)) return;
            // Keys that change many times a second are timers, not world progress.
            float now = UnityEngine.Time.unscaledTime;
            if (!_rate.TryGetValue(key, out var rt) || now - rt.start > 2f) rt = (now, 0);
            rt.count++; _rate[key] = rt;
            if (rt.count > 6) { _muted.Add(key); Plugin.L.LogInfo($"[world] muting high-frequency key '{key}'"); return; }
            net.SendFlag(kind, key, value);
        }

        // ---------------- applying remote changes ----------------

        public static void Apply(FlagKind kind, string key, string value)
        {
            try
            {
                if (kind == FlagKind.ItemDelta || kind == FlagKind.ItemSet) { ApplyItem(kind, key, value); return; }
                if (!ShouldSync(key)) return;
                switch (kind)
                {
                    case FlagKind.Int:
                        { int v = int.Parse(value, CultureInfo.InvariantCulture); SaveFile.SetInt(key, v); _ints[key] = v; break; }
                    case FlagKind.Float:
                        { float v = float.Parse(value, CultureInfo.InvariantCulture); SaveFile.SetFloat(key, v); _floats[key] = v; break; }
                    case FlagKind.String:
                        SaveFile.SetString(key, value); _strings[key] = value; break;
                }
                _dirty = true;
                Plugin.L.LogInfo($"[world] remote set '{key}' = {value}");
            }
            catch (Exception e) { Plugin.L.LogWarning($"[world] apply {key} failed: {e.Message}"); }
        }

        /// <summary>Local credit decided by CoinSync (each machine credits itself); not sent as a delta.</summary>
        public static void Credit(string item, int value)
        {
            try { ApplyItem(FlagKind.ItemDelta, item, value.ToString(CultureInfo.InvariantCulture), true); }
            catch (Exception e) { Plugin.L.LogWarning($"[inv] credit {item} failed: {e.Message}"); }
        }

        static void ApplyItem(FlagKind kind, string name, string value, bool local = false)
        {
            var it = Inventory.GetItemByName(name);
            if (it == null) { Plugin.L.LogWarning($"[inv] unknown item '{name}'"); return; }
            if (!local && HostSettings.IsPersonal(it)) return;   // own money/consumables mode: nobody else touches these
            int n = int.Parse(value, CultureInfo.InvariantCulture);
            int before = it.Quantity;
            int after = kind == FlagKind.ItemDelta ? Math.Max(0, before + n) : Math.Max(0, n);
            if (after == before) return;
            it.Quantity = after;
            // Shift the baseline by exactly the applied change. Setting it to it.Quantity swallowed local
            // pickups made since the last poll (verified: A=57 vs B=59 money after simultaneous pickups).
            int applied = it.Quantity - before;
            _items[name] = (_items.TryGetValue(name, out var bl) ? bl : before) + applied;
            Plugin.L.LogInfo($"[inv] remote {name} {before} -> {_items[name]}");
        }

        // ---------------- full world + inventory for joiners ----------------

        public static bool WriteDump(NetDataWriter w)
        {
            var ints = new List<KeyValuePair<string, int>>();
            var floats = new List<KeyValuePair<string, float>>();
            var strings = new List<KeyValuePair<string, string>>();
            var items = new List<KeyValuePair<string, int>>();
            if (Plugin.SyncWorldFlags.Value)
            {
                try
                {
                    if (SaveFile.intStore != null) foreach (var kv in SaveFile.intStore) if (ShouldSync(kv.Key)) ints.Add(new KeyValuePair<string, int>(kv.Key, kv.Value));
                    if (SaveFile.floatStore != null) foreach (var kv in SaveFile.floatStore) if (ShouldSync(kv.Key)) floats.Add(new KeyValuePair<string, float>(kv.Key, kv.Value));
                    if (SaveFile.stringStore != null) foreach (var kv in SaveFile.stringStore) if (ShouldSync(kv.Key)) strings.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
                    if (Inventory.itemList != null) foreach (var it in Inventory.itemList) if (it != null) items.Add(new KeyValuePair<string, int>(it.name, it.Quantity));
                }
                catch (Exception e) { Plugin.L.LogWarning("[world] dump failed: " + e.Message); }
            }
            w.Put(ints.Count); foreach (var kv in ints) { w.Put(kv.Key); w.Put(kv.Value); }
            w.Put(floats.Count); foreach (var kv in floats) { w.Put(kv.Key); w.Put(kv.Value); }
            w.Put(strings.Count); foreach (var kv in strings) { w.Put(kv.Key); w.Put(kv.Value); }
            w.Put(items.Count); foreach (var kv in items) { w.Put(kv.Key); w.Put(kv.Value); }
            Plugin.L.LogInfo($"[world] sent dump: {ints.Count} ints, {floats.Count} floats, {strings.Count} strings, {items.Count} items");
            return true;
        }

        public static void ReadDump(NetDataReader r)
        {
            int n = r.GetInt(); for (int i = 0; i < n; i++) { var k = r.GetString(); Apply(FlagKind.Int, k, r.GetInt().ToString(CultureInfo.InvariantCulture)); }
            int f = r.GetInt(); for (int i = 0; i < f; i++) { var k = r.GetString(); Apply(FlagKind.Float, k, r.GetFloat().ToString("R", CultureInfo.InvariantCulture)); }
            int s = r.GetInt(); for (int i = 0; i < s; i++) { var k = r.GetString(); Apply(FlagKind.String, k, r.GetString()); }
            int it = r.GetInt(); for (int i = 0; i < it; i++) { var k = r.GetString(); Apply(FlagKind.ItemSet, k, r.GetInt().ToString(CultureInfo.InvariantCulture)); }
            Plugin.L.LogInfo($"[world] applied host world: {n} ints, {f} floats, {s} strings, {it} items");
        }
    }
}
