using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace TunicCoop
{
    /// <summary>One frame of a player's visible state. Wire format is Write()/Read() below.</summary>
    public class Snapshot
    {
        public float Time;                 // sender's Time.unscaledTime when captured
        public string Scene;
        public Vector3 Pos;
        public float Yaw;
        public int Hp, MaxHp;
        public readonly List<int> LayerHash = new List<int>();
        public readonly List<float> LayerTime = new List<float>();   // normalized time
        public readonly List<float> LayerLen = new List<float>();    // state length (s)
        public readonly List<bool> LayerLoop = new List<bool>();
        public readonly List<int> ParamHash = new List<int>();
        public readonly List<float> ParamVal = new List<float>();
        public float AnimSpeed = 1f;

        /// <summary>Seconds into the layer's current state at capture time.</summary>
        public float StateSeconds(int i)
        {
            float n = LayerTime[i];
            n = LayerLoop[i] ? n - Mathf.Floor(n) : Mathf.Min(n, 1f);
            return n * LayerLen[i];
        }

        public void Write(NetDataWriter w)
        {
            w.Put(Time);
            w.Put(Scene ?? ""); w.Put(Pos.x); w.Put(Pos.y); w.Put(Pos.z); w.Put(Yaw);
            w.Put((short)Hp); w.Put((short)MaxHp); w.Put(AnimSpeed);
            w.Put((byte)LayerHash.Count);
            for (int i = 0; i < LayerHash.Count; i++) { w.Put(LayerHash[i]); w.Put(LayerTime[i]); w.Put(LayerLen[i]); w.Put(LayerLoop[i]); }
            w.Put((byte)ParamHash.Count);
            for (int i = 0; i < ParamHash.Count; i++) { w.Put(ParamHash[i]); w.Put(ParamVal[i]); }
        }

        public static Snapshot Read(NetDataReader r)
        {
            var s = new Snapshot
            {
                Time = r.GetFloat(),
                Scene = r.GetString(64),
                Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
                Yaw = r.GetFloat(),
                Hp = r.GetShort(), MaxHp = r.GetShort(), AnimSpeed = r.GetFloat(),
            };
            int nl = r.GetByte();
            for (int i = 0; i < nl; i++)
            {
                s.LayerHash.Add(r.GetInt()); s.LayerTime.Add(r.GetFloat()); s.LayerLen.Add(r.GetFloat()); s.LayerLoop.Add(r.GetBool());
            }
            int np = r.GetByte();
            for (int i = 0; i < np; i++) { s.ParamHash.Add(r.GetInt()); s.ParamVal.Add(r.GetFloat()); }
            return s;
        }

        /// <summary>Cheap fingerprint of the local fox's animator states; a change means "send now".</summary>
        public static int LocalStateKey()
        {
            var pc = PlayerCharacter.instance;
            if (pc == null) return 0;
            var anim = pc.cachedAnimator;
            if (anim == null || !anim.isActiveAndEnabled) return 0;
            unchecked
            {
                int h = 17, layers = Math.Min(anim.layerCount, 4);
                for (int i = 0; i < layers; i++)
                {
                    h = h * 31 + anim.GetCurrentAnimatorStateInfo(i).fullPathHash;
                    if (anim.IsInTransition(i)) h = h * 31 + anim.GetNextAnimatorStateInfo(i).fullPathHash;
                }
                return h;
            }
        }

        /// <summary>Capture the local fox. Returns null when there is no player (menus, loading).</summary>
        public static Snapshot CaptureLocal(string scene)
        {
            var pc = PlayerCharacter.instance;
            if (pc == null) return null;
            var t = pc.transform;
            var s = new Snapshot { Time = UnityEngine.Time.unscaledTime, Scene = scene, Pos = t.position, Yaw = t.eulerAngles.y };
            try { s.Hp = pc.hp; s.MaxHp = pc.maxhp; } catch { }
            var anim = pc.cachedAnimator != null ? pc.cachedAnimator : pc.GetComponentInChildren<Animator>();
            if (anim != null && anim.isActiveAndEnabled)
            {
                int layers = Math.Min(anim.layerCount, 4);
                for (int i = 0; i < layers; i++)
                {
                    // Mid-transition, report where the layer is heading: that is what the puppet should play.
                    var st = anim.IsInTransition(i) ? anim.GetNextAnimatorStateInfo(i) : anim.GetCurrentAnimatorStateInfo(i);
                    s.LayerHash.Add(st.fullPathHash); s.LayerTime.Add(st.normalizedTime);
                    s.LayerLen.Add(st.length); s.LayerLoop.Add(st.loop);
                }
                foreach (var p in anim.parameters)
                {
                    float v;
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Float: v = anim.GetFloat(p.nameHash); break;
                        case AnimatorControllerParameterType.Int: v = anim.GetInteger(p.nameHash); break;
                        case AnimatorControllerParameterType.Bool: v = anim.GetBool(p.nameHash) ? 1f : 0f; break;
                        default: continue; // triggers are covered by layer-state sync
                    }
                    s.ParamHash.Add(p.nameHash); s.ParamVal.Add(v);
                    if (s.ParamHash.Count >= 64) break;
                }
                s.AnimSpeed = anim.speed;
            }
            return s;
        }
    }

    /// <summary>A remote player's fox: visuals + animator only, no gameplay components.</summary>
    public class Puppet : MonoBehaviour
    {
        public Puppet(IntPtr ptr) : base(ptr) { }

        public byte PlayerId;
        public string PlayerName;
        public Snapshot Latest;
        Animator _anim;
        Dictionary<int, AnimatorControllerParameterType> _paramTypes;
        float[] _mismatchSince = { -1, -1, -1, -1 };

        static GameObject _holder;

        public static Puppet Spawn(byte id, string name)
        {
            GameObject prefab = FindPrefab();
            if (prefab == null) { Plugin.L.LogWarning("[puppet] no player prefab available yet"); return null; }

            if (_holder == null)
            {
                _holder = new GameObject("TunicCoop_PuppetHolder");
                _holder.SetActive(false); // children instantiated here never Awake
                Object.DontDestroyOnLoad(_holder);
            }

            var go = Object.Instantiate(prefab, _holder.transform);
            go.name = $"TunicCoop_Player{id}";
            Strip(go);
            go.transform.SetParent(null, false);
            Object.DontDestroyOnLoad(go);
            var pup = go.AddComponent<Puppet>();
            pup.PlayerId = id; pup.PlayerName = name;
            go.SetActive(false); // shown once a snapshot in our scene arrives
            Plugin.L.LogInfo($"[puppet] spawned puppet for player {id} '{name}'");
            return pup;
        }

        static GameObject FindPrefab()
        {
            try
            {
                string path = PlayerCharacter.kCharacterPrefabPath;
                if (!string.IsNullOrEmpty(path))
                {
                    var p = Resources.Load(path);
                    var go = p != null ? p.TryCast<GameObject>() : null;
                    if (go != null) return go;
                }
            }
            catch (Exception e) { Plugin.L.LogDebug("[puppet] prefab path load failed: " + e.Message); }
            try
            {
                var p = Resources.Load("_Fox");
                var go = p != null ? p.TryCast<GameObject>() : null;
                if (go != null) return go;
            }
            catch { }
            // Fallback: clone the live local player (still under the inactive holder, so no Awake).
            var pc = PlayerCharacter.instance;
            return pc != null ? pc.gameObject : null;
        }

        static void Strip(GameObject go)
        {
            // Several passes: RequireComponent can block destroying a component before its dependents.
            for (int pass = 0; pass < 4; pass++)
            {
                int left = 0;
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    try { Object.DestroyImmediate(mb); } catch { left++; }
                }
                if (left == 0) break;
            }
            foreach (var a in go.GetComponentsInChildren<NavMeshAgent>(true)) Object.DestroyImmediate(a);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.detectCollisions = false; }
            foreach (var cc in go.GetComponentsInChildren<CharacterController>(true)) cc.enabled = false;
        }

        void Awake()
        {
            _anim = GetComponentInChildren<Animator>(true);
            if (_anim != null)
            {
                // Root motion is consumed (and discarded) by RootMotionSink; position comes from the network.
                _anim.applyRootMotion = true;
                if (_anim.GetComponent<RootMotionSink>() == null) _anim.gameObject.AddComponent<RootMotionSink>();
                _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _paramTypes = new Dictionary<int, AnimatorControllerParameterType>();
                foreach (var p in _anim.parameters) _paramTypes[p.nameHash] = p.type;
            }
            _vis = new Dictionary<int, VisNode>();
            foreach (var n in Visibility.Index(transform, _anim))
            {
                _vis[n.Hash] = n;
                // Invisible until the owner tells us what is shown (avoids every item/effect at once).
                foreach (var r in n.Renderers) if (r != null) { r.enabled = false; if (Visibility.HiddenOnPuppet(n)) r.forceRenderingOff = true; }
                if (n.Ps != null) n.Ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        Dictionary<int, VisNode> _vis;
        bool _loggedVis;

        public void ApplyVis(List<KeyValuePair<int, byte>> state, bool full)
        {
            if (full && !_loggedVis)
            {
                int hit = 0; foreach (var kv in state) if (_vis.ContainsKey(kv.Key)) hit++;
                Plugin.L.LogInfo($"[puppet] {PlayerId}: visibility map {hit}/{state.Count} sender nodes matched ({_vis.Count} on puppet)");
                _loggedVis = true;
            }
            foreach (var kv in state) _visState[kv.Key] = kv.Value;
            Visibility.Apply(_vis, state);
        }

        // Full last-known visibility. Re-applied whenever the puppet reappears: re-activating a GameObject
        // restarts play-on-awake particle systems (the orbs), and unchanged nodes never get a new delta.
        readonly Dictionary<int, byte> _visState = new Dictionary<int, byte>();
        float _nextVisEnforce;

        readonly Dictionary<int, Visibility.AnimEntry> _animState = new Dictionary<int, Visibility.AnimEntry>();
        public void ApplyAnims(List<Visibility.AnimEntry> a)
        {
            foreach (var e in a) _animState[e.Node] = e;
            Visibility.ApplyAnims(_vis, a);
        }

        void ReapplyVis()
        {
            if (_animState.Count > 0) Visibility.ApplyAnims(_vis, _animState.Values);
            if (_visState.Count == 0) return;
            var all = new List<KeyValuePair<int, byte>>(_visState.Count);
            foreach (var kv in _visState) all.Add(kv);
            Visibility.Apply(_vis, all);
        }

        void OnEnable() { _reapplyNextFrame = true; }

        /// <summary>For tests: how many renderers/particle systems are currently showing.</summary>
        public string RenderersOn()
        {
            int r = 0, ps = 0;
            foreach (var n in _vis.Values)
            {
                if (n.T == null || !n.T.gameObject.activeInHierarchy) continue;
                foreach (var x in n.Renderers) if (x != null && x.enabled) r++;
                if (n.Ps != null && n.Ps.isEmitting) ps++;
            }
            return $"{{\"renderers\":{r},\"emitting\":{ps}}}";
        }
        bool _reapplyNextFrame;

        // ---------------- snapshot interpolation ----------------
        // We render the remote fox InterpDelay behind its owner's clock, blending between the two snapshots
        // around that moment. Position and animation states are read from the same timeline, so a roll's
        // animation starts exactly where its movement starts.
        const float InterpDelay = 0.1f;       // > 2 send intervals at 30 Hz, absorbs jitter
        const float MaxExtrapolate = 0.15f;
        readonly List<Snapshot> _buf = new List<Snapshot>();
        float _clockOffset;                   // local unscaledTime - sender time (latency + clock skew)
        bool _haveOffset;
        readonly int[] _appliedHash = new int[4];
        string _scene;

        public void Apply(Snapshot s, string localScene)
        {
            Latest = s;
            _scene = localScene;
            float sample = UnityEngine.Time.unscaledTime - s.Time;
            if (!_haveOffset || sample < _clockOffset || Mathf.Abs(sample - _clockOffset) > 1f)
            { _clockOffset = sample; _haveOffset = true; }       // fastest packet = best latency estimate
            else _clockOffset += (sample - _clockOffset) * 0.01f; // creep up slowly if latency grows

            // Insert in time order; ignore duplicates/out-of-date packets.
            int idx = _buf.Count;
            while (idx > 0 && _buf[idx - 1].Time > s.Time) idx--;
            if (idx > 0 && Mathf.Approximately(_buf[idx - 1].Time, s.Time)) return;
            _buf.Insert(idx, s);
            if (_buf.Count > 64) _buf.RemoveAt(0);

            bool same = s.Scene == localScene;
            if (gameObject.activeSelf != same)
            {
                gameObject.SetActive(same);
                if (same) { _buf.Clear(); _buf.Add(s); for (int i = 0; i < 4; i++) _appliedHash[i] = 0; }
            }
        }

        void Update()
        {
            // Re-assert visibility right after reappearing, then every 0.5 s (cheap; catches particle restarts).
            if (_reapplyNextFrame || UnityEngine.Time.unscaledTime >= _nextVisEnforce)
            {
                _reapplyNextFrame = false;
                _nextVisEnforce = UnityEngine.Time.unscaledTime + 0.5f;
                ReapplyVis();
            }
            if (_buf.Count == 0) return;
            float rt = UnityEngine.Time.unscaledTime - _clockOffset - InterpDelay;

            // Drop snapshots we no longer need (keep one before the render time).
            while (_buf.Count > 2 && _buf[1].Time <= rt) _buf.RemoveAt(0);

            Snapshot a = _buf[0], b = _buf.Count > 1 ? _buf[1] : null;
            Vector3 pos; float yaw;
            if (rt <= a.Time || b == null)
            {
                // Starved (or not started): hold, or extrapolate briefly from the last movement.
                pos = a.Pos; yaw = a.Yaw;
                if (b == null && _prev != null && rt > a.Time && a.Time > _prev.Time)
                {
                    float dt = Mathf.Min(rt - a.Time, MaxExtrapolate);
                    pos += (a.Pos - _prev.Pos) / (a.Time - _prev.Time) * dt;
                }
            }
            else
            {
                float u = Mathf.Clamp01((rt - a.Time) / Mathf.Max(1e-4f, b.Time - a.Time));
                bool teleport = (b.Pos - a.Pos).sqrMagnitude > 25f;
                pos = teleport ? (u < 0.5f ? a.Pos : b.Pos) : Vector3.Lerp(a.Pos, b.Pos, u);
                yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, u);
            }
            if (b != null) _prev = a;

            var t = transform;
            t.position = pos;
            t.rotation = Quaternion.Euler(0, yaw, 0);

            if (_anim == null || !_anim.isActiveAndEnabled) return;
            ApplyAnimator(a, b, rt);   // a = the sender's state "now" on the render timeline
        }

        Snapshot _prev;

        void ApplyAnimator(Snapshot a, Snapshot b, float rt)
        {
            _anim.speed = a.AnimSpeed;
            float u = b != null ? Mathf.Clamp01((rt - a.Time) / Mathf.Max(1e-4f, b.Time - a.Time)) : 0f;

            for (int i = 0; i < a.ParamHash.Count; i++)
            {
                if (!_paramTypes.TryGetValue(a.ParamHash[i], out var type)) continue;
                float v = a.ParamVal[i];
                switch (type)
                {
                    case AnimatorControllerParameterType.Float:
                        if (b != null && i < b.ParamHash.Count && b.ParamHash[i] == a.ParamHash[i]) v = Mathf.Lerp(v, b.ParamVal[i], u);
                        _anim.SetFloat(a.ParamHash[i], v); break;
                    case AnimatorControllerParameterType.Int: _anim.SetInteger(a.ParamHash[i], (int)v); break;
                    case AnimatorControllerParameterType.Bool: _anim.SetBool(a.ParamHash[i], v > 0.5f); break;
                }
            }

            int layers = Math.Min(Math.Min(a.LayerHash.Count, _anim.layerCount), 4);
            for (int i = 0; i < layers; i++)
            {
                int target = a.LayerHash[i];
                var curInfo = _anim.GetCurrentAnimatorStateInfo(i);
                bool inTrans = _anim.IsInTransition(i);
                bool already = curInfo.fullPathHash == target || (inTrans && _anim.GetNextAnimatorStateInfo(i).fullPathHash == target);

                if (target != _appliedHash[i])
                {
                    // Sender entered a new state (roll, attack...): blend in at the right point of the clip.
                    _appliedHash[i] = target;
                    _mismatchSince[i] = -1;
                    if (already) continue;
                    float offset = a.StateSeconds(i) + Mathf.Max(0f, rt - a.Time) * a.AnimSpeed;
                    _anim.CrossFadeInFixedTime(target, 0.06f, i, offset);
                    continue;
                }
                // Same target, but our controller drifted elsewhere (params fired a transition): correct it.
                if (already) { _mismatchSince[i] = -1; continue; }
                if (_mismatchSince[i] < 0) _mismatchSince[i] = UnityEngine.Time.unscaledTime;
                else if (UnityEngine.Time.unscaledTime - _mismatchSince[i] > 0.2f)
                {
                    float offset = a.StateSeconds(i) + Mathf.Max(0f, rt - a.Time) * a.AnimSpeed;
                    _anim.CrossFadeInFixedTime(target, 0.1f, i, offset);
                    _mismatchSince[i] = -1;
                }
            }
        }
    }
}
