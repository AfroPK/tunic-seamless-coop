using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// Talking without voice chat: F3 pings a spot everyone can see (with an arrow at the screen edge when it is
    /// off screen), F4 opens quick chat (1-6) for short phrases shown above your fox and in everyone's chat log.
    /// </summary>
    public static class Signals
    {
        public static readonly string[] Phrases = { "Over here!", "Help!", "Wait for me", "Let's go!", "Thanks!", "Careful!" };
        const float PingLife = 7f, BubbleLife = 4.5f;

        public class Ping { public byte From; public string Scene; public Vector3 Pos; public float At; }
        public static readonly List<Ping> Pings = new List<Ping>();
        public static readonly Dictionary<byte, (string text, float at)> Bubbles = new Dictionary<byte, (string, float)>();
        public static readonly List<(string who, string text, Color color, float at)> Log = new List<(string, string, Color, float)>();

        public static bool ChatOpen;
        static float _chatOpenedAt, _lastPing;

        public static void Tick(Net net)
        {
            float now = Time.unscaledTime;
            Pings.RemoveAll(p => now - p.At > PingLife);
            if (ChatOpen && now - _chatOpenedAt > 5f) ChatOpen = false;
            if (Log.Count > 6) Log.RemoveRange(0, Log.Count - 6);
        }

        public static void Key(KeyCode k, Net net)
        {
            if (k == KeyCode.F3) { PingHere(net); return; }
            if (k == KeyCode.F4) { ChatOpen = !ChatOpen; _chatOpenedAt = Time.unscaledTime; return; }
            if (!ChatOpen) return;
            int i = k >= KeyCode.Alpha1 && k <= KeyCode.Alpha6 ? k - KeyCode.Alpha1 : k >= KeyCode.Keypad1 && k <= KeyCode.Keypad6 ? k - KeyCode.Keypad1 : -1;
            if (i >= 0) { Say(net, i); ChatOpen = false; }
            else if (k == KeyCode.Escape) ChatOpen = false;
        }

        public static void PingHere(Net net)
        {
            var pc = PlayerCharacter.instance;
            if (pc == null || net == null || !net.Connected || net.LocalId == 255) { CoopUI.Toast("Pings work in a co-op session"); return; }
            if (Time.unscaledTime - _lastPing < 0.75f) return;    // no spamming
            _lastPing = Time.unscaledTime;
            var pos = pc.transform.position + pc.transform.forward * 2.5f;
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            AddPing(net.LocalId, scene, pos);
            net.SendFlag(FlagKind.Ping, net.LocalId.ToString(CultureInfo.InvariantCulture),
                string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}", scene, pos.x, pos.y, pos.z));
        }

        public static void OnPing(Net net, byte from, string value, string ourScene)
        {
            var v = value.Split('|'); if (v.Length < 4) return;
            var c = CultureInfo.InvariantCulture;
            var pos = new Vector3(float.Parse(v[1], c), float.Parse(v[2], c), float.Parse(v[3], c));
            AddPing(from, v[0], pos);
            if (v[0] != ourScene) CoopUI.Toast($"{Name(net, from)} pinged a spot in {GameInfo.FriendlyArea(v[0])}", CoopUI.Kind.Info, 4f);
        }

        static void AddPing(byte from, string scene, Vector3 pos)
        {
            Pings.RemoveAll(p => p.From == from);              // one ping per player
            Pings.Add(new Ping { From = from, Scene = scene, Pos = pos, At = Time.unscaledTime });
        }

        public static void Say(Net net, int phrase)
        {
            if (phrase < 0 || phrase >= Phrases.Length || net == null || !net.Connected || net.LocalId == 255) return;
            Show(net, net.LocalId, phrase);
            net.SendFlag(FlagKind.Chat, net.LocalId.ToString(CultureInfo.InvariantCulture), phrase.ToString(CultureInfo.InvariantCulture));
        }

        public static void OnChat(Net net, byte from, string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) Show(net, from, i);
        }

        static void Show(Net net, byte from, int i)
        {
            if (i < 0 || i >= Phrases.Length) return;
            Bubbles[from] = (Phrases[i], Time.unscaledTime);
            Log.Add((Name(net, from), Phrases[i], CoopUI.PlayerColors[from % CoopUI.PlayerColors.Length], Time.unscaledTime));
            Plugin.L.LogInfo($"[chat] {Name(net, from)}: {Phrases[i]}");
        }

        public static string BubbleFor(byte id)
        {
            if (Bubbles.TryGetValue(id, out var b) && Time.unscaledTime - b.at < BubbleLife) return b.text;
            return null;
        }

        public static string Describe() => $"pings={Pings.Count} chatOpen={ChatOpen} log=[{string.Join(" / ", Log.ConvertAll(l => l.who + ": " + l.text))}]";

        static string Name(Net net, byte id) => net != null && net.Players.TryGetValue(id, out var n) ? n : "Player " + id;
    }
}
