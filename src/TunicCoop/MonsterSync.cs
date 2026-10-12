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
            public bool Boss; public bool Dynamic; public bool Native; public float AnnouncedAt = -100f; public float Scale = 1f;
            public float ClaimedAt = -100f; public float Carry;
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
            if (scene != _scene) { _scene = scene; ReleaseAll(); _byId.Clear(); _clock.Clear(); ClearDynamic(); _sceneStart = now; _pendingOwner.Clear(); _wakeTried.Clear(); _scaled.Clear(); _byPtr.Clear(); }
            if (!online) { if (_byId.Count > 0) { ReleaseAll(); _byId.Clear(); ClearDynamic(); } return; }

            // Scripted spawns (boss adds, gauntlet waves) appear any frame: look for them often.
            if (now >= _nextScan) { _nextScan = now + 0.25f; Scan(net); }
            WatchLocalAttacks(now);

            var players = PlayersInScene(net, scene);   // id → position (includes us)
            byte sceneOwner = 255; foreach (var id in players.Keys) if (id < sceneOwner) sceneOwner = id;
            ProcessDynamic(net, scene, now, players);

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

        static void Scan(Net net)
        {
            try
            {
                var mm = MonsterManager.instance;
                if (mm == null || mm.monsters == null) return;
                foreach (var m in mm.monsters)
                {
                    if (m == null) continue;
                    var rs = m.runtimeStableID;
                    if (rs == null || !rs.HasValidID)
                    {
                        // No stable id: spawned at runtime. Same objects on every machine can't be matched by id,
                        // so the authority names them and everyone else pairs/creates copies (see ProcessDynamic).
                        var ptr = m.Pointer;
                        if (!_assigned.Contains(ptr) && !_dyn.ContainsKey(ptr)) _dyn[ptr] = new Dyn { M = m, Seen = Time.unscaledTime };
                        continue;
                    }
                    int id = rs.ID;
                    if (_byId.TryGetValue(id, out var mo) && mo.M != null && mo.M.Pointer == m.Pointer) continue;
                    var nm = Register(net, new Mon { M = m, Id = id, Owner = mo != null ? mo.Owner : (byte)255, Dead = mo != null && mo.Dead });
                    _byId[id] = nm;
                    if (_pendingOwner.TryGetValue(id, out var po)) { _pendingOwner.Remove(id); nm.Owner = 255; SetOwner(nm, po.owner, po.hp); }
                    else if (mo == null && Time.unscaledTime - _sceneStart > LateWake) ClaimWoken(net, nm);
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[mon] scan: " + e.Message); }
        }

        // ---------------- monsters that wake up mid-scene (ambushes, gauntlet waves) ----------------
        //
        // Pre-placed monsters can sit inactive until a trigger (an altar, an ambush) wakes them, and that happens
        // in ONE game only. The machine whose game woke one claims it (it is running it) and tells everyone; the
        // others wake their own inactive copy and mirror it.

        const float LateWake = 3f;
        static float _sceneStart;
        static readonly Dictionary<int, (byte owner, int hp)> _pendingOwner = new Dictionary<int, (byte, int)>();
        static readonly HashSet<int> _wakeTried = new HashSet<int>();

        static void ClaimWoken(Net net, Mon mo)
        {
            int hp = 0; try { hp = mo.M.hp; } catch { }
            mo.ClaimedAt = Time.unscaledTime;
            SetOwner(mo, net.LocalId, hp);
            Plugin.L.LogInfo($"[mon] {mo.Id} woke up here: claiming it");
            net.SendFlag(FlagKind.MonsterOwner, _scene + "|" + mo.Id.ToString(CultureInfo.InvariantCulture),
                net.LocalId.ToString(CultureInfo.InvariantCulture) + "|" + hp.ToString(CultureInfo.InvariantCulture) + "|wake");
        }

        /// <summary>Activate our inactive copy of a scene monster that woke up in someone else's game.</summary>
        static bool WakeLocal(int id)
        {
            int inScene = 0, inactive = 0, noId = 0;
            var sample = new List<string>();
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(UnhollowerRuntimeLib.Il2CppType.Of<Monster>()))
                {
                    var m = o.TryCast<Monster>();
                    if (m == null || m.gameObject.scene.name != _scene) continue;   // prefabs have no scene
                    inScene++;
                    if (!m.gameObject.activeInHierarchy) inactive++;
                    // An object that never became active never ran Awake: the monster's cached reference is still
                    // empty and the id may not be computed yet. Read the component and derive the id.
                    var rs = m.runtimeStableID ?? m.GetComponent<RuntimeStableID>();
                    int mid = rs == null ? int.MinValue : rs.HasValidID ? rs.ID : DerivedId(rs);
                    if (rs != null && !rs.HasValidID) noId++;
                    if (sample.Count < 4 && !m.gameObject.activeInHierarchy) sample.Add($"{m.gameObject.name}:{mid}");
                    if (mid != id) continue;
                    Activate(m);
                    return true;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[mon] wake: " + e.Message); }
            Plugin.L.LogInfo($"[mon] wake {id}: no copy found ({inScene} in scene, {inactive} inactive, {noId} without id; e.g. {string.Join(", ", sample)})");
            return false;
        }

        static int DerivedId(RuntimeStableID rs)
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex * 1000000 + rs.intraSceneID; }
            catch { return int.MinValue; }
        }

        static void Activate(Monster m)
        {
            var t = m.transform;
            for (int guard = 0; t != null && !m.gameObject.activeInHierarchy && guard < 10; guard++, t = t.parent)
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            Plugin.L.LogInfo($"[mon] woke our copy of {m.gameObject.name} active={m.gameObject.activeInHierarchy}");
        }

        // ---------------- who hit it? ----------------

        /// <summary>Last time the local player used an attack/item button (sword, wand, bombs...). Only damage
        /// to mirrors shortly after one counts as ours.</summary>
        public static float LastLocalAttack = -100f;
        const float AttackWindow = 3f;
        static List<InControl.PlayerAction> _attackActions;

        static void WatchLocalAttacks(float now)
        {
            try
            {
                if (_attackActions == null)
                {
                    var set = PlayerInput.Actions; if (set == null) return;
                    _attackActions = new List<InControl.PlayerAction>();
                    foreach (var a in set.actions)
                        if (a != null && (a.Name == "Item1" || a.Name == "Item2" || a.Name == "Item3")) _attackActions.Add(a);
                }
                foreach (var a in _attackActions) if (a.IsPressed) { LastLocalAttack = now; break; }
            }
            catch { _attackActions = null; }
        }

        // ---------------- bosses ----------------

        static readonly HashSet<string> BossTypes = new HashSet<string>
            { "Spidertank", "Librarian", "ScavengerBoss", "Knightbot", "Foxgod", "CryptBoss", "TunicKnightVoid" };

        /// <summary>First sight of a monster on this machine: classify it and apply the session's health scaling.</summary>
        static Mon Register(Net net, Mon mo)
        {
            try
            {
                var m = mo.M;
                _byPtr[m.Pointer] = mo;
                string type = m.GetIl2CppType().Name;
                int maxhp = 0; try { maxhp = m.maxhp; } catch { }
                mo.Boss = BossTypes.Contains(type) || maxhp >= 300 || m.GetComponentInParent<BossAnnounceOnAggro>() != null
                          || HasAnnouncerFor(m);
                ApplyScaling(net, mo);
                if (mo.Boss) Plugin.L.LogInfo($"[mon] boss {mo.Id} {type} hp {maxhp} scale x{mo.Scale:0.##}");
            }
            catch (Exception e) { Plugin.L.LogWarning("[mon] register: " + e.Message); }
            return mo;
        }

        static bool HasAnnouncerFor(Monster m)
        {
            try
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(UnhollowerRuntimeLib.Il2CppType.Of<BossAnnounceOnAggro>()))
                {
                    var b = o.TryCast<BossAnnounceOnAggro>();
                    if (b != null && b.monster != null && b.monster.Pointer == m.Pointer) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Optional difficulty: enemies take longer to kill with more players. Every hit on a scaled
        /// monster is divided by its factor in the damage hook (ReviveSystem), identically on every machine.</summary>
        static void ApplyScaling(Net net, Mon mo)
        {
            int pct = HostSettings.EnemyHealthPerExtraPlayer;
            int extra = net != null ? Math.Max(0, net.Players.Count - 1) : 0;
            float f = 1f + pct / 100f * extra;
            if (f <= 1.001f) return;
            mo.Scale = f;
            _scaled[mo.M.Pointer] = mo;
        }

        static readonly Dictionary<IntPtr, Mon> _scaled = new Dictionary<IntPtr, Mon>();
        static readonly Dictionary<IntPtr, Mon> _byPtr = new Dictionary<IntPtr, Mon>();

        /// <summary>
        /// Called from the creature damage hook for every hit on a monster. A MIRROR never decides its own fate:
        /// our player's hit is forwarded to the owner right here (full damage) and the mirror keeps at least 1 hp
        /// until the owner says it died. Otherwise a hit the owner's copy absorbs (spawn/i-frames, a block) would
        /// leave the monster dead here and alive there. Damage our player didn't cause is the owner's business.
        /// Returns the damage to apply locally; handled=false means "not a mirror, apply normally".
        /// </summary>
        public static int MirrorHit(IntPtr creature, int dmg, out bool handled)
        {
            handled = false;
            if (ApplyingFinal || _net == null || !_byPtr.TryGetValue(creature, out var mo) || !mo.Mirrored || mo.Dead || mo.M == null) return dmg;
            handled = true;
            if (dmg <= 0) return 0;
            if (Time.unscaledTime - LastLocalAttack > AttackWindow) return 0;
            _net.SendFlag(FlagKind.MonsterHit, _scene + "|" + mo.Id.ToString(CultureInfo.InvariantCulture), dmg.ToString(CultureInfo.InvariantCulture));
            Plugin.L.LogInfo($"[mon] hit mirror {mo.Id} for {dmg} -> owner {mo.Owner}");
            int hp = 1; try { hp = mo.M.hp; } catch { }
            return Math.Min(dmg, Math.Max(0, hp - 1));
        }

        /// <summary>After a forwarded mirror hit: the local hp drop is already accounted for.</summary>
        public static void AfterMirrorHit(IntPtr creature)
        {
            if (!_byPtr.TryGetValue(creature, out var mo) || mo.M == null) return;
            try { if (mo.M.hp < 1) mo.M.hp = 1; mo.Pin = mo.M.hp; } catch { }
        }
        /// <summary>True while we apply a hit that is already final (forwarded from a mirror, or a death).</summary>
        public static bool ApplyingFinal;

        /// <summary>Called from the creature damage hook: the damage this monster should really take. Fractions
        /// carry over so small hits still add up (two 1-damage hits at x2 = 1 damage).</summary>
        public static int ScaleDamage(IntPtr creature, int dmg)
        {
            if (ApplyingFinal || dmg <= 0 || _scaled.Count == 0 || !_scaled.TryGetValue(creature, out var mo)) return dmg;
            float total = dmg / mo.Scale + mo.Carry;
            int whole = (int)total;
            mo.Carry = total - whole;
            return whole;
        }

        /// <summary>A boss whose fight has begun stays with its owner: its phase/attack state lives in that game
        /// only, so moving it mid-fight would reset the fight.</summary>
        static bool Engaged(Mon mo)
        {
            try { return mo.M.hp < mo.M.maxhp || mo.M.IsAggroed; } catch { return true; }
        }

        // ---------------- runtime spawns (boss adds, gauntlet waves, ambushes...) ----------------
        //
        // Monsters spawned at runtime have no stable id. Whoever's game spawns one announces it under a fresh id
        // (owner = them) and everyone else pairs it with their own copy (if their game spawned the same thing) or
        // makes a copy. Rules:
        //  - While a boss owned by someone else is fighting here, our local spawns are replays of ITS attacks:
        //    never announce them; pair them with the owner's announcements, hide the rest.
        //  - Otherwise announce our spawn after a short delay, unless an identical remote one already exists nearby
        //    (a teammate triggered the same ambush first): then ours is a duplicate and gets hidden.
        //  - Two machines announcing the same spawn at once: the lower player id wins, the other retracts its id.

        class Dyn { public Monster M; public float Seen; public bool Suppressed; }
        static readonly Dictionary<IntPtr, Dyn> _dyn = new Dictionary<IntPtr, Dyn>();
        static readonly HashSet<IntPtr> _assigned = new HashSet<IntPtr>();
        static int _dynCounter;
        const float PairGrace = 2f, AnnounceDelay = 0.6f, DuplicateRadius = 12f, ConflictWindow = 3f;

        static void ClearDynamic() { _dyn.Clear(); _assigned.Clear(); }

        static string BaseName(Monster m)
        {
            string n = m.gameObject.name;
            while (n.EndsWith("(Clone)")) n = n.Substring(0, n.Length - 7).TrimEnd();
            return n;
        }

        static string Vec3(Vector3 p) => string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}", p.x, p.y, p.z);

        /// <summary>Is a boss that someone else simulates fighting in this scene?</summary>
        static bool MirroredBossActive(Net net, Dictionary<byte, Vector3> players)
        {
            foreach (var mo in _byId.Values)
                if (mo.Boss && !mo.Dead && mo.M != null && mo.Owner != net.LocalId && players.ContainsKey(mo.Owner)) return true;
            return false;
        }

        static Mon RemoteCopyNear(Net net, string name, Vector3 pos, float radius)
        {
            foreach (var mo in _byId.Values)
            {
                if (!mo.Dynamic || mo.Dead || mo.M == null || mo.Owner == net.LocalId) continue;
                if (BaseName(mo.M) == name && (mo.M.transform.position - pos).sqrMagnitude < radius * radius) return mo;
            }
            return null;
        }

        static void ProcessDynamic(Net net, string scene, float now, Dictionary<byte, Vector3> players)
        {
            if (_dyn.Count == 0) return;
            bool underMirroredBoss = MirroredBossActive(net, players);
            List<IntPtr> drop = null;
            foreach (var kv in _dyn)
            {
                var d = kv.Value;
                if (d.M == null) { (drop ??= new List<IntPtr>()).Add(kv.Key); continue; }
                if (d.Suppressed) continue;
                bool dead = false; try { dead = d.M.IsDead; } catch { }
                if (dead) continue;
                float age = now - d.Seen;
                string name = BaseName(d.M);
                if (underMirroredBoss || RemoteCopyNear(net, name, d.M.transform.position, DuplicateRadius) != null)
                {
                    if (age > PairGrace || !underMirroredBoss) Hide(d, underMirroredBoss ? "unmatched replay" : "duplicate of a teammate's");
                    continue;
                }
                if (age < AnnounceDelay) continue;
                int id = -(net.LocalId * 1000000 + (++_dynCounter % 999999) + 1);
                var mo = Register(net, new Mon { M = d.M, Id = id, Owner = net.LocalId, Dynamic = true, Native = true, AnnouncedAt = now });
                _byId[id] = mo; _assigned.Add(kv.Key); (drop ??= new List<IntPtr>()).Add(kv.Key);
                var p = d.M.transform.position;
                Plugin.L.LogInfo($"[mon] spawn {id} {name} at ({p.x:F1},{p.y:F1},{p.z:F1})");
                net.SendFlag(FlagKind.MonsterSpawn, scene + "|" + id.ToString(CultureInfo.InvariantCulture), name.Replace("|", "/") + "|" + Vec3(p));
            }
            if (drop != null) foreach (var k in drop) _dyn.Remove(k);
        }

        static void Hide(Dyn d, string why)
        {
            d.Suppressed = true;
            try { d.M.gameObject.SetActive(false); } catch { }
            Plugin.L.LogInfo($"[mon] hid local spawn {BaseName(d.M)} ({why})");
        }

        /// <summary>Someone announced a runtime spawn (or retracted one: value "-").</summary>
        public static void ApplySpawn(string key, string value, byte from)
        {
            var k = key.Split('|'); if (k.Length != 2 || k[0] != _scene || _net == null) return;
            int id = int.Parse(k[1], CultureInfo.InvariantCulture);
            if (value == "-") { Retracted(id); return; }
            if (_byId.ContainsKey(id)) return;
            var v = value.Split('|'); if (v.Length < 4) return;
            string name = v[0];
            var pos = new Vector3(float.Parse(v[1], CultureInfo.InvariantCulture), float.Parse(v[2], CultureInfo.InvariantCulture), float.Parse(v[3], CultureInfo.InvariantCulture));
            try
            {
                // Both games spawned it and both announced: lower player id keeps its id.
                float now = Time.unscaledTime;
                foreach (var mine in new List<Mon>(_byId.Values))
                {
                    if (!mine.Dynamic || mine.Owner != _net.LocalId || mine.M == null || now - mine.AnnouncedAt > ConflictWindow) continue;
                    if (BaseName(mine.M) != name || (mine.M.transform.position - pos).sqrMagnitude > DuplicateRadius * DuplicateRadius) continue;
                    if (from > _net.LocalId) return;                   // we win; they will retract theirs
                    _byId.Remove(mine.Id);
                    _net.SendFlag(FlagKind.MonsterSpawn, _scene + "|" + mine.Id.ToString(CultureInfo.InvariantCulture), "-");
                    Plugin.L.LogInfo($"[mon] spawn conflict: our {mine.Id} becomes player {from}'s {id}");
                    mine.Id = id; mine.Owner = from; mine.AnnouncedAt = -100f;
                    _byId[id] = mine;
                    Capture(mine);
                    return;
                }

                // Our game spawned the same thing (e.g. a mirror boss replayed the attack): nearest same-named one.
                Dyn best = null; IntPtr bestPtr = IntPtr.Zero; float bestD = 40f * 40f;
                foreach (var kv in _dyn)
                {
                    var d = kv.Value; if (d.M == null || BaseName(d.M) != name) continue;
                    float dist = (d.M.transform.position - pos).sqrMagnitude;
                    if (dist < bestD) { bestD = dist; best = d; bestPtr = kv.Key; }
                }
                Monster m; bool native;
                if (best != null)
                {
                    _dyn.Remove(bestPtr); m = best.M; native = true;
                    if (best.Suppressed) m.gameObject.SetActive(true);
                    Plugin.L.LogInfo($"[mon] paired spawn {id} {name} with our copy");
                }
                else
                {
                    var tpl = FindTemplate(name);
                    if (tpl == null) { Plugin.L.LogWarning($"[mon] no template for spawn '{name}'"); return; }
                    var go = UnityEngine.Object.Instantiate(tpl.gameObject, pos, tpl.transform.rotation);
                    go.name = name + "(Clone)";
                    if (!go.activeSelf) go.SetActive(true);
                    m = go.GetComponent<Monster>(); native = false;
                    if (m == null) { UnityEngine.Object.Destroy(go); return; }
                    Plugin.L.LogInfo($"[mon] created spawn {id} {name} for player {from}");
                }
                _assigned.Add(m.Pointer);
                var mo = Register(_net, new Mon { M = m, Id = id, Owner = from, Dynamic = true, Native = native });
                _byId[id] = mo;
                Capture(mo);
            }
            catch (Exception e) { Plugin.L.LogWarning("[mon] apply spawn: " + e.Message); }
        }

        static void Retracted(int id)
        {
            if (!_byId.TryGetValue(id, out var mo)) return;
            _byId.Remove(id);
            try
            {
                if (mo.M != null)
                {
                    if (mo.Native) mo.M.gameObject.SetActive(false);    // our game's own object: just hide it
                    else UnityEngine.Object.Destroy(mo.M.gameObject);
                }
            }
            catch { }
            Plugin.L.LogInfo($"[mon] spawn {id} retracted");
        }

        static Monster FindTemplate(string name)
        {
            foreach (var mo in _byId.Values) if (mo.M != null && BaseName(mo.M) == name) return mo.M;
            foreach (var d in _dyn.Values) if (d.M != null && BaseName(d.M) == name) return d.M;
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(UnhollowerRuntimeLib.Il2CppType.Of<Monster>()))
                {
                    var m = o.TryCast<Monster>();
                    if (m != null && m.gameObject.name == name) return m;
                }
            }
            catch { }
            return null;
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
            if (mo.Boss && Engaged(mo)) return;   // sticky once the fight is on
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
            var k = key.Split('|'); if (k.Length != 2 || k[0] != _scene) { Plugin.L.LogInfo($"[mon] owner msg {key}={value} ignored (we are in '{_scene}')"); return; }
            int id = int.Parse(k[1], CultureInfo.InvariantCulture);
            var v = value.Split('|');
            byte owner = byte.Parse(v[0], CultureInfo.InvariantCulture); int hp = int.Parse(v[1], CultureInfo.InvariantCulture);
            bool wake = v.Length > 2 && v[2] == "wake";
            if (!_byId.TryGetValue(id, out var mo) || mo.M == null)
            {
                // Not active here (yet): it woke up in their game. Wake ours; it gets this owner when it registers.
                if (id > 0) { _pendingOwner[id] = (owner, hp); if (_wakeTried.Add(id)) WakeLocal(id); }
                return;
            }
            if (wake && _net != null && mo.Owner == _net.LocalId && Time.unscaledTime - mo.ClaimedAt < 3f && owner > _net.LocalId)
                return;   // both games woke it at once: the lower player id keeps it
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
                // dmg is final: the hitter's machine already applied the session's scaling.
                ApplyingFinal = true;
                try { mo.M.IDamageable_ReceiveDamage(dmg, 0, Vector3.zero, 0f, 0); }
                finally { ApplyingFinal = false; }
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
                if (!_byId.TryGetValue(id, out var mo) || mo.M == null)
                {
                    // A teammate is running a monster that is still asleep here (it woke before we arrived).
                    if (id > 0 && _wakeTried.Add(id)) { _pendingOwner[id] = (from, hp); WakeLocal(id); }
                    continue;
                }
                if (mo.Owner != from) continue;                      // only the current owner's view counts
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
                else if (hp < mo.Pin && now - LastLocalAttack > AttackWindow)
                {
                    // Damage our player didn't cause (the mirror's own replayed explosions, other mirrors,
                    // hazards): the owner simulates all of that already, forwarding it would count it twice.
                    m.hp = baseline; mo.Pin = m.hp;
                }
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
                ApplyingFinal = true;
                try { m.IDamageable_ReceiveDamage(999, 999, Vector3.zero, 0f, 0); } finally { ApplyingFinal = false; }
                // Our own hit a moment ago may have left it in its post-hit invulnerability: then the killing blow
                // bounces off. The owner says it's dead, so run the game's death directly.
                if (!m.IsDead) { m.hp = 0; m.Die(); }
                Plugin.L.LogInfo($"[mon] {id} died (owner said) -> killed mirror, dead={m.IsDead}");
            }
            catch (Exception e) { Plugin.L.LogWarning("[mon] kill mirror: " + e.Message); }
        }

        /// <summary>For tests: the monster this machine knows under a sync id (stable or runtime-spawn id).</summary>
        public static Monster Find(int id) => _byId.TryGetValue(id, out var mo) ? mo.M : null;

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
                parts.Add(string.Format(CultureInfo.InvariantCulture, "{0}:{1}@{2:F1},{3:F1} hp={4} dead={5} own={6}{7}{8}{9}",
                    mo.Id, mo.M.GetIl2CppType().Name, p.x, p.z, hp, dead, mo.Owner, mo.Mirrored ? " M" : "", mo.Boss ? " BOSS" : "", mo.Dynamic ? " DYN" : ""));
            }
            int hidden = 0, pending = 0; foreach (var d in _dyn.Values) { if (d.Suppressed) hidden++; else pending++; }
            return $"me={(_net != null ? _net.LocalId : 255)} owned={mine} mirrored={mirrored} handoffs={Handoffs} dynPending={pending} dynHidden={hidden} | " + string.Join("; ", parts);
        }
    }
}
