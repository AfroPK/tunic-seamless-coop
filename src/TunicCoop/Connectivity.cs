using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// Join codes: an address + port as a short code ("4T7JA-E1QJK") that is easier to share than an IP and
    /// can be typed into the Join field. (Anyone can decode it back to the address: streamer mode hides codes too.)
    /// </summary>
    public static class JoinCode
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford base32 (no I L O U)
        const ulong Scramble = 0x5A3C96E1B7D4UL;                      // only so codes don't visibly look like IPs

        public static string Encode(IPAddress ip, int port)
        {
            var b = ip.GetAddressBytes(); if (b.Length != 4) return null;
            ulong v = ((ulong)b[0] << 40) | ((ulong)b[1] << 32) | ((ulong)b[2] << 24) | ((ulong)b[3] << 16) | (ushort)port;
            v ^= Scramble;
            var sb = new StringBuilder();
            for (int i = 0; i < 10; i++) { sb.Insert(0, Alphabet[(int)(v & 31)]); v >>= 5; }
            return sb.ToString(0, 5) + "-" + sb.ToString(5, 5);
        }

        public static bool TryDecode(string code, out string address, out int port)
        {
            address = null; port = 0;
            if (string.IsNullOrWhiteSpace(code)) return false;
            var s = code.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "").Replace('O', '0').Replace('I', '1').Replace('L', '1');
            if (s.Length != 10) return false;
            ulong v = 0;
            foreach (char ch in s) { int d = Alphabet.IndexOf(ch); if (d < 0) return false; v = (v << 5) | (uint)d; }
            v ^= Scramble;
            if (v >> 48 != 0) return false;
            port = (int)(v & 0xFFFF);
            address = $"{(v >> 40) & 255}.{(v >> 32) & 255}.{(v >> 24) & 255}.{(v >> 16) & 255}";
            return port > 0;
        }

        public static bool LooksLikeCode(string s) => !string.IsNullOrEmpty(s) && s.IndexOf('.') < 0 && s.IndexOf(':') < 0 && TryDecode(s, out _, out _);
    }

    /// <summary>
    /// Automatic port forwarding through the router (UPnP IGD), so friends over the internet can join without
    /// touching router settings. Runs on a background thread; never blocks the game.
    /// </summary>
    public static class PortMapper
    {
        public enum State { Off, Working, Mapped, Unavailable }
        public static volatile State Status = State.Off;
        public static volatile string ExternalIp;
        static string _controlUrl, _serviceType, _localIp;
        static int _port;

        public static void Start(int port)
        {
            if (!Plugin.AutoPortForward.Value) { Status = State.Off; return; }
            _port = port; Status = State.Working; ExternalIp = null;
            new Thread(() =>
            {
                try
                {
                    if (!Discover()) { Status = State.Unavailable; Plugin.L.LogInfo("[upnp] no UPnP router found"); return; }
                    Soap("AddPortMapping",
                        $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>UDP</NewProtocol>" +
                        $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{_localIp}</NewInternalClient><NewEnabled>1</NewEnabled>" +
                        "<NewPortMappingDescription>TUNIC Co-op</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration>");
                    var r = Soap("GetExternalIPAddress", "");
                    var m = Regex.Match(r ?? "", "<NewExternalIPAddress>([^<]+)</NewExternalIPAddress>");
                    ExternalIp = m.Success ? m.Groups[1].Value.Trim() : null;
                    Status = State.Mapped;
                    Plugin.L.LogInfo($"[upnp] UDP {port} forwarded to this PC (external address {(ExternalIp != null ? "found" : "unknown")})");
                }
                catch (Exception e) { Status = State.Unavailable; Plugin.L.LogInfo("[upnp] port mapping failed: " + e.Message); }
            }) { IsBackground = true, Name = "TunicCoop UPnP" }.Start();
        }

        public static void Stop()
        {
            if (Status != State.Mapped || _controlUrl == null) { Status = State.Off; return; }
            int port = _port;
            var t = new Thread(() =>
            {
                try { Soap("DeletePortMapping", $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>UDP</NewProtocol>"); Plugin.L.LogInfo("[upnp] mapping removed"); }
                catch { }
            }) { IsBackground = true };
            t.Start(); t.Join(1500);
            Status = State.Off;
        }

        static bool Discover()
        {
            using (var udp = new UdpClient())
            {
                udp.Client.ReceiveTimeout = 2500;
                var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                foreach (var st in new[] { "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANIPConnection:2", "urn:schemas-upnp-org:service:WANPPPConnection:1" })
                {
                    var req = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: {st}\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n\r\n");
                    udp.Send(req, req.Length, group);
                }
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    IPEndPoint from = null; byte[] resp;
                    try { resp = udp.Receive(ref from); } catch (SocketException) { break; }
                    var text = Encoding.ASCII.GetString(resp);
                    var loc = Regex.Match(text, @"(?im)^location:\s*(\S+)");
                    if (!loc.Success) continue;
                    if (TryDescription(loc.Groups[1].Value)) return true;
                }
            }
            return false;
        }

        static bool TryDescription(string location)
        {
            var req = (HttpWebRequest)WebRequest.Create(location);
            req.Timeout = 3000;
            string xml;
            using (var resp = req.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream())) xml = sr.ReadToEnd();
            foreach (Match svc in Regex.Matches(xml, @"<service>(.*?)</service>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var type = Regex.Match(svc.Groups[1].Value, @"<serviceType>\s*([^<]+?)\s*</serviceType>", RegexOptions.IgnoreCase).Groups[1].Value;
                if (type.IndexOf("WANIPConnection", StringComparison.OrdinalIgnoreCase) < 0 && type.IndexOf("WANPPPConnection", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var ctl = Regex.Match(svc.Groups[1].Value, @"<controlURL>\s*([^<]+?)\s*</controlURL>", RegexOptions.IgnoreCase).Groups[1].Value;
                var baseUri = new Uri(location);
                _controlUrl = new Uri(baseUri, ctl).ToString();
                _serviceType = type;
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(baseUri.Host, baseUri.Port);
                    _localIp = ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
                return true;
            }
            return false;
        }

        static string Soap(string action, string args)
        {
            string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
                          $"<u:{action} xmlns:u=\"{_serviceType}\">{args}</u:{action}></s:Body></s:Envelope>";
            var req = (HttpWebRequest)WebRequest.Create(_controlUrl);
            req.Method = "POST"; req.Timeout = 3000; req.ContentType = "text/xml; charset=\"utf-8\"";
            req.Headers.Add("SOAPACTION", $"\"{_serviceType}#{action}\"");
            var bytes = Encoding.UTF8.GetBytes(body);
            using (var rs = req.GetRequestStream()) rs.Write(bytes, 0, bytes.Length);
            using (var resp = req.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream())) return sr.ReadToEnd();
        }
    }

    /// <summary>Public address via STUN (when the router has no UPnP), so the host can still see an internet
    /// join code to use with a manual port forward. Only asked while hosting.</summary>
    public static class PublicAddress
    {
        public static volatile string Ip;
        static bool _asked;

        public static void Ask()
        {
            if (_asked || !Plugin.DiscoverPublicAddress.Value) return;
            _asked = true;
            new Thread(() =>
            {
                try
                {
                    using (var udp = new UdpClient())
                    {
                        udp.Client.ReceiveTimeout = 2500;
                        var tid = new byte[12]; new System.Random().NextBytes(tid);
                        var req = new byte[20]; req[1] = 1; req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42; Buffer.BlockCopy(tid, 0, req, 8, 12);
                        udp.Send(req, req.Length, "stun.l.google.com", 19302);
                        IPEndPoint from = null; var r = udp.Receive(ref from);
                        for (int i = 20; i + 4 <= r.Length;)
                        {
                            int type = (r[i] << 8) | r[i + 1], len = (r[i + 2] << 8) | r[i + 3];
                            if ((type == 0x0020 || type == 0x0001) && len >= 8 && r[i + 5] == 1)
                            {
                                var a = new byte[4]; Buffer.BlockCopy(r, i + 8, a, 0, 4);
                                if (type == 0x0020) { a[0] ^= 0x21; a[1] ^= 0x12; a[2] ^= 0xA4; a[3] ^= 0x42; }
                                Ip = new IPAddress(a).ToString();
                                break;
                            }
                            i += 4 + len + ((4 - len % 4) % 4);
                        }
                    }
                }
                catch (Exception e) { Plugin.L.LogInfo("[stun] " + e.Message); }
            }) { IsBackground = true, Name = "TunicCoop STUN" }.Start();
        }
    }

    /// <summary>Checks GitHub once per launch for a newer release, on a background thread. (Unity's web request
    /// can't be used: TUNIC's build strips its download handler.)</summary>
    public static class UpdateCheck
    {
        public const string RepoUrl = "github.com/AfroPK/tunic-seamless-coop";
        public static volatile string Latest;     // e.g. "0.3.0" when newer than ours
        static volatile string _found;
        static bool _started, _announced;

        public static void Tick()
        {
            if (!Plugin.CheckForUpdates.Value) return;
            if (!_started)
            {
                _started = true;
                new Thread(Check) { IsBackground = true, Name = "TunicCoop update check" }.Start();
                return;
            }
            if (_found != null && !_announced)
            {
                _announced = true;
                if (Newer(_found, Plugin.Version))
                {
                    Latest = _found;
                    CoopUI.Toast($"TUNIC Co-op {Latest} is available ({RepoUrl}). Everyone in a session needs the same version.", CoopUI.Kind.Info, 10f);
                }
            }
        }

        static void Check()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/AfroPK/tunic-seamless-coop/releases/latest");
                req.UserAgent = "TunicCoop/" + Plugin.Version; req.Timeout = 10000; req.Accept = "application/vnd.github+json";
                string text;
                using (var resp = req.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream())) text = sr.ReadToEnd();
                var m = Regex.Match(text, "\"tag_name\"\\s*:\\s*\"v?([0-9][^\"]*)\"");
                _found = m.Success ? m.Groups[1].Value : "0";
                Plugin.L.LogInfo($"[update] latest release: {(m.Success ? m.Groups[1].Value : "unknown")} (ours {Plugin.Version})");
            }
            catch (Exception e) { Plugin.L.LogInfo("[update] check failed: " + e.Message); }
        }

        public static bool Newer(string a, string b)
        {
            int[] P(string s) { var p = s.Split('-')[0].Split('.'); var r = new int[3]; for (int i = 0; i < 3 && i < p.Length; i++) int.TryParse(p[i], out r[i]); return r; }
            var x = P(a); var y = P(b);
            for (int i = 0; i < 3; i++) if (x[i] != y[i]) return x[i] > y[i];
            return false;
        }
    }
    /// <summary>
    /// Steam (only when the game runs from Steam): shows "Playing TUNIC co-op" to friends and offers them
    /// "Join Game" in the friends list. If their game is closed, Steam starts it with our join code on the
    /// command line and the mod joins automatically once the save is loaded. Never required: everything works
    /// without Steam.
    /// </summary>
    public static class SteamPresence
    {
        // The game already loaded steam_api64.dll (from Tunic_Data/Plugins), where Mono's DllImport can't find it:
        // bind to the loaded module's exports directly.
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string proc);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr FriendsFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool SetRpFn(IntPtr self, byte[] key, byte[] value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void ClearRpFn(IntPtr self);
        static FriendsFn SteamAPI_SteamFriends_v017;
        static SetRpFn SteamAPI_ISteamFriends_SetRichPresence;
        static ClearRpFn SteamAPI_ISteamFriends_ClearRichPresence;

        static bool Bind()
        {
            if (SteamAPI_SteamFriends_v017 != null) return true;
            var mod = GetModuleHandle("steam_api64.dll");
            if (mod == IntPtr.Zero) return false;
            IntPtr a = GetProcAddress(mod, "SteamAPI_SteamFriends_v017"), b = GetProcAddress(mod, "SteamAPI_ISteamFriends_SetRichPresence"), c = GetProcAddress(mod, "SteamAPI_ISteamFriends_ClearRichPresence");
            if (a == IntPtr.Zero || b == IntPtr.Zero || c == IntPtr.Zero) return false;
            SteamAPI_SteamFriends_v017 = (FriendsFn)Marshal.GetDelegateForFunctionPointer(a, typeof(FriendsFn));
            SteamAPI_ISteamFriends_SetRichPresence = (SetRpFn)Marshal.GetDelegateForFunctionPointer(b, typeof(SetRpFn));
            SteamAPI_ISteamFriends_ClearRichPresence = (ClearRpFn)Marshal.GetDelegateForFunctionPointer(c, typeof(ClearRpFn));
            return true;
        }

        public const string LaunchArg = "+tunic-coop";
        static bool _broken;
        static string _last;
        public static string LastStatus;

        static byte[] U(string s) { var b = Encoding.UTF8.GetBytes(s ?? ""); Array.Resize(ref b, b.Length + 1); return b; }

        public static void Update(string status, string connectCode, int players)
        {
            if (_broken || !Plugin.SteamInvites.Value || GameInfo.Store != "Steam") return;
            string key = status + "|" + connectCode + "|" + players;
            if (key == _last) return;
            _last = key;
            try
            {
                if (!Bind()) { _broken = true; Plugin.L.LogInfo("[steam] steam_api64 not loaded: no Steam presence"); return; }
                var f = SteamAPI_SteamFriends_v017();
                if (f == IntPtr.Zero) return;
                if (status == null) { SteamAPI_ISteamFriends_ClearRichPresence(f); LastStatus = "cleared"; return; }
                bool ok = SteamAPI_ISteamFriends_SetRichPresence(f, U("status"), U(status));
                LastStatus = $"status ok={ok} connect={(connectCode != null ? "yes" : "no")}";
                Plugin.L.LogInfo("[steam] presence: " + LastStatus);
                SteamAPI_ISteamFriends_SetRichPresence(f, U("connect"), U(connectCode != null ? LaunchArg + " " + connectCode : ""));
                SteamAPI_ISteamFriends_SetRichPresence(f, U("steam_player_group"), U(connectCode ?? ""));
                SteamAPI_ISteamFriends_SetRichPresence(f, U("steam_player_group_size"), U(players.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception e) { _broken = true; Plugin.L.LogInfo("[steam] rich presence unavailable: " + e.Message); }
        }

        /// <summary>A join code Steam passed on the command line ("Join Game" while the game was closed).</summary>
        public static string CodeFromCommandLine()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == LaunchArg) return args[i + 1];
            }
            catch { }
            return null;
        }
    }
}
