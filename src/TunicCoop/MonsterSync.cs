using System;
using System.Collections.Generic;
using System.Globalization;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// Enemy sync with PER-MONSTER ownership by proximity.
    ///
    /// Every monster (id = RuntimeStableID.ID, identical on all machines: verified) has exactly one owner, the
    /// machine whose game runs its AI. TUNIC's AI chases the local fox, so the owner should be the NEAREST player:
    /// then the unmodified AI does the right thing for every monster type. (Redirecting AI targets on one owner
    /// failed: steering differs per monster type; verified slimes still pathed to the owner's fox and fell off a
    /// cliff.)
    ///
    ///  - Default owner = lowest player id in the scene.
    ///  - The current owner (only it — single writer) hands a monster to another player who is ≥ HandoffMargin
    ///    closer: reliable MonsterOwner(id, newOwner, hp). The new owner restores hp and lets its AI run.
    ///  - Owner streams its monsters (unreliable, chunked); everyone else MIRRORS them: AI/NavMesh off,
    ///    kinematic, transform + animation from the stream, hp pinned to the owner's hp. A local hit lowers hp
    ///    below the pin by exactly the damage this game computed → MonsterHit → owner applies it.
    ///  - Owner death → reliable MonsterDead → mirrors run the real death; mirror coin drops are suppressed (the
    ///    owner's coins arrive via CoinSync).
    /// </summary>
    public static class MonsterSync
    {
        const float StreamInterval = 1f / 15f, StreamRadius = 45f, InterpDelay = 0.12f, HandoffMargin = 4f;

        class Mon
        {
            public Monster M; public int Id; public byte Owner = 255; public bool Mirrored; public bool Dead;
            public int LastHp = -1; public int Pin = -1; public int AppliedAnim; public float LastHandoff;
            public readonly List<(float t, Vector3 pos, float yaw, int anim, float norm)> Buf = new List<(float, Vector3, float, int, float)>();
        }

        static readonly Dictionary<int, Mon> _byId = new Dictionary<int, Mon>();
        static string _scene;
        static float _nextStream, _nextScan;
        static readonly Dictionary<byte, (float offset, bool have)> _clock = new Dictionary<byte, (float, bool)>();
        static Net _net;
        public static int Handoffs;

        // ---------------- per frame ----------------

        public static void Tick(Net net, string scene)
        {
            _net = net;
            float now = Time.unscaledTime;
            bool online = net != null && net.Connected && net.LocalId != 255 && PlayerCharacter.instance != null;
            if (scene != _scene) { _scene = scene; ReleaseAll(); _byId.Clear(); _clock.Clear(); }
            if (!online) { if (_byId.Count > 0) { ReleaseAll(); _byId.Clear(); } return; }

            if (now >= _nextScan) { _nextScan = now + 1f; Scan(); }

            var players = PlayersInScene(net, scene);   // id → position (includes us)
            byte sceneOwner = 255; foreach (var id in players.Keys) if (id < sceneOwner) sceneOwner = id;

            foreach (var mo in _byId.Values)
            {
                if (mo.M == null) continue;
                // Owner unknown or gone from this scene → the scene owner takes it (same rule everywhere).
                if (mo.Owner == 255 || !players.ContainsKey(mo.Owner)) SetOwner(mo, sceneOwner, mo.LastHp);

                if (mo.Owner == net.LocalId) { WatchOwnerDeath(net, scene, mo); MaybeHandoff(net, scene, mo, players, now); }
                else DriveMirror(net, scene, mo, now);
            }

            if (now >= _nextStream) { _nextStream = now + StreamInterval; Stream(net, scene, players); }
        }

        static Dictionary<byte, Vector3> PlayersInScene(Net net, string scene)
        {
            var d = new Dictionary<byte, Vector3> { [net.LocalId] = PlayerCharacter.instance.transform.position };
            foreach (var p in PuppetRegistry.All())
                if (p.Latest != null && p.Latest.Scene == scene) d[p.PlayerId] = p.Latest.Pos;
            return d;
        }

        static void Scan()
        {
            try
            {
                var mm = MonsterManager.instance;
                if (mm == null || mm.monsters == null) return;
                foreach (var m in mm.monsters)
                {
                    if (m == null) continue;
                    var rs = m.runtimeStableID;
                    if (rs == null || !rs.HasValidID) continue;
                    int id = rs.ID;
                    if (_byId.TryGetValue(id, out var mo) && mo.M != null && mo.M.Pointer == m.Pointer) continue;
                    _byId[id] = new Mon { M = m, Id = id, Owner = mo != null ? mo.Owner : (byte)255, Dead = mo != null && mo.Dead };
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[mon] scan: " + e.Message); }
        }

        /// <summary>Change a monster's owner on this machine; take or give up the AI accordingly.</summary>
        static void SetOwner(Mon mo, byte owner, int hp)
        {
            if (mo.Owner == owner) return;
            mo.Owner = owner;
            if (_net != null && owner == _net.LocalId) Release(mo, hp);   // our AI runs it now
            // (becoming a mirror happens lazily in DriveMirror once the new owner's stream arrives)
        }

        // ---------------- owner side ----------------

        static void MaybeHandoff(Net net, string scene, Mon mo, Dictionary<byte, Vector3> players, float now)
        {
            if (mo.Dead || now - mo.LastHandoff < 1.5f || players.Count < 2) return;
            var mp = mo.M.transform.position;
            float mine = (players[net.LocalId] - mp).sqrMagnitude;
            byte best = net.LocalId; float bestD = mine;
            foreach (var kv in players) { float d = (kv.Value - mp).sqrMagnitude; if (d < bestD) { bestD = d; best = kv.Key; } }
            if (best == net.LocalId) return;
            if (Mathf.Sqrt(mine) - Mathf.Sqrt(bestD) < HandoffMargin) return;   // hysteresis
            int hp = 0; try { hp = mo.M.hp; } catch { }
            mo.LastHandoff = now; Handoffs++;
            Plugin.L.LogInfo($"[mon] handoff {mo.Id} → player {best} (hp {hp})");
            net.SendFlag(FlagKind.MonsterOwner, scene + "|" + mo.Id.ToString(CultureInfo.InvariantCulture), best.ToString(CultureInfo.InvariantCulture) + "|" + hp.ToString(CultureInfo.InvariantCulture));
            mo.LastHp = hp;
            mo.Owner = best;          // we're a mirror from now on (captured on first DriveMirror)
            mo.Buf.Clear(); mo.Pin = -1;
        }

        public static void ApplyOwner(string key, string value)
        {
            var k = key.Split('|'); if (k.Length != 2 || k[0] != _scene) return;
            int id = int.Parse(k[1], CultureInfo.InvariantCulture);
            var v = value.Split('|');
            byte owner = byte.Parse(v[0], CultureInfo.InvariantCulture); int hp = int.Parse(v[1], CultureInfo.InvariantCulture);
            if (!_byId.TryGetValue(id, out var mo) || mo.M == null) return;
            mo.LastHp = hp;
            Plugin.L.LogInfo($"[mon] {id} now owned by player {owner} (hp {hp})");
            mo.Buf.Clear(); mo.Pin = -1;
            SetOwner(mo, owner, hp);
        }

        static void Stream(Net net, string scene, Dictionary<byte, Vector3> players)
        {
            var mine = new List<Mon>();
            foreach (var mo in _byId.Values)
            {
                if (mo.M == null || mo.Dead || mo.Owner != net.LocalId) continue;
                var pos = mo.M.transform.position;
                foreach (var pp in players.Values) if ((pp - pos).sqrMagnitude < StreamRadius * StreamRadius) { mine.Add(mo); break; }
            }
            for (int start = 0; start < mine.Count; start += 28)       // ≤ 28 per packet: under the unreliable MTU
            {
                var w = net.Begin(Msg.Monsters);
                w.Put(net.LocalId); w.Put(Time.unscaledTime); w.Put(scene);
                int n = Math.Min(28, mine.Count - start);
                w.Put((byte)n);
                for (int i = start; i < start + n; i++)
                {
                    var m = mine[i].M; var t = m.transform; var pos = t.position;
                    w.Put(mine[i].Id); w.Put(pos.x); w.Put(pos.y); w.Put(pos.z); w.Put(t.eulerAngles.y);
                    int hash = 0; float norm = 0;
                    try { var a = m.cachedAnimator; if (a != null) { var st = a.IsInTransition(0) ? a.GetNextAnimatorStateInfo(0) : a.GetCurrentAnimatorStateInfo(0); hash = st.fullPathHash; norm = st.normalizedTime; } } catch { }
                    w.Put(hash); w.Put(norm);
                    short hp = 0; try { hp = (short)Mathf.Clamp(m.hp, 0, short.MaxValue); } catch { }
                    w.Put(hp);
                }
                net.Broadcast(w, DeliveryMethod.Unreliable);
            }
        }

        static void WatchOwnerDeath(Net net, string scene, Mon mo)
        {
            if (mo.M == null || mo.Dead) return;
            bool dead; try { dead = mo.M.IsDead; } catch { return; }
            if (!dead) return;
            mo.Dead = true;
            Plugin.L.LogInfo($"[mon] {mo.Id} died (owner)");
            net.SendFlag(FlagKind.MonsterDead, scene + "|" + mo.Id.ToString(CultureInfo.InvariantCulture), "");
        }

        /// <summary>The owner receives damage a non-owner dealt to its mirror.</summary>
        public static void ApplyHit(string key, string value, byte from)
        {
            var k = key.Split('|'); if (k.Length != 2 || k[0] != _scene || _net == null) return;
            int id = int.Parse(k[1], CultureInfo.InvariantCulture);
            int dmg = int.Parse(value, CultureInfo.InvariantCulture);
            if (!_byId.TryGetValue(id, out var mo) || mo.M == null || mo.Dead || mo.Owner != _net.LocalId) return;
            try
            {
                int before = mo.M.hp;
                mo.M.IDamageable_ReceiveDamage(dmg, 0, Vector3.zero, 0f, 0);
                Plugin.L.LogInfo($"[mon] {id} hit by player {from} for {dmg}: hp {before} -> {mo.M.hp}");
            }
            catch (Exception e) { Plugin.L.LogWarning("[mon] apply hit: " + e.Message); }
        }

        // ---------------- mirror side ----------------

        public static void OnStream(byte from, NetDataReader r)
        {
            float sentAt = r.GetFloat(); string scene = r.GetString(64);
            int n = r.GetByte();
            if (scene != _scene) return;
            float sample = Time.unscaledTime - sentAt;
            _clock.TryGetValue(from, out var c);
            if (!c.have || sample < c.offset || Mathf.Abs(sample - c.offset) > 1f) c = (sample, true);
            else c.offset += (sample - c.offset) * 0.01f;
            _clock[from] = c;
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt();
                var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                float yaw = r.GetFloat(); int hash = r.GetInt(); float norm = r.GetFloat(); short hp = r.GetShort();
                if (!_byId.TryGetValue(id, out var mo) || mo.M == null) continue;
                if (mo.Owner != from) continue;                       // only the current owner's view counts
                mo.LastHp = hp;
                mo.Buf.Add((sentAt, pos, yaw, hash, norm));
                if (mo.Buf.Count > 20) mo.Buf.RemoveAt(0);
            }
        }

        static void DriveMirror(Net net, string scene, Mon mo, float now)
        {
            var m = mo.M;
            if (m == null || mo.Dead || mo.Buf.Count == 0) return;
            if (!mo.Mirrored) Capture(mo);

            // Damage dealt locally → owner. hp is pinned to the owner's streamed hp; a drop below the pin is
            // exactly the damage this game computed. (A 9999 sentinel got clamped to maxhp=10 → read as 9989 dmg →
            // every slime died. Fixed.) A hit lethal here is lethal on the owner too, so local deaths agree.
            try
            {
                int baseline = mo.LastHp > 0 ? mo.LastHp : m.maxhp;
                int hp = m.hp;
                if (mo.Pin < 0) { m.hp = baseline; mo.Pin = m.hp; }
                else if (hp < mo.Pin)
                {
                    int dmg = mo.Pin - hp;
                    Plugin.L.LogInfo($"[mon] hit mirror {mo.Id} for {dmg} (pin {mo.Pin}) -> owner {mo.Owner}");
                    net.SendFlag(FlagKind.MonsterHit, scene + "|" + mo.Id.ToString(CultureInfo.InvariantCulture), dmg.ToString(CultureInfo.InvariantCulture));
                    if (hp > 0) { m.hp = baseline; mo.Pin = m.hp; } else mo.Pin = 0;
                }
                else if (baseline != mo.Pin && hp > 0) { m.hp = baseline; mo.Pin = m.hp; }
            }
            catch { }

            // Interpolate InterpDelay behind the owner's clock.
            _clock.TryGetValue(mo.Owner, out var c);
            float rt = now - c.offset - InterpDelay;
            while (mo.Buf.Count > 2 && mo.Buf[1].t <= rt) mo.Buf.RemoveAt(0);
            var a = mo.Buf[0]; var b = mo.Buf.Count > 1 ? mo.Buf[1] : a;
            float u = b.t > a.t ? Mathf.Clamp01((rt - a.t) / (b.t - a.t)) : 1f;
            var tr = m.transform;
            tr.position = (b.pos - a.pos).sqrMagnitude > 25f ? b.pos : Vector3.Lerp(a.pos, b.pos, u);
            tr.rotation = Quaternion.Euler(0, Mathf.LerpAngle(a.yaw, b.yaw, u), 0);
            var cur = rt >= b.t ? b : a;
            try
            {
                var anim = m.cachedAnimator;
                if (anim != null && cur.anim != 0 && cur.anim != mo.AppliedAnim)
                {
                    mo.AppliedAnim = cur.anim;
                    if (anim.GetCurrentAnimatorStateInfo(0).fullPathHash != cur.anim) anim.CrossFade(cur.anim, 0.08f, 0, cur.norm % 1f);
                }
            }
            catch { }
        }

        static void Capture(Mon mo)
        {
            var m = mo.M;
            try
            {
                mo.Mirrored = true;
                m.enabled = false;
                if (m.nma != null) m.nma.enabled = false;
                var rb = m.GetComponent<Rigidbody>(); if (rb != null) rb.isKinematic = true;
                mo.Pin = -1;
                Plugin.L.LogInfo($"[mon] mirroring {mo.Id} ({m.GetIl2CppType().Name}) owner {mo.Owner}");
            }
            catch (Exception e) { Plugin.L.LogWarning("[mon] capture: " + e.Message); }
        }

        static void Release(Mon mo, int hp)
        {
            mo.Buf.Clear(); mo.Pin = -1;
            if (!mo.Mirrored || mo.M == null) return;
            try
            {
                var m = mo.M;
                m.enabled = true;
                if (m.nma != null) { m.nma.enabled = true; m.nma.Warp(m.transform.position); }
                var rb = m.GetComponent<Rigidbody>(); if (rb != null) rb.isKinematic = false;
                if (!mo.Dead) m.hp = hp > 0 ? hp : (mo.LastHp > 0 ? mo.LastHp : m.maxhp);
                Plugin.L.LogInfo($"[mon] took over {mo.Id} (hp {m.hp})");
            }
            catch (Exception e) { Plugin.L.LogWarning("[mon] release: " + e.Message); }
            mo.Mirrored = false;
        }

        static void ReleaseAll() { foreach (var mo in _byId.Values) Release(mo, mo.LastHp); }

        public static void ApplyDead(string key)
        {
            var k = key.Split('|'); if (k.Length != 2 || k[0] != _scene || _net == null) return;
            int id = int.Parse(k[1], CultureInfo.InvariantCulture);
            if (!_byId.TryGetValue(id, out var mo) || mo.M == null || mo.Dead || mo.Owner == _net.LocalId) return;
            mo.Dead = true;
            try
            {
                var m = mo.M;
                if (m.IsDead) { Plugin.L.LogInfo($"[mon] {id} died (owner said); mirror already dead locally"); return; }
                CoinSync.SuppressNear(m.transform.position, 3f, 2.5f);   // owner's drop arrives via CoinSync
                m.enabled = true;
                m.hp = 1;
                m.IDamageable_ReceiveDamage(999, 999, Vector3.zero, 0f, 0);
                Plugin.L.LogInfo($"[mon] {id} died (owner said) -> killed mirror, dead={m.IsDead}");
            }
            catch (Exception e) { Plugin.L.LogWarning("[mon] kill mirror: " + e.Message); }
        }

        /// <summary>For tests: monsters near a point with owner and mirror state.</summary>
        public static string Describe(Vector3 from, float radius)
        {
            var parts = new List<string>();
            int mine = 0, mirrored = 0;
            foreach (var mo in _byId.Values)
            {
                if (mo.M == null) continue;
                if (_net != null && mo.Owner == _net.LocalId) mine++; else if (mo.Mirrored) mirrored++;
                var p = mo.M.transform.position;
                if ((p - from).magnitude > radius) continue;
                string hp = "?"; try { hp = mo.M.hp.ToString(); } catch { }
                string dead = "?"; try { dead = mo.M.IsDead ? "1" : "0"; } catch { }
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1}@{2:F1},{3:F1} hp={4} dead={5} own={6}{7}",
                    mo.Id, mo.M.GetIl2CppType().Name, p.x, p.z, hp, dead, mo.Owner, mo.Mirrored ? " M" : ""));
            }
            return $"me={(_net != null ? _net.LocalId : 255)} owned={mine} mirrored={mirrored} handoffs={Handoffs} | " + string.Join("; ", parts);
        }
    }
}
