using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// Down-but-not-out. A lethal hit while another player is still up leaves you DOWNED instead of dead: no
    /// damage, no control, the camera spectates teammates (←/→ to switch). A teammate standing next to you for
    /// ReviveSeconds brings you back. You only really die (TUNIC's normal death + campfire respawn) when every
    /// player is down, or when you hold F2 to respawn yourself.
    ///
    /// Death is intercepted at Creature.IDamageable_ReceiveDamage (native hook; PlayerCharacter doesn't override
    /// it): the killing blow is reduced to leave 1 hp, so TUNIC's death sequence never starts. Falls/instant deaths
    /// don't go through damage and stay normal deaths.
    /// Spectating shifts the game camera by (teammate − our fox) after CameraController positions it, keeping
    /// TUNIC's own framing and smoothing; the shift is removed before the game's update so it never accumulates.
    /// </summary>
    public static class ReviveSystem
    {
        // ---------------- state ----------------
        public static bool Downed { get; private set; }
        static float _downedAt;
        static readonly HashSet<byte> _downedPlayers = new HashSet<byte>();
        static byte _spectating = 255;
        static float _giveUpHeld;
        static bool _bypass;            // our own killing blow must not be intercepted
        static bool _prevLock;
        static byte _reviveTarget = 255; static float _reviveProgress;
        public static bool IsDowned(byte id) => _downedPlayers.Contains(id);
        public static byte Spectating => _spectating;
        public static (byte target, float progress) ReviveProgress => (_reviveTarget, _reviveProgress);

        const float ReviveRange = 2.5f;   // metres; generous so a teammate doesn't have to stand pixel-perfect
        static Net _net;
        static string _scene;

        // ---------------- hooks ----------------
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate byte ReceiveDamageFn(IntPtr self, int dmg, int poise, IntPtr force, float freeze, int poison, IntPtr mi);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void VoidFn(IntPtr self, IntPtr mi);
        static ReceiveDamageFn _origDamage;
        static VoidFn _origCamUpdate, _origCamFixed;
        static Vector3 _camShift;

        public static void InstallHooks()
        {
            try
            {
                _origDamage = NativeHook.Install<ReceiveDamageFn>("Creature.IDamageable_ReceiveDamage",
                    NativeHook.MethodPtr(typeof(Creature), "NativeMethodInfoPtr_IDamageable_ReceiveDamage_Private_Virtual_Final_New_Boolean_Int32_Int32_Vector3_Single_Int32_0"), DamageHook);
            }
            catch (Exception e) { Plugin.L.LogError("[revive] damage hook failed (revive disabled): " + e.Message); }
            try
            {
                _origCamUpdate = NativeHook.Install<VoidFn>("CameraController.Update",
                    NativeHook.MethodPtr(typeof(CameraController), "NativeMethodInfoPtr_Update_Protected_Void_0"), (s, m) => CamStep(s, m, _origCamUpdate));
                _origCamFixed = NativeHook.Install<VoidFn>("CameraController.FixedUpdate",
                    NativeHook.MethodPtr(typeof(CameraController), "NativeMethodInfoPtr_FixedUpdate_Protected_Void_0"), (s, m) => CamStep(s, m, _origCamFixed));
            }
            catch (Exception e) { Plugin.L.LogError("[revive] camera hook failed (no spectating): " + e.Message); }
        }

        static byte DamageHook(IntPtr self, int dmg, int poise, IntPtr force, float freeze, int poison, IntPtr mi)
        {
            try
            {
                var pc = PlayerCharacter.instance;
                if (!_bypass && pc != null && self == pc.Pointer && Plugin.Revive.Value && _net != null && _net.Connected)
                {
                    if (Downed) return 0;                                    // no damage while down
                    int hp = pc.hp;
                    if (dmg >= hp && AnyTeammateUp())
                    {
                        byte r = hp > 1 ? _origDamage(self, hp - 1, poise, force, freeze, poison, mi) : (byte)1; // keep the hit reaction
                        GoDown();
                        return r;
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[revive] damage hook: " + e.Message); }
            return _origDamage(self, dmg, poise, force, freeze, poison, mi);
        }

        static void CamStep(IntPtr self, IntPtr mi, VoidFn orig)
        {
            Transform t = null;
            try { t = new CameraController(self).transform; if (_camShift != Vector3.zero) t.position -= _camShift; } catch { }
            orig(self, mi);
            try
            {
                _camShift = Vector3.zero;
                if (t == null || !Downed || _spectating == 255) return;
                var pc = PlayerCharacter.instance; var target = PuppetPos(_spectating);
                if (pc == null || target == null) return;
                _camShift = target.Value - pc.transform.position;
                t.position += _camShift;
            }
            catch { _camShift = Vector3.zero; }
        }

        // ---------------- per frame ----------------

        public static void Tick(Net net, string scene)
        {
            _net = net; _scene = scene;
            if (net == null || !net.Connected || net.LocalId == 255) { if (Downed) StandUp(null, false); _downedPlayers.Clear(); return; }
            foreach (var id in new List<byte>(_downedPlayers)) if (!net.Players.ContainsKey(id)) _downedPlayers.Remove(id);

            if (Downed) TickDowned(net);
            else TickReviver(net);
        }

        static void TickDowned(Net net)
        {
            var pc = PlayerCharacter.instance;
            PlayerCharacter._cutsceneLocked = true;
            try { if (pc != null && pc.hp < 1) pc.hp = 1; } catch { }

            // everyone down → the session's real death
            if (!AnyTeammateUp()) { CoopUI.Toast("Everyone is down…", CoopUI.Kind.Bad); ReallyDie(); return; }

            // keep spectating someone valid in this scene
            var cands = SpectateCandidates();
            if (_spectating == 255 || !cands.Contains(_spectating)) _spectating = cands.Count > 0 ? cands[0] : (byte)255;
            bool left = false, right = false, give = false;
            try { left = Input.GetKeyDown(KeyCode.LeftArrow); right = Input.GetKeyDown(KeyCode.RightArrow); give = Input.GetKey(KeyCode.F2); } catch { }
            if (left) Cycle(-1); if (right) Cycle(+1);
            _giveUpHeld = give ? _giveUpHeld + Time.unscaledDeltaTime : 0f;
            if (_giveUpHeld >= 1.5f) { CoopUI.Toast("Respawning at your campfire"); ReallyDie(); }
        }

        public static void Cycle(int dir)
        {
            var cands = SpectateCandidates();
            if (cands.Count == 0) { _spectating = 255; return; }
            int i = cands.IndexOf(_spectating);
            _spectating = cands[((i < 0 ? 0 : i + dir) % cands.Count + cands.Count) % cands.Count];
        }

        static void TickReviver(Net net)
        {
            var pc = PlayerCharacter.instance;
            if (pc == null || _downedPlayers.Count == 0) { _reviveTarget = 255; _reviveProgress = 0; return; }
            byte best = 255; float bestD = ReviveRange * ReviveRange;
            foreach (var id in _downedPlayers)
            {
                var p = PuppetPos(id);
                if (p == null) continue;
                float d = (p.Value - pc.transform.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = id; }
            }
            if (best != _reviveTarget) { _reviveTarget = best; _reviveProgress = 0; }
            if (best == 255) return;
            _reviveProgress += Time.deltaTime / Mathf.Max(0.5f, Plugin.ReviveSeconds.Value);
            if (_reviveProgress >= 1f)
            {
                _reviveProgress = 0; _reviveTarget = 255;
                net.SendFlag(FlagKind.Revive, best.ToString(CultureInfo.InvariantCulture), net.LocalId.ToString(CultureInfo.InvariantCulture));
                _downedPlayers.Remove(best);
                CoopUI.Toast($"You revived {Name(best)}!", CoopUI.Kind.Good);
            }
        }

        // ---------------- transitions ----------------

        static void GoDown()
        {
            if (Downed) return;
            Downed = true; _downedAt = Time.unscaledTime; _giveUpHeld = 0;
            _prevLock = PlayerCharacter._cutsceneLocked;
            _spectating = 255;
            Plugin.L.LogInfo("[revive] downed");
            _net?.SendFlag(FlagKind.PlayerDown, _net.LocalId.ToString(CultureInfo.InvariantCulture), "");
            CoopUI.Toast("You're down! A teammate can revive you.", CoopUI.Kind.Bad);
        }

        /// <summary>Back on your feet (revived), or leaving the downed state for a real death.</summary>
        static void StandUp(string by, bool heal)
        {
            Downed = false; _spectating = 255; _camShift = Vector3.zero;
            PlayerCharacter._cutsceneLocked = _prevLock;
            var pc = PlayerCharacter.instance;
            if (heal && pc != null)
            {
                try { pc.hp = Mathf.Max(1, Mathf.RoundToInt(pc.maxhp * Mathf.Clamp01(Plugin.ReviveHealthPercent.Value / 100f))); } catch { }
            }
            _net?.SendFlag(FlagKind.PlayerUp, _net.LocalId.ToString(CultureInfo.InvariantCulture), "");
            if (by != null) { Plugin.L.LogInfo($"[revive] revived by {by}"); CoopUI.Toast($"{by} revived you!", CoopUI.Kind.Good); }
        }

        /// <summary>TUNIC's own death (campfire respawn, bloodstain).</summary>
        public static void ReallyDie()
        {
            StandUp(null, false);
            var pc = PlayerCharacter.instance;
            if (pc == null) return;
            _bypass = true;
            try { pc.IDamageable_ReceiveDamage(99999, 999, Vector3.zero, 0f, 0); }
            catch (Exception e) { Plugin.L.LogWarning("[revive] real death: " + e.Message); }
            finally { _bypass = false; }
            Plugin.L.LogInfo("[revive] real death");
        }

        // ---------------- remote events ----------------

        public static void OnPlayerDown(string key)
        {
            byte id = byte.Parse(key, CultureInfo.InvariantCulture);
            if (_net != null && id == _net.LocalId) return;
            _downedPlayers.Add(id);
            CoopUI.Toast($"{Name(id)} is down! Stand next to them to revive.", CoopUI.Kind.Warn, 7f);
        }

        public static void OnPlayerUp(string key)
        {
            byte id = byte.Parse(key, CultureInfo.InvariantCulture);
            _downedPlayers.Remove(id);
        }

        public static void OnRevive(string key, string value)
        {
            byte target = byte.Parse(key, CultureInfo.InvariantCulture), by = byte.Parse(value, CultureInfo.InvariantCulture);
            if (_net != null && target == _net.LocalId && Downed) StandUp(Name(by), true);
            else if (_net != null && by != _net.LocalId) { _downedPlayers.Remove(target); CoopUI.Toast($"{Name(by)} revived {Name(target)}", CoopUI.Kind.Good); }
        }

        /// <summary>Test hook: revive a downed player as if standing next to them.</summary>
        public static void ForceReviveLocal() { if (Downed) StandUp("a test", true); }

        // ---------------- helpers ----------------

        static bool AnyTeammateUp()
        {
            if (_net == null) return false;
            foreach (var id in _net.Players.Keys)
                if (id != _net.LocalId && !_downedPlayers.Contains(id)) return true;
            return false;
        }

        static List<byte> SpectateCandidates()
        {
            var l = new List<byte>();
            foreach (var p in PuppetRegistry.All())
                if (p.PlayerId != 250 && p.gameObject.activeSelf && p.Latest != null && p.Latest.Scene == _scene && !_downedPlayers.Contains(p.PlayerId))
                    l.Add(p.PlayerId);
            l.Sort();
            return l;
        }

        static Vector3? PuppetPos(byte id)
        {
            foreach (var p in PuppetRegistry.All()) if (p.PlayerId == id && p.gameObject.activeSelf) return p.transform.position;
            return null;
        }

        public static string Name(byte id) => _net != null && _net.Players.TryGetValue(id, out var n) ? n : $"Player {id + 1}";

        /// <summary>Teammates who are up but somewhere else (for the downed banner).</summary>
        public static List<string> ElsewhereUp()
        {
            var l = new List<string>();
            if (_net == null) return l;
            foreach (var kv in _net.Players)
            {
                if (kv.Key == _net.LocalId || _downedPlayers.Contains(kv.Key)) continue;
                Puppet p = null; foreach (var q in PuppetRegistry.All()) if (q.PlayerId == kv.Key) p = q;
                string sc = p?.Latest?.Scene;
                if (sc != null && sc != _scene) l.Add($"{kv.Value} ({GameInfo.FriendlyArea(sc)})");
            }
            return l;
        }

        public static string Describe() => $"downed={Downed} spectating={_spectating} downedPlayers=[{string.Join(",", _downedPlayers)}] revive={_reviveTarget}:{_reviveProgress:F2} camShift={_camShift}";
    }
}
