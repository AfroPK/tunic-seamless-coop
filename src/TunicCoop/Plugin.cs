using BepInEx;
using BepInEx.Configuration;
using BepInEx.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace TunicCoop
{
    [BepInPlugin(Guid, "TUNIC Seamless Co-op", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "com.freddy.tuniccoop";
        public const string Version = "0.1.0";
        public const int ProtocolVersion = 2;
        public const int HardPlayerCap = 8;

        internal static ManualLogSource L;
        internal static ConfigEntry<string> PlayerName;
        internal static ConfigEntry<bool> NameChosen;
        internal static ConfigEntry<bool> StreamerMode;
        internal static ConfigEntry<bool> Revive;
        internal static ConfigEntry<float> ReviveSeconds;
        internal static ConfigEntry<int> ReviveHealthPercent;
        internal static ConfigEntry<string> JoinAddress;
        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<int> MaxPlayers;
        internal static ConfigEntry<string> Password;
        internal static ConfigEntry<bool> SyncWorldFlags;
        internal static ConfigEntry<bool> EnableAutopilot;
        internal static ConfigEntry<bool> EnableHookProbe;

        public override void Load()
        {
            L = Log;
            PlayerName = Config.Bind("Player", "Name", "Fox", "Name shown to other players (change it in game with F1).");
            Revive = Config.Bind("Gameplay", "Revive", true,
                "A lethal hit while a teammate is still up leaves you downed (spectating) until someone revives you, instead of dying. Everyone down = normal death.");
            ReviveSeconds = Config.Bind("Gameplay", "ReviveSeconds", 3f, "Seconds a teammate must stand next to you to revive you.");
            ReviveHealthPercent = Config.Bind("Gameplay", "ReviveHealthPercent", 50, new ConfigDescription("Health after being revived.", new AcceptableValueRange<int>(1, 100)));
            StreamerMode = Config.Bind("Privacy", "StreamerMode", false,
                "Hide IP addresses everywhere on screen (panel, messages, menu) so they never show on stream. Toggle in the F1 menu.");
            NameChosen = Config.Bind("Player", "NameChosen", false, "Set once you have picked a name in the F1 menu.");
            JoinAddress = Config.Bind("Network", "JoinAddress", "127.0.0.1", "Host IP (or hostname) to join with F8.");
            Port = Config.Bind("Network", "Port", 27015, "UDP port. Host must forward it (or use a VPN like Tailscale/Radmin).");
            MaxPlayers = Config.Bind("Network", "MaxPlayers", 4,
                new ConfigDescription("Players per session including host.", new AcceptableValueRange<int>(2, HardPlayerCap)));
            Password = Config.Bind("Network", "Password", "", "Optional session password (must match host).");
            SyncWorldFlags = Config.Bind("World", "SyncWorldFlags", true,
                "Share world progress (doors, chests, bells, items...). Host's world is the truth.");

            EnableAutopilot = Config.Bind("Debug", "EnableAutopilot", false,
                "Test autopilot: reads BepInEx/coop_test/cmd.txt and drives the game with a virtual gamepad. Developers only.");
            EnableHookProbe = Config.Bind("Debug", "HookProbe", false, "Count calls on a few game methods (diagnostics). Developers only.");

            ClassInjector.RegisterTypeInIl2Cpp<CoopManager>();
            ClassInjector.RegisterTypeInIl2Cpp<Puppet>();
            ClassInjector.RegisterTypeInIl2Cpp<RootMotionSink>();

            ChestSync.InstallHooks();
            ReviveSystem.InstallHooks();
            if (EnableHookProbe.Value) HookProbe.Install();

            var harmony = new Harmony(Guid);
            try { harmony.PatchAll(typeof(Plugin).Assembly); }
            catch (System.Exception e) { Log.LogError("Harmony PatchAll failed: " + e); }
            foreach (var m in harmony.GetPatchedMethods()) Log.LogInfo($"[harmony] patched {m.DeclaringType?.Name}.{m.Name}");

            var go = new GameObject("TunicCoop");
            Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<CoopManager>();

            L.LogInfo($"TUNIC Seamless Co-op {Version} loaded. F7 host, F8 join {JoinAddress.Value}:{Port.Value}, F9 leave, F10 loopback ghost.");
        }
    }
}
