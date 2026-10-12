using System;
using System.Collections.Generic;
using System.Globalization;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// TUNIC saves an opened chest as SaveFile int "chest open &lt;chestID&gt;" = 1, but only writes it when the scene
    /// is saved (verified: absent seconds after opening), so the store poll can't see it live. Instead: watch
    /// Chest.opening (10x/s) and send the chest's stable id. Receivers set the key (persists on their save) and,
    /// if the chest is in their scene, re-run its Awake so it reads the key and shows as open. Contents travel
    /// separately: items via shared inventory, money spray via CoinSync — receivers never get the reward twice.
    /// </summary>
    public static class ChestSync
    {
        const float PollInterval = 0.1f, RescanInterval = 2f;
        static readonly List<Chest> _chests = new List<Chest>();
        static readonly HashSet<int> _announced = new HashSet<int>();
        static readonly HashSet<IntPtr> _hooked = new HashSet<IntPtr>();
        static string _scene;
        static float _nextPoll, _nextScan;

        public static string Key(int chestId) => "chest open " + chestId.ToString(CultureInfo.InvariantCulture);

        public static void Tick(Net net, string scene)
        {
            float now = Time.unscaledTime;
            if (now < _nextPoll) return;
            _nextPoll = now + PollInterval;
            if (net == null || !net.Connected || net.LocalId == 255 || PlayerCharacter.instance == null) return;
            if (scene != _scene) { _scene = scene; _announced.Clear(); Rescan(); }
            else if (now >= _nextScan) Rescan();

            foreach (var ch in _chests)
            {
                if (ch == null) continue;
                bool opening;
                try { opening = ch.opening; } catch { continue; }
                if (opening) Opened(ch.chestID, "poll");
            }
            while (_pending.Count > 0) { var id = _pending.Dequeue(); Announce(net, scene, id); }
        }

        static readonly Queue<int> _pending = new Queue<int>();

        /// <summary>Any detector (poll, OnOpen event, Interact hook) reports here; first report wins.</summary>
        public static void Opened(int chestId, string via)
        {
            if (_announced.Contains(chestId) || _pending.Contains(chestId)) return;
            Plugin.L.LogInfo($"[chest] chest {chestId} opened (detected via {via})");
            _pending.Enqueue(chestId);
        }

        static void Announce(Net net, string scene, int id)
        {
            if (!_announced.Add(id)) return;
            net.SendFlag(FlagKind.ChestOpen, scene + "|" + id.ToString(CultureInfo.InvariantCulture), "");
        }

        // ---- native hook on the open coroutine (Harmony patches never fire here; see NativeHook) ----
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        delegate byte MoveNextFn(IntPtr self, IntPtr methodInfo);
        static MoveNextFn _origMoveNext;

        // The iterator class name ("_openSequence_d__35") is compiler-generated and can differ between game
        // builds (other stores/versions), so it's resolved by reflection: a hard typeof() would make the whole
        // plugin fail to load on such a build.
        static Type _iterType;
        static System.Reflection.PropertyInfo _iterThis;

        public static void InstallHooks()
        {
            try
            {
                foreach (var t in typeof(Chest).GetNestedTypes())
                    if (t.Name.StartsWith("_openSequence_d__")) { _iterType = t; break; }
                if (_iterType == null) { Plugin.L.LogWarning("[chest] open-sequence iterator not found in this game build; chest sync falls back to save-key polling"); return; }
                _iterThis = _iterType.GetProperty("__4__this");
                var ptr = NativeHook.MethodPtr(_iterType, "NativeMethodInfoPtr_MoveNext_Private_Virtual_Final_New_Boolean_0");
                _origMoveNext = NativeHook.Install<MoveNextFn>("Chest.openSequence.MoveNext", ptr, OpenSequenceMoveNext);
            }
            catch (Exception e) { Plugin.L.LogError("[chest] hook install failed (chest sync falls back to save-key polling): " + e.Message); }
        }

        static byte OpenSequenceMoveNext(IntPtr self, IntPtr methodInfo)
        {
            try
            {
                var it = Activator.CreateInstance(_iterType, self);
                var ch = _iterThis?.GetValue(it) as Chest;
                if (ch != null) Opened(ch.chestID, "openSequence");
            }
            catch (Exception e) { Plugin.L.LogWarning("[chest] openSequence hook: " + e.Message); }
            return _origMoveNext(self, methodInfo);
        }
        static void Rescan()
        {
            _nextScan = Time.unscaledTime + RescanInterval;
            _chests.Clear();
            try
            {
                foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Chest>()))
                {
                    var ch = obj.TryCast<Chest>();
                    if (ch == null) continue;
                    _chests.Add(ch);
                    if (_hooked.Add(ch.Pointer))
                    {
                        int id = ch.chestID;
                        var d = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => Opened(id, "OnOpen")));
                        ch.OnOpen = ch.OnOpen == null ? d : Il2CppSystem.Delegate.Combine(ch.OnOpen, d).Cast<Il2CppSystem.Action>();
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[chest] scan failed: " + e.Message); }
        }

        public static void Apply(string key, string localScene)
        {
            var p = key.Split('|');
            if (p.Length != 2) return;
            int id = int.Parse(p[1], CultureInfo.InvariantCulture);
            try
            {
                SaveFile.SetInt(Key(id), 1);          // persists on this player's save too
                _announced.Add(id);                    // don't echo
                if (p[0] != localScene) { Plugin.L.LogInfo($"[chest] remote opened chest {id} in {p[0]} (other scene)"); return; }
                if (_scene != localScene || _chests.Count == 0) { _scene = localScene; Rescan(); }
                foreach (var ch in _chests)
                {
                    if (ch == null || ch.chestID != id) continue;
                    if (ch.opening) break;             // already opening here
                    try { ch.Awake(); } catch (Exception e) { Plugin.L.LogWarning("[chest] re-Awake: " + e.Message); }
                    Plugin.L.LogInfo($"[chest] remote opened chest {id}: shown open");
                    return;
                }
                Plugin.L.LogInfo($"[chest] remote opened chest {id}: not found in scene");
            }
            catch (Exception e) { Plugin.L.LogWarning("[chest] apply failed: " + e.Message); }
        }
    }
}
