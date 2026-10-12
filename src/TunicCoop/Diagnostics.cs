using System;
using System.Text;
using UnityEngine;

namespace TunicCoop
{
    /// <summary>F11: write every monster in the current scene to the log. Run on two PCs in the same scene and
    /// diff, to check that RuntimeStableIDs line up across machines (the basis for enemy sync).</summary>
    public static class Diagnostics
    {
        public static void DumpMonsters(string scene)
        {
            var sb = new StringBuilder();
            int n = 0;
            try
            {
                var mm = MonsterManager.instance;
                var list = mm != null ? mm.monsters : null;
                if (list == null) { Plugin.L.LogInfo($"[diag] {scene}: no MonsterManager"); return; }
                foreach (var m in list)
                {
                    if (m == null) continue;
                    n++;
                    string id = "?", intra = "?";
                    try { var rs = m.runtimeStableID; if (rs != null) { id = rs.HasValidID ? rs.ID.ToString() : "invalid"; intra = rs.intraSceneID.ToString(); } } catch { }
                    string hp = "?";
                    try { hp = $"{m.hp}/{m.maxhp}"; } catch { }
                    var p = m.transform.position;
                    sb.AppendLine($"  id={id} intra={intra} type={m.GetIl2CppType().Name} name='{m.gameObject.name}' pos=({p.x:F1},{p.y:F1},{p.z:F1}) hp={hp} dead={SafeDead(m)}");
                }
            }
            catch (Exception e) { sb.AppendLine("  error: " + e.Message); }
            Plugin.L.LogInfo($"[diag] {scene}: {n} monsters\n{sb}");
        }

        static string SafeDead(Monster m) { try { return m.IsDead.ToString(); } catch { return "?"; } }
    }
}
