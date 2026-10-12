using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace TunicCoop
{
    public enum Msg : byte
    {
        Hello = 1,      // C->H  name
        Welcome = 2,    // H->C  yourId, maxPlayers, [id,name]*
        Joined = 3,     // H->*  id, name
        Left = 4,       // H->*  id
        State = 5,      // *     PlayerSnapshot (unreliable sequenced)
        Flag = 6,       // *     origin, kind, key, value (reliable ordered)
        FlagDump = 7,   // H->C  full world KV store
        Reject = 8,     // H->C  reason
        Vis = 9,
        Rename = 11,    // C->H new name; H->* id, name
        Monsters = 10,  // *     owner's enemy stream (unreliable, chunked)        // *     id, full, visibility entries (reliable ordered, fragmentable)
    }

    public enum FlagKind : byte { Int = 0, Float = 1, String = 2, ItemDelta = 3, ItemSet = 4, Smash = 5, CoinSpawn = 6, CoinMove = 7, CoinGone = 8, CoinClaim = 9, CoinTaken = 10, ChestOpen = 11, MonsterHit = 12, MonsterDead = 13, MonsterOwner = 14, PlayerDown = 15, PlayerUp = 16, Revive = 17, MonsterSpawn = 18, Settings = 19, Ping = 20, Chat = 21, Arrived = 22 }

    /// <summary>
    /// LiteNetLib session. Star topology: the host (player 0) relays every client message to the others.
    /// All callbacks fire on the Unity main thread because Poll() is called from CoopManager.Update.
    /// </summary>
    public class Net
    {
        public const byte HostId = 0;

        public bool IsHost { get; private set; }
        public bool Active => _mgr != null && _mgr.IsRunning;
        public bool Connected => IsHost || (_server != null && _server.ConnectionState == ConnectionState.Connected);
        public byte LocalId { get; private set; } = HostId;
        public readonly Dictionary<byte, string> Players = new Dictionary<byte, string>();
        /// <summary>Per player: game version and store (for the UI; mismatched game builds get a warning).</summary>
        public readonly Dictionary<byte, (string game, string store)> Meta = new Dictionary<byte, (string, string)>();
        public string LastAddress { get; private set; }
        public int LastPort { get; private set; }
        public int MaxPlayers => _max;

        public event Action<byte, string> OnPlayerJoined;
        public event Action<byte> OnPlayerLeft;
        public event Action<byte, NetDataReader> OnState;
        public event Action<byte, NetDataReader> OnVis;
        public event Action<byte, NetDataReader> OnMonsters;
        public event Action<byte, FlagKind, string, string> OnFlag;
        public event Action<NetDataReader> OnFlagDump;
        public event Action OnWelcomed;
        public event Action<string> OnDisconnected;
        public event Action<byte, string, string> OnRenamed;   // id, old, new
        public Func<NetDataWriter, bool> WriteFlagDump; // host fills the KV dump for a joiner

        NetManager _mgr;
        NetPeer _server;                                         // client side
        readonly Dictionary<int, byte> _peerToPlayer = new Dictionary<int, byte>();   // host side
        readonly Dictionary<byte, NetPeer> _playerToPeer = new Dictionary<byte, NetPeer>();
        readonly NetDataWriter _w = new NetDataWriter();
        string _key;
        int _max;

        static string Key(string password) => $"TunicCoop{Plugin.ProtocolVersion}:{password}";

        public void Host(int port, int maxPlayers, string password, string name)
        {
            Stop("restart");
            IsHost = true; LocalId = HostId; _max = maxPlayers; _key = Key(password);
            Players.Clear(); Players[HostId] = name; Meta.Clear(); Meta[HostId] = (GameInfo.Version, GameInfo.Store); LastPort = port;
            var l = new EventBasedNetListener();
            l.ConnectionRequestEvent += req =>
            {
                if (_playerToPeer.Count + 1 >= _max) { req.Reject(Encode("Session full")); return; }
                req.AcceptIfKey(_key);
            };
            l.PeerConnectedEvent += peer => Plugin.L.LogInfo($"[net] peer {peer} connected, waiting for Hello");
            l.PeerDisconnectedEvent += (peer, info) => HostDropPeer(peer, info.Reason.ToString());
            l.NetworkReceiveEvent += HostReceive;
            _mgr = new NetManager(l) { AutoRecycle = true, UpdateTime = 15, DisconnectTimeout = 10000, NatPunchEnabled = true };
            _mgr.NatPunchModule.Init(new EventBasedNatPunchListener());   // the relay introduces joiners; nothing to do here
            if (!_mgr.Start(IPAddress.Any, IPAddress.IPv6Any, port))
                throw new Exception($"Could not bind UDP port {port}");
            Plugin.L.LogInfo($"[net] hosting on UDP {port}, max {_max} players");
        }

        // ---------------- NAT punch-through via an optional relay (Network.Introducer) ----------------

        public static IPEndPoint Introducer()
        {
            string s = Plugin.Introducer.Value?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                int port = 27016; string host = s;
                int colon = s.LastIndexOf(':');
                if (colon > 0 && int.TryParse(s.Substring(colon + 1), out var p)) { host = s.Substring(0, colon); port = p; }
                var ip = IPAddress.TryParse(host, out var a) ? a : Array.Find(Dns.GetHostAddresses(host), x => x.AddressFamily == AddressFamily.InterNetwork);
                return ip != null ? new IPEndPoint(ip, port) : null;
            }
            catch { return null; }
        }

        float _nextRegister;
        public string RegisteredCode { get; private set; }

        /// <summary>Host: keep our join code registered with the relay so joiners behind other NATs can find us.</summary>
        public void TickHostRegistration(string code)
        {
            if (!IsHost || _mgr == null || string.IsNullOrEmpty(code) || UnityEngine.Time.unscaledTime < _nextRegister) return;
            _nextRegister = UnityEngine.Time.unscaledTime + 5f;
            var ep = Introducer(); if (ep == null) return;
            _mgr.NatPunchModule.SendNatIntroduceRequest(ep, "host:" + code);
            if (RegisteredCode != code) { RegisteredCode = code; Plugin.L.LogInfo("[net] join code registered with the relay"); }
        }

        NetPeer _direct;            // the plain connection attempt (may fail when NATs are in the way)
        float _punchUntil;          // a relay punch-through is still possible until then
        string _punchCode, _password;

        public void Join(string address, int port, string password, string name, string code = null)
        {
            Stop("restart");
            IsHost = false; LocalId = 255; Players.Clear(); Meta.Clear(); LastAddress = address; LastPort = port;
            _password = password;
            var l = new EventBasedNetListener();
            l.PeerConnectedEvent += peer =>
            {
                if (_server != null && _server != peer && _server.ConnectionState == ConnectionState.Connected) { peer.Disconnect(); return; }   // the other route won
                _server = peer;
                _punchUntil = 0;
                _w.Reset(); _w.Put((byte)Msg.Hello); _w.Put(name);
                _w.Put(Plugin.Version); _w.Put(GameInfo.Version); _w.Put(GameInfo.Store);
                peer.Send(_w, DeliveryMethod.ReliableOrdered);
            };
            l.PeerDisconnectedEvent += (peer, info) =>
            {
                // A failed route while another may still work (direct vs relay punch-through): not a disconnect yet.
                if (peer != _server && _server != null) return;
                if (LocalId == 255 && UnityEngine.Time.unscaledTime < _punchUntil) { Plugin.L.LogInfo("[net] direct route failed; waiting for the relay"); _server = null; return; }
                string extra = info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0 ? info.AdditionalData.GetString() : null;
                string why = Explain(info.Reason, extra, address, port);
                Plugin.L.LogWarning($"[net] disconnected from host ({why})");
                var ps = new List<byte>(Players.Keys);
                foreach (var id in ps) if (id != LocalId) OnPlayerLeft?.Invoke(id);
                Players.Clear();
                OnDisconnected?.Invoke(why);
                _stopAfterPoll = true;   // can't Stop() the manager from inside its own event
            };
            l.NetworkReceiveEvent += ClientReceive;
            _mgr = new NetManager(l) { AutoRecycle = true, UpdateTime = 15, DisconnectTimeout = 10000, NatPunchEnabled = true };
            var punch = new EventBasedNatPunchListener();
            punch.NatIntroductionSuccess += (target, type, token) =>
            {
                if (_server != null && _server.ConnectionState == ConnectionState.Connected) return;
                Plugin.L.LogInfo($"[net] relay introduced us to the host ({type}); connecting through the NAT");
                _server = _mgr.Connect(target, Key(_password));
            };
            _mgr.NatPunchModule.Init(punch);
            _mgr.Start();
            _server = _direct = _mgr.Connect(address, port, Key(password));
            Plugin.L.LogInfo($"[net] connecting to {address}:{port}");
            var relay = Introducer();
            if (relay != null && !string.IsNullOrEmpty(code))
            {
                _punchCode = code; _punchUntil = UnityEngine.Time.unscaledTime + 15f;
                _mgr.NatPunchModule.SendNatIntroduceRequest(relay, "join:" + code);
                Plugin.L.LogInfo("[net] also asking the relay for a punch-through");
            }
        }

        public void Stop(string why)
        {
            if (_mgr == null) return;
            _mgr.Stop(true);
            _mgr = null; _server = null;
            _peerToPlayer.Clear(); _playerToPeer.Clear();
            var ps = new List<byte>(Players.Keys);
            foreach (var id in ps) if (id != LocalId) OnPlayerLeft?.Invoke(id);
            Players.Clear();
            Plugin.L.LogInfo($"[net] stopped ({why})");
        }

        /// <summary>Test hook: vanish like a crashed game (no goodbye packet), so the host only notices by timeout
        /// or when we reconnect.</summary>
        public void SimulateCrash()
        {
            if (_mgr == null) return;
            _mgr.Stop(false);
            _mgr = null; _server = null;
            Players.Clear();
            Plugin.L.LogInfo("[net] simulated crash (no disconnect sent)");
        }

        bool _stopAfterPoll;
        public void Poll()
        {
            _mgr?.PollEvents();
            _mgr?.NatPunchModule.PollEvents();
            // Relay punch-through timed out without any route working: now it's a real failure.
            if (_mgr != null && !IsHost && LocalId == 255 && _punchUntil > 0 && UnityEngine.Time.unscaledTime > _punchUntil && (_server == null || _server.ConnectionState != ConnectionState.Connected))
            {
                _punchUntil = 0;
                OnDisconnected?.Invoke($"Couldn't reach {GameInfo.Show(LastAddress + ":" + LastPort)}, directly or through the relay.");
                _stopAfterPoll = true;
            }
            if (_stopAfterPoll) { _stopAfterPoll = false; Stop("disconnected"); }   // HUD shows Offline, F8/menu can retry
        }

        // ---------------- sending ----------------

        public NetDataWriter Begin(Msg m) { _w.Reset(); _w.Put((byte)m); return _w; }

        /// <summary>Send a message originating locally to everyone else.</summary>
        public void Broadcast(NetDataWriter w, DeliveryMethod dm)
        {
            if (_mgr == null) return;
            if (IsHost)
            {
                foreach (var p in _playerToPeer.Values) p.Send(w, dm);
            }
            else if (_server != null && _server.ConnectionState == ConnectionState.Connected && LocalId != 255)
            {
                _server.Send(w, dm);
            }
        }

        public void SendFlag(FlagKind kind, string key, string value)
        {
            var w = Begin(Msg.Flag);
            w.Put(LocalId); w.Put((byte)kind); w.Put(key); w.Put(value);
            Broadcast(w, DeliveryMethod.ReliableOrdered);
        }

        // ---------------- host ----------------

        void HostReceive(NetPeer peer, NetPacketReader r, byte channel, DeliveryMethod dm)
        {
            if (r.AvailableBytes < 1) return;
            var m = (Msg)r.GetByte();
            _peerToPlayer.TryGetValue(peer.Id, out byte from);
            bool known = _peerToPlayer.ContainsKey(peer.Id);

            switch (m)
            {
                case Msg.Hello:
                {
                    if (known) return;
                    string name = Sanitize(r.GetString(32));
                    string modVer = r.AvailableBytes > 0 ? r.GetString(32) : "?";
                    string gameVer = r.AvailableBytes > 0 ? r.GetString(32) : "?";
                    string store = r.AvailableBytes > 0 ? r.GetString(16) : "?";
                    if (modVer != Plugin.Version)
                    {
                        peer.Disconnect(Encode($"Mod version mismatch: host has {Plugin.Version}, you have {modVer}. Everyone needs the same version."));
                        return;
                    }
                    // Rejoin after a crash: their old connection may not have timed out yet. Same address + same
                    // name = the same player coming back; drop the stale one so they get their seat back now.
                    foreach (var kv in new List<KeyValuePair<byte, NetPeer>>(_playerToPeer))
                    {
                        if (kv.Value == peer || !Players.TryGetValue(kv.Key, out var oldName) || oldName != name) continue;
                        if (!kv.Value.Address.Equals(peer.Address)) continue;
                        Plugin.L.LogInfo($"[net] '{name}' reconnected; dropping their stale connection (player {kv.Key})");
                        HostDropPeer(kv.Value, "reconnected");
                        kv.Value.Disconnect();
                    }
                    byte id = 0;
                    for (byte i = 1; i < Plugin.HardPlayerCap; i++) if (!Players.ContainsKey(i)) { id = i; break; }
                    if (id == 0 || Players.Count >= _max) { peer.Disconnect(Encode("Session full")); return; }
                    _peerToPlayer[peer.Id] = id; _playerToPeer[id] = peer; Players[id] = name; Meta[id] = (gameVer, store);

                    var w = Begin(Msg.Welcome);
                    w.Put(id); w.Put((byte)_max); w.Put((byte)Players.Count);
                    foreach (var kv in Players) { w.Put(kv.Key); w.Put(kv.Value); var mt = Meta.TryGetValue(kv.Key, out var x) ? x : ("?", "?"); w.Put(mt.Item1); w.Put(mt.Item2); }
                    peer.Send(w, DeliveryMethod.ReliableOrdered);

                    if (WriteFlagDump != null)
                    {
                        var d = Begin(Msg.FlagDump);
                        if (WriteFlagDump(d)) peer.Send(d, DeliveryMethod.ReliableOrdered);
                    }

                    var j = Begin(Msg.Joined); j.Put(id); j.Put(name); j.Put(gameVer); j.Put(store);
                    foreach (var kv in _playerToPeer) if (kv.Key != id) kv.Value.Send(j, DeliveryMethod.ReliableOrdered);

                    Plugin.L.LogInfo($"[net] player {id} '{name}' joined ({Players.Count}/{_max})");
                    OnPlayerJoined?.Invoke(id, name);
                    break;
                }
                case Msg.State:
                {
                    if (!known) return;
                    byte claimed = r.GetByte();
                    if (claimed != from) return; // no spoofing other players
                    int start = r.Position;
                    // relay raw bytes
                    var w = Begin(Msg.State); w.Put(from); w.Put(r.RawData, start, r.AvailableBytes);
                    foreach (var kv in _playerToPeer) if (kv.Key != from) kv.Value.Send(w, DeliveryMethod.Sequenced);
                    OnState?.Invoke(from, r);
                    break;
                }
                case Msg.Vis:
                {
                    if (!known) return;
                    if (r.GetByte() != from) return;
                    int start = r.Position;
                    var w = Begin(Msg.Vis); w.Put(from); w.Put(r.RawData, start, r.AvailableBytes);
                    foreach (var kv in _playerToPeer) if (kv.Key != from) kv.Value.Send(w, DeliveryMethod.ReliableOrdered);
                    OnVis?.Invoke(from, r);
                    break;
                }
                case Msg.Monsters:
                {
                    if (!known) return;
                    if (r.GetByte() != from) return;
                    int start = r.Position;
                    var w = Begin(Msg.Monsters); w.Put(from); w.Put(r.RawData, start, r.AvailableBytes);
                    foreach (var kv in _playerToPeer) if (kv.Key != from) kv.Value.Send(w, DeliveryMethod.Unreliable);
                    OnMonsters?.Invoke(from, r);
                    break;
                }
                case Msg.Rename:
                {
                    if (!known) return;
                    ApplyRename(from, Sanitize(r.GetString(32)), true);
                    break;
                }
                case Msg.Flag:
                {
                    if (!known) return;
                    r.GetByte(); // origin, overwritten with the real sender
                    var kind = (FlagKind)r.GetByte(); string key = r.GetString(); string val = r.GetString();
                    var w = Begin(Msg.Flag); w.Put(from); w.Put((byte)kind); w.Put(key); w.Put(val);
                    foreach (var kv in _playerToPeer) if (kv.Key != from) kv.Value.Send(w, DeliveryMethod.ReliableOrdered);
                    OnFlag?.Invoke(from, kind, key, val);
                    break;
                }
            }
        }

        void HostDropPeer(NetPeer peer, string why)
        {
            if (!_peerToPlayer.TryGetValue(peer.Id, out byte id)) return;
            _peerToPlayer.Remove(peer.Id); _playerToPeer.Remove(id); Players.Remove(id); Meta.Remove(id);
            var w = Begin(Msg.Left); w.Put(id);
            foreach (var p in _playerToPeer.Values) p.Send(w, DeliveryMethod.ReliableOrdered);
            Plugin.L.LogInfo($"[net] player {id} left ({why})");
            OnPlayerLeft?.Invoke(id);
        }

        // ---------------- client ----------------

        void ClientReceive(NetPeer peer, NetPacketReader r, byte channel, DeliveryMethod dm)
        {
            if (r.AvailableBytes < 1) return;
            var m = (Msg)r.GetByte();
            switch (m)
            {
                case Msg.Welcome:
                {
                    LocalId = r.GetByte(); _max = r.GetByte(); int n = r.GetByte();
                    Players.Clear();
                    for (int i = 0; i < n; i++) { byte id = r.GetByte(); Players[id] = r.GetString(); Meta[id] = (r.GetString(), r.GetString()); }
                    Plugin.L.LogInfo($"[net] welcomed as player {LocalId} ({Players.Count}/{_max})");
                    OnWelcomed?.Invoke();
                    foreach (var kv in Players) if (kv.Key != LocalId) OnPlayerJoined?.Invoke(kv.Key, kv.Value);
                    break;
                }
                case Msg.Joined: { byte id = r.GetByte(); string name = r.GetString(); Players[id] = name; Meta[id] = (r.GetString(), r.GetString()); OnPlayerJoined?.Invoke(id, name); break; }
                case Msg.Left: { byte id = r.GetByte(); Players.Remove(id); Meta.Remove(id); OnPlayerLeft?.Invoke(id); break; }
                case Msg.State: { byte id = r.GetByte(); OnState?.Invoke(id, r); break; }
                case Msg.Vis: { byte id = r.GetByte(); OnVis?.Invoke(id, r); break; }
                case Msg.Monsters: { byte id = r.GetByte(); OnMonsters?.Invoke(id, r); break; }
                case Msg.Flag:
                {
                    byte from = r.GetByte(); var kind = (FlagKind)r.GetByte();
                    OnFlag?.Invoke(from, kind, r.GetString(), r.GetString());
                    break;
                }
                case Msg.FlagDump: OnFlagDump?.Invoke(r); break;
                case Msg.Rename: { byte id = r.GetByte(); ApplyRename(id, r.GetString(), false); break; }
            }
        }

        /// <summary>Round-trip ms to a player as seen from here (host: each client; client: the host only).</summary>
        public int PingTo(byte id)
        {
            if (id == LocalId) return 0;
            if (IsHost) return _playerToPeer.TryGetValue(id, out var p) ? p.Ping : -1;
            return id == HostId && _server != null ? _server.Ping : -1;
        }

        /// <summary>Plain-language reason for a dropped/refused connection.</summary>
        static string Explain(DisconnectReason reason, string extra, string address, int port)
        {
            if (!string.IsNullOrEmpty(extra)) return extra;
            switch (reason)
            {
                case DisconnectReason.ConnectionFailed: return $"Couldn't reach {GameInfo.Show(address + ":" + port)}. Check the address, that the host is hosting, and port forwarding / VPN / firewall.";
                case DisconnectReason.ConnectionRejected: return "The host refused the connection: wrong password, or a different mod version.";
                case DisconnectReason.Timeout: return "Lost connection to the host (timed out).";
                case DisconnectReason.RemoteConnectionClose: return "The host ended the session.";
                case DisconnectReason.HostUnreachable: case DisconnectReason.NetworkUnreachable: return $"Network can't reach {GameInfo.Show(address)}.";
                case DisconnectReason.DisconnectPeerCalled: return "Left the session.";
                default: return reason.ToString();
            }
        }

        /// <summary>Change our display name for everyone, live.</summary>
        public void Rename(string newName)
        {
            newName = Sanitize(newName);
            if (_mgr == null || LocalId == 255) return;
            if (IsHost) { ApplyRename(HostId, newName, true); return; }
            var w = Begin(Msg.Rename); w.Put(newName);
            _server?.Send(w, DeliveryMethod.ReliableOrdered);
        }

        void ApplyRename(byte id, string name, bool broadcast)
        {
            Players.TryGetValue(id, out var old);
            if (old == name) return;
            Players[id] = name;
            OnRenamed?.Invoke(id, old, name);
            if (!broadcast) return;
            var w = Begin(Msg.Rename); w.Put(id); w.Put(name);
            foreach (var p in _playerToPeer.Values) p.Send(w, DeliveryMethod.ReliableOrdered);
        }

        static NetDataWriter Encode(string s) { var w = new NetDataWriter(); w.Put(s); return w; }

        static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Fox";
            s = s.Trim();
            return s.Length > 24 ? s.Substring(0, 24) : s;
        }
    }
}
