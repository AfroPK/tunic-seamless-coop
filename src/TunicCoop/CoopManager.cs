using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TunicCoop
{
    public class CoopManager : MonoBehaviour
    {
        public CoopManager(IntPtr ptr) : base(ptr) { }

        public static CoopManager Instance;
        public static Net NetOrNull => Instance != null ? Instance._net : null;

        Net _net;
        internal readonly Dictionary<byte, Puppet> _puppets = new Dictionary<byte, Puppet>();
        readonly Dictionary<byte, string> _pendingSpawn = new Dictionary<byte, string>();
        float _nextSend;
        const float SendInterval = 1f / 30f;
        const float MinSendGap = 1f / 90f;   // early sends on animation-state changes, capped
        int _lastStateKey;
        float _lastSendTime;

        // F10 loopback: our own snapshots, serialized + deserialized, replayed 0.75 s late and 1.5 m to the side.
        const byte LoopbackId = 250;
        bool _loopback;
        readonly Queue<(float t, byte[] data)> _loopQueue = new Queue<(float, byte[])>();

        string _scene = "";

        void Awake()
        {
            Instance = this;
            _net = new Net();
            _net.WriteFlagDump = WorldSync.WriteDump;
            _net.OnPlayerJoined += (id, name) => { if (_net.IsHost) SendSettings(); ReannounceSoon(); _pendingSpawn[id] = name; Visibility.ForceFull = true; Toast($"{name} joined the session", CoopUI.Kind.Good); };
            _net.OnMonsters += (id, r) => MonsterSync.OnStream(id, r);
            _net.OnVis += (id, r) => { bool full = r.GetBool(); ApplyVis(id, Visibility.ReadEntries(r), full); ApplyAnims(id, Visibility.ReadAnims(r)); };
            _net.OnPlayerLeft += id => { if (_puppets.ContainsKey(id) && _puppets[id] != null) Toast($"{_puppets[id].PlayerName} left the session", CoopUI.Kind.Warn); RemovePuppet(id); };
            _net.OnState += (id, r) => ApplyState(id, Snapshot.Read(r));
            _net.OnFlag += (from, kind, key, val) =>
            {
                if (kind == FlagKind.MonsterOwner || kind == FlagKind.MonsterSpawn) Plugin.L.LogInfo($"[flag] {kind} {key}={val} from {from}");
                switch (kind)
                {
                    case FlagKind.Smash: SmashSync.Apply(key, _scene); break;
                    case FlagKind.CoinSpawn: CoinSync.ApplySpawn(key, val, _scene); break;
                    case FlagKind.CoinMove: CoinSync.ApplyMove(key, val); break;
                    case FlagKind.CoinGone: CoinSync.ApplyGone(key); break;
                    case FlagKind.CoinClaim: CoinSync.ApplyClaim(key, from); break;
                    case FlagKind.CoinTaken: CoinSync.ApplyTaken(key, val); break;
                    case FlagKind.ChestOpen: ChestSync.Apply(key, _scene); break;
                    case FlagKind.MonsterHit: MonsterSync.ApplyHit(key, val, from); break;
                    case FlagKind.MonsterDead: MonsterSync.ApplyDead(key); break;
                    case FlagKind.MonsterOwner: MonsterSync.ApplyOwner(key, val); break;
                    case FlagKind.PlayerDown: ReviveSystem.OnPlayerDown(key); break;
                    case FlagKind.PlayerUp: ReviveSystem.OnPlayerUp(key); break;
                    case FlagKind.Revive: ReviveSystem.OnRevive(key, val); break;
                    case FlagKind.MonsterSpawn: MonsterSync.ApplySpawn(key, val, from); break;
                    case FlagKind.Arrived: Regroup.OnArrived(_net, from, val, _scene); break;
                    case FlagKind.Ping: Signals.OnPing(_net, from, val, _scene); break;
                    case FlagKind.Chat: Signals.OnChat(_net, from, val); break;
                    case FlagKind.Settings: if (from == Net.HostId && !_net.IsHost) { HostSettings.Apply(val); Toast("Host rules: " + HostSettings.Describe(), CoopUI.Kind.Info, 7f); } break;
                    default: WorldSync.Apply(kind, key, val); break;
                }
            };
            _net.OnFlagDump += WorldSync.ReadDump;
            _net.OnWelcomed += () => Visibility.ForceFull = true;
            _net.OnWelcomed += () =>
            {
                Toast($"Joined {(_net.Players.TryGetValue(Net.HostId, out var hn) ? hn + "'s" : "the")} session  ·  {_net.Players.Count} player{(_net.Players.Count == 1 ? "" : "s")}", CoopUI.Kind.Good);
                if (_net.Meta.TryGetValue(Net.HostId, out var hm) && hm.game != "?" && hm.game != GameInfo.Version)
                    Toast($"The host runs TUNIC {hm.game}, you have {GameInfo.Version}. Different game versions can desync — update both if you can.", CoopUI.Kind.Warn, 9f);
            };
            _net.OnRenamed += (id, oldName, newName) =>
            {
                if (_puppets.TryGetValue(id, out var pp) && pp != null) pp.PlayerName = newName;
                if (id != _net.LocalId && !string.IsNullOrEmpty(oldName)) Toast($"{oldName} is now called {newName}");
                else if (id == _net.LocalId) Toast($"Everyone now sees you as {newName}", CoopUI.Kind.Good);
            };
            _net.OnDisconnected += why => { WorldSync.Reset(); HostSettings.LoadLocal(); Regroup.Reset(); Toast(why, CoopUI.Kind.Bad); };
            _pendingJoinCode = SteamPresence.CodeFromCommandLine();
            if (_pendingJoinCode != null) Plugin.L.LogInfo("[steam] launched to join a session; joining once the save is loaded");
            HostSettings.LoadLocal();
        }

        void Update()
        {
            try { _scene = SceneManager.GetActiveScene().name; } catch { }
            HandleKeys();
            _net.Poll();
            // Each subsystem is isolated: on a game build that differs from the one this was developed on (another
            // store or version), a missing member throws inside one subsystem only; it gets switched off with a
            // clear message instead of breaking the whole mod.
            Guard("World sync", () => WorldSync.Tick(_net));
            Guard("Pot sync", () => SmashSync.Tick(_net, _scene));
            Guard("Coin sync", () => CoinSync.Tick(_net, _scene));
            Guard("Chest sync", () => ChestSync.Tick(_net, _scene));
            Guard("Enemy sync", () => MonsterSync.Tick(_net, _scene));
            Guard("Revive", () => ReviveSystem.Tick(_net, _scene));
            Guard("Regroup", () => { if (_reannounceAt > 0 && Time.unscaledTime > _reannounceAt && PlayerCharacter.instance != null) { _reannounceAt = 0; Regroup.Announce(_net, _scene); } Regroup.Tick(_net, _scene); });
            Guard("Signals", () => Signals.Tick(_net));
            Guard("Updates", UpdateCheck.Tick);
            Guard("Presence", TickPresence);
            Guard("Relay", () => { if (_net.IsHost && _net.Active) _net.TickHostRegistration(CoopUI.HostCodes(_net).internet); });
            if (Plugin.EnableAutopilot.Value) Guard("Autopilot", () => Autopilot.Tick(this, _net, _scene));
            Guard("UI", () => CoopUI.Tick(_net, _scene));

            // Spawn queued puppets (prefab may not exist until the game has loaded a player once).
            if (_pendingSpawn.Count > 0)
            {
                foreach (var kv in new List<KeyValuePair<byte, string>>(_pendingSpawn))
                {
                    if (_puppets.ContainsKey(kv.Key)) { _pendingSpawn.Remove(kv.Key); continue; }
                    var p = Puppet.Spawn(kv.Key, kv.Value);
                    if (p != null)
                    {
                        _puppets[kv.Key] = p; _pendingSpawn.Remove(kv.Key);
                        if (_pendingVis.TryGetValue(kv.Key, out var pv)) { p.ApplyVis(pv, true); _pendingVis.Remove(kv.Key); }
                    }
                }
            }

            // Send on the 30 Hz tick, or immediately when the fox enters a new animation state (roll, attack)
            // so the start of the move isn't delayed by up to a tick.
            int key = 0;
            try { key = Snapshot.LocalStateKey(); } catch { }
            bool stateChanged = key != _lastStateKey && Time.unscaledTime - _lastSendTime >= MinSendGap;
            if (Time.unscaledTime >= _nextSend || stateChanged)
            {
                _nextSend = Time.unscaledTime + SendInterval;
                _lastSendTime = Time.unscaledTime;
                _lastStateKey = key;
                try { SendLocalState(); }
                catch (Exception e) { Plugin.L.LogWarning("[sync] send failed: " + e.Message); }
            }

            while (_loopQueue.Count > 0 && Time.unscaledTime - _loopQueue.Peek().t >= 0.75f)
            {
                var r = new NetDataReader(_loopQueue.Dequeue().data);
                var s = Snapshot.Read(r);
                s.Pos += new Vector3(1.5f, 0, 0); // fixed world offset (yaw-relative offset swung the ghost around)
                ApplyState(LoopbackId, s);
            }
        }

        void SendLocalState()
        {
            bool net = _net.Connected && _net.LocalId != 255;
            if (!net && !_loopback) return;
            Snapshot s;
            try { s = Snapshot.CaptureLocal(_scene); }
            catch (Exception e) { Plugin.L.LogWarning("[sync] capture failed: " + e.Message); return; }
            if (s == null) return;

            if (net)
            {
                var w = _net.Begin(Msg.State);
                w.Put(_net.LocalId);
                s.Write(w);
                _net.Broadcast(w, DeliveryMethod.Sequenced);
            }
            if (_loopback)
            {
                var w = new NetDataWriter();
                s.Write(w);
                _loopQueue.Enqueue((Time.unscaledTime, w.CopyData()));
            }

            // Visibility: only what changed (full table after a join), reliable so it is sent once.
            var vis = Visibility.LocalDelta(PlayerCharacter.instance, out bool full);
            var anims = Visibility.LocalAnimDelta(full);
            if (vis == null && anims == null) return;
            vis ??= new List<KeyValuePair<int, byte>>();
            if (net)
            {
                var w = _net.Begin(Msg.Vis);
                w.Put(_net.LocalId); w.Put(full); Visibility.WriteEntries(w, vis); Visibility.WriteAnims(w, anims);
                _net.Broadcast(w, DeliveryMethod.ReliableOrdered);
            }
            if (_loopback) { ApplyVis(LoopbackId, vis, full); if (anims != null) ApplyAnims(LoopbackId, anims); }
        }

        void ApplyAnims(byte id, List<Visibility.AnimEntry> a)
        {
            if (a.Count > 0 && _puppets.TryGetValue(id, out var p) && p != null) p.ApplyAnims(a);
        }

        // Visibility that arrives before the puppet exists is merged and applied at spawn.
        readonly Dictionary<byte, List<KeyValuePair<int, byte>>> _pendingVis = new Dictionary<byte, List<KeyValuePair<int, byte>>>();

        void ApplyVis(byte id, List<KeyValuePair<int, byte>> e, bool full)
        {
            if (_puppets.TryGetValue(id, out var p) && p != null) { p.ApplyVis(e, full); return; }
            if (!_pendingVis.TryGetValue(id, out var acc) || full) _pendingVis[id] = acc = new List<KeyValuePair<int, byte>>();
            acc.AddRange(e);
        }

        void ApplyState(byte id, Snapshot s)
        {
            if (!_puppets.TryGetValue(id, out var p) || p == null)
            {
                if (!_pendingSpawn.ContainsKey(id))
                    _pendingSpawn[id] = _net.Players.TryGetValue(id, out var n) ? n : (id == LoopbackId ? "Echo" : $"Player{id}");
                return;
            }
            p.Apply(s, _scene);
        }

        void RemovePuppet(byte id)
        {
            _pendingSpawn.Remove(id);
            _pendingVis.Remove(id);
            if (_puppets.TryGetValue(id, out var p))
            {
                if (p != null) Destroy(p.gameObject);
                _puppets.Remove(id);
                Plugin.L.LogInfo($"[puppet] removed player {id}");
            }
        }

        float _reannounceAt;
        void ReannounceSoon() { if (_net.Active && _net.LocalId != 255) _reannounceAt = Time.unscaledTime + 1f; }

        string _pendingJoinCode;
        float _nextPresence;

        /// <summary>Steam "Join Game" presence, and the auto-join when Steam launched us with a join code.</summary>
        void TickPresence()
        {
            if (_pendingJoinCode != null && PlayerCharacter.instance != null && _scene != "TitleScreen" && !_net.Active)
            {
                var code = _pendingJoinCode; _pendingJoinCode = null;
                Toast("Joining your Steam friend's session…", CoopUI.Kind.Info);
                StartJoin(code);
            }
            if (Time.unscaledTime < _nextPresence) return;
            _nextPresence = Time.unscaledTime + 3f;
            if (!_net.Active || _net.LocalId == 255) { SteamPresence.Update(null, null, 0); return; }
            string code2 = null;
            if (_net.IsHost) { var c = CoopUI.HostCodes(_net); code2 = c.forwarded ? c.internet : null; }
            else if (System.Net.IPAddress.TryParse(_net.LastAddress, out var ip)) code2 = JoinCode.Encode(ip, _net.LastPort);
            SteamPresence.Update($"TUNIC co-op  ·  {_net.Players.Count}/{_net.MaxPlayers}", code2, _net.Players.Count);
        }

        void HandleKeys()
        {
            if (Pressed(KeyCode.F3)) Signals.Key(KeyCode.F3, _net);
            if (Pressed(KeyCode.F4)) Signals.Key(KeyCode.F4, _net);
            if (Signals.ChatOpen)
            {
                for (var k = KeyCode.Alpha1; k <= KeyCode.Alpha6; k++) if (Pressed(k)) Signals.Key(k, _net);
                for (var k = KeyCode.Keypad1; k <= KeyCode.Keypad6; k++) if (Pressed(k)) Signals.Key(k, _net);
                if (Pressed(KeyCode.Escape)) Signals.Key(KeyCode.Escape, _net);
            }
            if (Pressed(KeyCode.F5)) Regroup.QuickGo(_net);
            if (Pressed(KeyCode.F7)) StartHost();
            if (Pressed(KeyCode.F8)) StartJoin(null);
            if (Pressed(KeyCode.F9)) Leave();
            if (Pressed(KeyCode.F11)) { Diagnostics.DumpMonsters(_scene); Toast("Enemy list written to log"); }
            if (Pressed(KeyCode.F10))
            {
                _loopback = !_loopback;
                if (!_loopback) { RemovePuppet(LoopbackId); _loopQueue.Clear(); }
                Toast("Loopback ghost " + (_loopback ? "ON" : "OFF"));
            }
        }

        static void NameTip()
        {
            if (!Plugin.NameChosen.Value)
                Toast($"Other players see you as \"{Plugin.PlayerName.Value}\". Press F1 to choose your name.", CoopUI.Kind.Info, 8f);
        }

        public void StartHost()
        {
            NameTip();
            if (_net.Active && _net.IsHost) { Toast($"Already hosting ({_net.Players.Count}/{Plugin.MaxPlayers.Value}). F9 to stop."); return; }
            try
            {
                HostSettings.LoadLocal();
                _net.Host(Plugin.Port.Value, Plugin.MaxPlayers.Value, Plugin.Password.Value, Plugin.PlayerName.Value);
                WorldSync.Reset(); Regroup.Reset();
                PortMapper.Start(Plugin.Port.Value);     // internet friends without router fiddling (UPnP)
                PublicAddress.Ask();
                var addrs = GameInfo.LocalAddresses();
                Toast(Plugin.StreamerMode.Value
                    ? $"Hosting  ·  up to {Plugin.MaxPlayers.Value} players  ·  join code hidden (streamer mode)"
                    : $"Hosting  ·  friends join with your code (top left) or {(addrs.Count > 0 ? addrs[0] : "your IP")}:{Plugin.Port.Value}  ·  up to {Plugin.MaxPlayers.Value} players", CoopUI.Kind.Good, 8f);
            }
            catch (Exception e) { Toast("Couldn't host: " + e.Message + " (is another program using that port?)", CoopUI.Kind.Bad); }
        }

        public void StartJoin(string address)
        {
            NameTip();
            if (_net.Active && _net.IsHost) { Toast("You are hosting. Press F9 first to join someone else."); return; }
            address = string.IsNullOrEmpty(address) ? Plugin.JoinAddress.Value : address;
            int port = Plugin.Port.Value;
            string code = null;
            if (JoinCode.LooksLikeCode(address) && JoinCode.TryDecode(address, out var codeAddr, out var codePort)) { code = address.Trim().ToUpperInvariant(); address = codeAddr; port = codePort; }
            WorldSync.Reset(); Regroup.Reset();
            _net.Join(address, port, Plugin.Password.Value, Plugin.PlayerName.Value, code);
            Toast($"Connecting to {GameInfo.Show(address + ":" + port)}…");
        }

        public void Leave() { bool host = _net.IsHost && _net.Active; _net.Stop("left"); if (host) PortMapper.Stop(); WorldSync.Reset(); HostSettings.LoadLocal(); Regroup.Reset(); Toast("You left the session"); }

        /// <summary>Host: (re)announce the session rules, e.g. after a join or a change in the F1 menu.</summary>
        public void SendSettings()
        {
            HostSettings.LoadLocal();
            if (_net.Active && _net.IsHost) _net.SendFlag(FlagKind.Settings, "rules", HostSettings.Serialize());
        }

        // Read by PuppetRegistry. NOTE: don't add iterator / Nullable<T> methods to this injected class: BepInEx
        // pre.1's ClassInjector crashed the game natively while registering CoopManager with them.

        static bool _legacyInputBroken;
        static bool Pressed(KeyCode k)
        {
            if (_legacyInputBroken) return false;
            try { return Input.GetKeyDown(k); }
            catch (Exception e)
            {
                _legacyInputBroken = true;
                Plugin.L.LogError("[input] legacy Input unavailable: " + e.Message);
                return false;
            }
        }

        static readonly Dictionary<string, int> _failures = new Dictionary<string, int>();
        static readonly HashSet<string> _disabled = new HashSet<string>();
        static void Guard(string name, Action a)
        {
            if (_disabled.Contains(name)) return;
            try { a(); _failures[name] = 0; }
            catch (Exception e)
            {
                _failures.TryGetValue(name, out int n); _failures[name] = ++n;
                if (n == 1) Plugin.L.LogWarning($"[coop] {name} error: {e}");
                if (n >= 30)
                {
                    _disabled.Add(name);
                    Plugin.L.LogError($"[coop] {name} disabled after repeated errors (game build differs?). Last: {e.Message}");
                    Toast($"{name} was turned off after errors. Co-op still works without it; please report your game version ({GameInfo.Version}, {GameInfo.Store}).", CoopUI.Kind.Warn, 12f);
                }
            }
        }

        static void Toast(string msg, CoopUI.Kind kind = CoopUI.Kind.Info, float life = 5f) => CoopUI.Toast(msg, kind, life);
        void OnDestroy() { _net?.Stop("shutdown"); PortMapper.Stop(); SteamPresence.Update(null, null, 0); }
    }
}
