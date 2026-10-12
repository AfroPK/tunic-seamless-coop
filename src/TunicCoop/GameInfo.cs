using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using BepInEx;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// Store-agnostic facts about this install. The mod never talks to Steam/Epic/GOG: networking is plain UDP
    /// (LiteNetLib), so any PC copy can play with any other — as long as the GAME version matches, since enemy
    /// ids and save keys come from the game build.
    /// </summary>
    public static class GameInfo
    {
        static string _store;

        public static string Version
        {
            get { try { return Application.version; } catch { return "?"; } }
        }

        /// <summary>Best-effort store guess from files shipped next to the game (informational only).</summary>
        public static string Store
        {
            get
            {
                if (_store != null) return _store;
                _store = "PC";
                try
                {
                    string root = Path.GetDirectoryName(Paths.ExecutablePath);
                    bool Has(string pattern) =>
                        Directory.GetFiles(root, pattern, SearchOption.AllDirectories).Length > 0;
                    if (Has("steam_api*.dll") || Has("Steamworks.NET.txt")) _store = "Steam";
                    else if (Has("EOSSDK-*.dll") || Has(".egstore")) _store = "Epic";
                    else if (Has("Galaxy*.dll") || Has("goggame-*.info")) _store = "GOG";
                    else if (Has("MicrosoftGame.config") || Has("appxmanifest.xml")) _store = "Xbox";
                }
                catch { }
                return _store;
            }
        }

        /// <summary>This PC's IPv4 addresses that another player could reach (LAN / VPN adapters).</summary>
        public static List<string> LocalAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var s = ua.Address.ToString();
                        if (s.StartsWith("169.254.")) continue;               // link-local, unreachable
                        // Prefer real LAN/VPN adapters over virtual switches (Hyper-V "vEthernet").
                        if (ni.Name.StartsWith("vEthernet") || ni.Description.Contains("Hyper-V")) continue;
                        if (!list.Contains(s)) list.Add(s);
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>An address as it may appear on screen: hidden in streamer mode.</summary>
        public static string Show(string address) => Plugin.StreamerMode != null && Plugin.StreamerMode.Value ? "the host (address hidden)" : address;

        public static string FriendlyArea(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return "";
            if (scene == "Loading") return "loading…";
            return scene.Replace(" Redux", "").Replace("_", " ").Trim();
        }
    }
}
