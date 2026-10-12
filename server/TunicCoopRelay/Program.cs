using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using LiteNetLib;

namespace TunicCoopRelay
{
    /// <summary>
    /// Matchmaking / NAT hole-punching introducer for TUNIC Seamless Co-op. It never carries game traffic: it only
    /// tells a host and a joiner each other's public address so their routers let them talk directly.
    ///
    ///   Host:   registers its join code every few seconds ("host:CODE").
    ///   Joiner: asks for that code ("join:CODE"); the relay introduces both, they punch through and connect.
    ///
    /// Run it anywhere reachable on UDP (default port 27016):  TunicCoopRelay [port]
    /// Then set Network.Introducer = "your.server:27016" in every player's config.
    /// </summary>
    class Program : INatPunchListener
    {
        class Waiting { public IPEndPoint Internal, External; public DateTime Seen; }
        readonly Dictionary<string, Waiting> _hosts = new Dictionary<string, Waiting>();
        NetManager _mgr;

        static void Main(string[] args)
        {
            int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 27016;
            new Program().Run(port);
        }

        void Run(int port)
        {
            _mgr = new NetManager(new EventBasedNetListener()) { NatPunchEnabled = true, IPv6Enabled = false };
            _mgr.NatPunchModule.Init(this);
            if (!_mgr.Start(port)) { Console.WriteLine($"Could not bind UDP {port}"); return; }
            Console.WriteLine($"TUNIC Co-op relay listening on UDP {port}. Ctrl+C to stop.");
            var last = DateTime.UtcNow;
            while (true)
            {
                _mgr.NatPunchModule.PollEvents();
                if ((DateTime.UtcNow - last).TotalSeconds > 10)
                {
                    last = DateTime.UtcNow;
                    var stale = new List<string>();
                    foreach (var kv in _hosts) if ((DateTime.UtcNow - kv.Value.Seen).TotalSeconds > 60) stale.Add(kv.Key);
                    foreach (var k in stale) _hosts.Remove(k);
                }
                Thread.Sleep(10);
            }
        }

        public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
        {
            if (token == null || token.Length > 64) return;
            if (token.StartsWith("host:"))
            {
                string code = token.Substring(5);
                _hosts[code] = new Waiting { Internal = localEndPoint, External = remoteEndPoint, Seen = DateTime.UtcNow };
                return;
            }
            if (token.StartsWith("join:"))
            {
                string code = token.Substring(5);
                if (!_hosts.TryGetValue(code, out var h)) { Console.WriteLine($"{DateTime.Now:T} join for unknown code"); return; }
                _mgr.NatPunchModule.NatIntroduce(h.Internal, h.External, localEndPoint, remoteEndPoint, code);
                Console.WriteLine($"{DateTime.Now:T} introduced a joiner to a host");
            }
        }

        public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token) { }
    }
}
