using System.Collections.Generic;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>Queries over CoopManager's puppets, kept outside the IL2CPP-injected class (see note there).</summary>
    public static class PuppetRegistry
    {
        public static List<Puppet> All()
        {
            var l = new List<Puppet>();
            var m = CoopManager.Instance;
            if (m != null) foreach (var p in m._puppets.Values) if (p != null) l.Add(p);
            return l;
        }

        public static Vector3? Position(byte id)
        {
            var m = CoopManager.Instance;
            return m != null && m._puppets.TryGetValue(id, out var p) && p != null && p.gameObject.activeSelf
                ? p.transform.position : (Vector3?)null;
        }
    }
}
