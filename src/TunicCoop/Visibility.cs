using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>
    /// The player prefab carries every weapon, item and effect as children; game scripts toggle them.
    /// Puppets have no scripts, so we mirror the real fox: per child path → active / renderer on / emitting.
    /// Both sides come from the same prefab, so relative paths (hashed) line up.
    /// </summary>
    public class VisNode
    {
        public int Hash;
        public Transform T;
        public Renderer[] Renderers;
        public ParticleSystem Ps;
        public Animator Anim;      // secondary animator on this node (equipment/effects); null for the main body
        public string Path;
    }

    public static class Visibility
    {
        public const byte Active = 1, Rendered = 2, Emitting = 4;

        public static List<VisNode> Index(Transform root, Animator main = null)
        {
            var list = new List<VisNode>();
            Walk(root, "", list, true);
            foreach (var n in list) { var a = n.T.GetComponent<Animator>(); if (a != null && (main == null || a.Pointer != main.Pointer)) n.Anim = a; }
            return list;
        }

        static void Walk(Transform t, string path, List<VisNode> list, bool isRoot)
        {
            if (!isRoot)
            {
                var rs = t.GetComponents<Renderer>();
                var arr = new Renderer[rs.Length];
                for (int i = 0; i < rs.Length; i++) arr[i] = rs[i];
                list.Add(new VisNode { Hash = Fnv(path), T = t, Renderers = arr, Ps = t.GetComponent<ParticleSystem>(), Path = path });
            }
            int n = t.childCount;
            for (int i = 0; i < n; i++)
            {
                var c = t.GetChild(i);
                // Runtime-spawned clones get "(Clone)" names; they don't exist on the puppet anyway.
                Walk(c, path + "/" + c.name, list, false);
            }
        }

        public static byte Flags(VisNode n)
        {
            byte f = 0;
            if (n.T == null) return 0;
            if (n.T.gameObject.activeSelf) f |= Active;
            // forceRenderingOff counts as hidden: the tether-wand tentacles are enabled on the real fox but forced off by
            // the wand script (verified: forcing them off on the puppet removed the blue blob).
            foreach (var r in n.Renderers) if (r != null && r.enabled && !r.forceRenderingOff) { f |= Rendered; break; }
            if (n.Ps != null && n.Ps.isEmitting) f |= Emitting;
            return f;
        }

        public static void Write(NetDataWriter w, List<VisNode> nodes)
        {
            w.Put((ushort)nodes.Count);
            foreach (var n in nodes) { w.Put(n.Hash); w.Put(Flags(n)); }
        }

        /// <summary>Signature of current flags, to detect changes cheaply.</summary>
        public static int Signature(List<VisNode> nodes)
        {
            unchecked
            {
                int h = 17;
                foreach (var n in nodes) h = h * 31 + Flags(n);
                return h;
            }
        }

        // Always hidden on puppets. The tether-wand wind-up tentacles are hidden on the real fox by a mechanism we
        // could not reproduce (checked: renderer flags, forceRenderingOff, bones, bounds, animator state, property
        // blocks, materials — all identical), and showed as a blue blob on puppets. Cost: others don't see the
        // wind-up flourish.
        static readonly string[] PuppetHidden = { "tetherwand_windup" };
        public static bool HiddenOnPuppet(VisNode n) { foreach (var h in PuppetHidden) if (n.Path != null && n.Path.Contains(h)) return true; return false; }

        public static void Apply(Dictionary<int, VisNode> byHash, List<KeyValuePair<int, byte>> state)
        {
            foreach (var kv in state)
            {
                if (!byHash.TryGetValue(kv.Key, out var n) || n.T == null) continue;
                bool active = (kv.Value & Active) != 0;
                if (n.T.gameObject.activeSelf != active) n.T.gameObject.SetActive(active);
                bool rendered = (kv.Value & Rendered) != 0 && !HiddenOnPuppet(n);
                foreach (var r in n.Renderers)
                {
                    if (r == null) continue;
                    if (r.enabled != rendered) r.enabled = rendered;
                    if (r.forceRenderingOff == rendered) r.forceRenderingOff = !rendered;
                }
                if (n.Ps != null)
                {
                    bool emit = (kv.Value & Emitting) != 0;
                    if (emit && !n.Ps.isEmitting) n.Ps.Play(false);
                    // Stop AND clear: StopEmitting left long-lived arrival particles (a blue swirl) on puppets
                    // after the real fox's effect had ended (verified in Sword Cave). Also clears leftovers on the
                    // periodic re-apply.
                    else if (!emit && (n.Ps.isEmitting || n.Ps.particleCount > 0)) n.Ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }
        }

        // ---------- secondary animators (tether-wand tentacles etc.) ----------
        // Their state is set by game scripts the puppet doesn't have; unsynced they sat in the default (extended)
        // pose = a blue tentacle swirl on puppets (verified: same renderers + scale on both sides, only the
        // tetherwand_windup animators differed). Sync layer-0 state per node.

        public struct AnimEntry { public int Node, State; public float Norm; }
        static readonly Dictionary<int, int> _sentAnim = new Dictionary<int, int>();

        public static List<AnimEntry> LocalAnimDelta(bool full)
        {
            if (_local == null) return null;
            List<AnimEntry> l = null;
            foreach (var n in _local)
            {
                if (n.Anim == null || !n.Anim.isActiveAndEnabled) continue;
                var st = n.Anim.GetCurrentAnimatorStateInfo(0);
                if (!full && _sentAnim.TryGetValue(n.Hash, out var prev) && prev == st.fullPathHash) continue;
                _sentAnim[n.Hash] = st.fullPathHash;
                (l ??= new List<AnimEntry>()).Add(new AnimEntry { Node = n.Hash, State = st.fullPathHash, Norm = st.normalizedTime });
            }
            return l;
        }

        public static void WriteAnims(NetDataWriter w, List<AnimEntry> a)
        {
            w.Put((ushort)(a?.Count ?? 0));
            if (a != null) foreach (var e in a) { w.Put(e.Node); w.Put(e.State); w.Put(e.Norm); }
        }

        public static List<AnimEntry> ReadAnims(NetDataReader r)
        {
            var l = new List<AnimEntry>();
            if (r.AvailableBytes < 2) return l;
            int n = r.GetUShort();
            for (int i = 0; i < n; i++) l.Add(new AnimEntry { Node = r.GetInt(), State = r.GetInt(), Norm = r.GetFloat() });
            return l;
        }

        public static void ApplyAnims(Dictionary<int, VisNode> byHash, IEnumerable<AnimEntry> anims)
        {
            foreach (var e in anims)
            {
                if (!byHash.TryGetValue(e.Node, out var n) || n.Anim == null || !n.Anim.isActiveAndEnabled) continue;
                if (n.Anim.GetCurrentAnimatorStateInfo(0).fullPathHash != e.State) n.Anim.Play(e.State, 0, e.Norm % 1f);
            }
        }

        // ---------- local side: changes since last send ----------

        static System.IntPtr _indexedFor;
        static List<VisNode> _local;
        static readonly Dictionary<int, byte> _sent = new Dictionary<int, byte>();
        public static bool ForceFull;

        /// <summary>Entries that changed since the last call (all of them when full). Null if nothing to send.</summary>
        public static List<KeyValuePair<int, byte>> LocalDelta(PlayerCharacter pc, out bool full)
        {
            full = false;
            if (pc == null) return null;
            if (_local == null || _indexedFor != pc.Pointer)
            {
                _local = Index(pc.transform, pc.cachedAnimator);
                _sentAnim.Clear();
                _indexedFor = pc.Pointer;
                ForceFull = true;
                Plugin.L.LogInfo($"[sync] indexed local fox: {_local.Count} nodes");
            }
            full = ForceFull;
            ForceFull = false;
            List<KeyValuePair<int, byte>> outList = null;
            foreach (var n in _local)
            {
                byte f = Flags(n);
                if (!full && _sent.TryGetValue(n.Hash, out var prev) && prev == f) continue;
                _sent[n.Hash] = f;
                (outList ??= new List<KeyValuePair<int, byte>>()).Add(new KeyValuePair<int, byte>(n.Hash, f));
            }
            return outList;
        }

        public static void WriteEntries(NetDataWriter w, List<KeyValuePair<int, byte>> e)
        {
            w.Put((ushort)e.Count);
            foreach (var kv in e) { w.Put(kv.Key); w.Put(kv.Value); }
        }

        public static List<KeyValuePair<int, byte>> ReadEntries(NetDataReader r)
        {
            int n = r.GetUShort();
            var l = new List<KeyValuePair<int, byte>>(n);
            for (int i = 0; i < n; i++) l.Add(new KeyValuePair<int, byte>(r.GetInt(), r.GetByte()));
            return l;
        }

        static int Fnv(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s) { h ^= c; h *= 16777619; }
                return (int)h;
            }
        }
    }
}
