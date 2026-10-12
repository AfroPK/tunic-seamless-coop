using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// World coins are pooled ItemPickups (CoinSpawner.poolList → PooledFX.pool, one pool per denomination).
    ///
    /// Exactly-once money with an owner per coin (the machine that dropped it):
    ///  - While connected, every tracked coin has quantityToGive = 0, so the GAME never pays for a coin.
    ///    (Letting the game pay was unfixable: a coin could be credited locally in the same instant another
    ///    player's pickup removed it, which double-paid; verified 54 vs expected 51.)
    ///  - A fox touching a coin = pickup. Owner's own coin: credit + broadcast CoinTaken. Someone else's coin:
    ///    send CoinClaim; the owner accepts the first claim and broadcasts CoinTaken.
    ///  - Everyone credits a coin's value once, on CoinTaken (or locally as the owner), through WorldSync so the
    ///    change is not echoed as an inventory delta.
    ///  - A coin that times out on its owner → CoinGone (despawn, no money).
    /// </summary>
    public static class CoinSync
    {
        const float PollInterval = 0.05f, SettleDelay = 1.0f, PickupRadius = 2.5f;

        class Tracked
        {
            public int Id; public GameObject Go; public ItemPickup Ip; public float SpawnTime; public bool Settled;
            public byte Owner; public int Value; public string Item; public bool Taken;
        }

        static readonly Dictionary<IntPtr, Tracked> _byPtr = new Dictionary<IntPtr, Tracked>();
        static readonly Dictionary<int, Tracked> _byId = new Dictionary<int, Tracked>();
        static readonly HashSet<IntPtr> _wasActive = new HashSet<IntPtr>();
        static readonly HashSet<int> _credited = new HashSet<int>();
        static int _counter;
        static float _nextPoll;
        static string _scene;
        static Net _net;

        static byte OwnerOf(int id) => (byte)((id >> 24) & 0xFF);

        public static void Tick(Net net, string scene)
        {
            _net = net;
            float now = Time.unscaledTime;
            if (now < _nextPoll) return;
            _nextPoll = now + PollInterval;
            if (scene != _scene) { _scene = scene; ClearTracking(); }
            if (net == null || !net.Connected || net.LocalId == 255 || PlayerCharacter.instance == null) return;

            List<PooledFX> pools;
            try { CoinSpawner.setupIfNeeded(); pools = ManagedPools(); }
            catch (Exception e) { Plugin.L.LogWarning("[coin] pools: " + e.Message); return; }

            var me = PlayerCharacter.instance.transform.position;
            foreach (var pool in pools)
            {
                var arr = pool.pool;
                if (arr == null) continue;
                for (int i = 0; i < arr.Length; i++)
                {
                    var go = arr[i];
                    if (go == null) continue;
                    var ptr = go.Pointer;
                    bool active = go.activeInHierarchy;
                    bool was = _wasActive.Contains(ptr);
                    if (active && !was)
                    {
                        _wasActive.Add(ptr);
                        if (!_byPtr.ContainsKey(ptr)) Announce(net, scene, go, ptr);
                    }
                    else if (!active && was)
                    {
                        _wasActive.Remove(ptr);
                        if (_byPtr.TryGetValue(ptr, out var t)) Deactivated(net, t, me);
                    }
                }
            }

            // Once a locally dropped coin has come to rest, tell others where.
            foreach (var t in new List<Tracked>(_byId.Values))
            {
                if (t.Owner != net.LocalId || t.Settled || now - t.SpawnTime < SettleDelay || t.Go == null) continue;
                t.Settled = true;
                net.SendFlag(FlagKind.CoinMove, t.Id.ToString(CultureInfo.InvariantCulture), Vec(t.Go.transform.position));
            }
        }

        // Coins a dying MIRROR monster drops locally are duplicates of the owner's drop → hide them.
        static Vector3 _suppressAt; static float _suppressR, _suppressUntil;
        public static void SuppressNear(Vector3 pos, float radius, float seconds)
        { _suppressAt = pos; _suppressR = radius; _suppressUntil = Time.unscaledTime + seconds; }

        static void Announce(Net net, string scene, GameObject go, IntPtr ptr)
        {
            var ip = go.GetComponent<ItemPickup>();
            if (ip == null || ip.quantityToGive <= 0) return;
            if (Time.unscaledTime < _suppressUntil && (go.transform.position - _suppressAt).magnitude < _suppressR)
            {
                Plugin.L.LogInfo($"[coin] suppressed mirror-death coin at {Vec(go.transform.position)}");
                _wasActive.Remove(ptr); go.SetActive(false);
                return;
            }
            var t = new Tracked
            {
                Id = (net.LocalId << 24) | (++_counter & 0xFFFFFF), Go = go, Ip = ip, SpawnTime = Time.unscaledTime,
                Owner = net.LocalId, Value = ip.quantityToGive, Item = ip.itemToGive != null ? ip.itemToGive.name : "MoneySmall",
            };
            ip.quantityToGive = 0;                       // the game must not pay; we credit exactly once
            _byPtr[ptr] = t; _byId[t.Id] = t;
            Plugin.L.LogInfo($"[coin] local coin {t.Id:X} value {t.Value} at {Vec(go.transform.position)}");
            net.SendFlag(FlagKind.CoinSpawn, $"{scene}|{t.Id.ToString(CultureInfo.InvariantCulture)}|{Vec(go.transform.position)}",
                t.Value.ToString(CultureInfo.InvariantCulture));
        }

        static void Deactivated(Net net, Tracked t, Vector3 me)
        {
            Restore(t);
            bool touched = t.Go != null && (t.Go.transform.position - me).magnitude < PickupRadius;
            if (t.Taken) { Forget(t); return; }            // we removed it (CoinTaken/CoinGone)
            if (!touched)
            {
                if (t.Owner == net.LocalId)
                {
                    Plugin.L.LogInfo($"[coin] {t.Id:X} despawned");
                    net.SendFlag(FlagKind.CoinGone, t.Id.ToString(CultureInfo.InvariantCulture), "");
                }
                Forget(t);
                return;
            }
            if (t.Owner == net.LocalId)
            {
                Take(net, t, net.LocalId);
            }
            else
            {
                Plugin.L.LogInfo($"[coin] claim {t.Id:X} from owner {t.Owner}");
                net.SendFlag(FlagKind.CoinClaim, t.Id.ToString(CultureInfo.InvariantCulture), "");
                t.Taken = true;   // keep the record until the owner's CoinTaken credits it
            }
        }

        /// <summary>Owner side: the coin is taken by `by` → credit once and tell everyone.</summary>
        static void Take(Net net, Tracked t, byte by)
        {
            t.Taken = true;
            Credit(t.Id, t.Item, t.Value);
            Plugin.L.LogInfo($"[coin] {t.Id:X} taken by player {by}, +{t.Value} {t.Item}");
            net.SendFlag(FlagKind.CoinTaken, t.Id.ToString(CultureInfo.InvariantCulture), $"{t.Value}|{t.Item}|{by}");
            if (t.Go != null && t.Go.activeSelf) { _wasActive.Remove(t.Go.Pointer); Restore(t); t.Go.SetActive(false); }
            Forget(t);
        }

        static void Credit(int id, string item, int value)
        {
            if (!_credited.Add(id)) return;
            WorldSync.Credit(item, value);
        }

        // ---------------- remote events ----------------

        public static void ApplySpawn(string key, string value, string localScene)
        {
            var p = key.Split('|');
            if (p.Length != 5 || p[0] != localScene) return;
            int id = int.Parse(p[1], CultureInfo.InvariantCulture);
            int qty = int.Parse(value, CultureInfo.InvariantCulture);
            if (_byId.ContainsKey(id) || _credited.Contains(id)) return;
            var pos = new Vector3(F(p[2]), F(p[3]), F(p[4]));

            try
            {
                CoinSpawner.setupIfNeeded();
                PooledFX match = null;
                foreach (var pool in ManagedPools())
                {
                    var tip = pool.templateObject != null ? pool.templateObject.GetComponent<ItemPickup>() : null;
                    if (tip != null && tip.quantityToGive == qty) { match = pool; break; }
                }
                if (match == null) { Plugin.L.LogWarning($"[coin] no pool for value {qty}"); return; }
                var go = match.GetNext(pos);
                if (go == null) return;
                var ip = go.GetComponent<ItemPickup>();
                var t = new Tracked
                {
                    Id = id, Go = go, Ip = ip, SpawnTime = Time.unscaledTime, Settled = true, Owner = OwnerOf(id), Value = qty,
                    Item = ip != null && ip.itemToGive != null ? ip.itemToGive.name : "MoneySmall",
                };
                if (ip != null) ip.quantityToGive = 0;
                _byPtr[go.Pointer] = t; _byId[id] = t;
                _wasActive.Add(go.Pointer);
                Plugin.L.LogInfo($"[coin] remote coin {id:X} value {qty} at {Vec(pos)}");
            }
            catch (Exception e) { Plugin.L.LogWarning("[coin] spawn failed: " + e.Message); }
        }

        public static void ApplyMove(string key, string value)
        {
            if (!_byId.TryGetValue(int.Parse(key, CultureInfo.InvariantCulture), out var t) || t.Go == null || !t.Go.activeInHierarchy) return;
            var p = value.Split('|');
            if (p.Length != 3) return;
            var target = new Vector3(F(p[0]), F(p[1]), F(p[2]));
            if ((t.Go.transform.position - target).sqrMagnitude > 0.04f)
            {
                t.Go.transform.position = target;
                var rb = t.Go.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            }
        }

        /// <summary>Owner receives someone's pickup claim: first claim wins.</summary>
        public static void ApplyClaim(string key, byte from)
        {
            int id = int.Parse(key, CultureInfo.InvariantCulture);
            if (_net == null || OwnerOf(id) != _net.LocalId) return;
            if (!_byId.TryGetValue(id, out var t) || t.Taken) return;   // already taken (by us or an earlier claim)
            Take(_net, t, from);
        }

        public static void ApplyTaken(string key, string value)
        {
            int id = int.Parse(key, CultureInfo.InvariantCulture);
            var p = value.Split('|');
            if (p.Length >= 2) Credit(id, p[1], int.Parse(p[0], CultureInfo.InvariantCulture));
            Remove(id);
        }

        public static void ApplyGone(string key) => Remove(int.Parse(key, CultureInfo.InvariantCulture));

        static void Remove(int id)
        {
            if (!_byId.TryGetValue(id, out var t)) return;
            t.Taken = true;
            if (t.Go != null && t.Go.activeSelf) { _wasActive.Remove(t.Go.Pointer); Restore(t); t.Go.SetActive(false); }
            Forget(t);
        }

        // ---------------- helpers ----------------

        /// <summary>For tests: "count | value(owner:id)@x,z ..." for active coin pickups within radius.</summary>
        public static string Describe(Vector3 from, float radius)
        {
            var parts = new List<string>();
            try
            {
                CoinSpawner.setupIfNeeded();
                foreach (var pool in ManagedPools())
                {
                    var arr = pool.pool;
                    if (arr == null) continue;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var go = arr[i];
                        if (go == null || !go.activeInHierarchy) continue;
                        var p = go.transform.position;
                        if ((p - from).magnitude > radius) continue;
                        string tag = _byPtr.TryGetValue(go.Pointer, out var t) ? $"{t.Value}({t.Owner}:{t.Id:X})" : "?(untracked)";
                        parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}@{1:F1},{2:F1}", tag, p.x, p.z));
                    }
                }
            }
            catch (Exception e) { return "error " + e.Message; }
            return parts.Count + " | " + string.Join(" ", parts);
        }

        static void Restore(Tracked t) { if (t.Ip != null && t.Value > 0) t.Ip.quantityToGive = t.Value; }

        static List<PooledFX> ManagedPools()
        {
            var l = new List<PooledFX>();
            var pl = CoinSpawner.poolList;
            if (pl != null) foreach (var p in pl) if (p != null) l.Add(p);
            return l;
        }

        static void Forget(Tracked t)
        {
            _byId.Remove(t.Id);
            if (t.Go != null) _byPtr.Remove(t.Go.Pointer);
        }

        static void ClearTracking()
        {
            foreach (var t in _byId.Values) Restore(t);
            _byPtr.Clear(); _byId.Clear(); _wasActive.Clear();
        }

        static string Vec(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "{0:F2}|{1:F2}|{2:F2}", v.x, v.y, v.z);
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    }
}
