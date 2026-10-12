using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TunicCoop
{
    /// <summary>
    /// Gameplay rules for the whole session. The HOST's config decides; clients receive them on join (and on
    /// every change) so everybody plays by the same rules. Offline, your own config applies.
    /// </summary>
    public static class HostSettings
    {
        // Effective values (read these, not the config entries).
        public static bool Revive = true;
        public static float ReviveSeconds = 3f;
        public static int ReviveHealthPercent = 50;
        public static int BleedOutSeconds;              // 0 = downed players wait forever
        public static int EnemyHealthPerExtraPlayer;    // % tougher per extra player, 0 = vanilla
        public static bool ShareMoneyAndConsumables = true;

        static bool _fromHost;
        public static bool FromHost => _fromHost;

        /// <summary>Use our own config (offline / hosting).</summary>
        public static void LoadLocal()
        {
            _fromHost = false;
            Revive = Plugin.Revive.Value;
            ReviveSeconds = Math.Max(0.5f, Plugin.ReviveSeconds.Value);
            ReviveHealthPercent = Plugin.ReviveHealthPercent.Value;
            BleedOutSeconds = Plugin.BleedOutSeconds.Value;
            EnemyHealthPerExtraPlayer = Plugin.EnemyHealthPerExtraPlayer.Value;
            ShareMoneyAndConsumables = Plugin.ShareMoneyAndConsumables.Value;
        }

        public static string Serialize()
        {
            var c = CultureInfo.InvariantCulture;
            return string.Join(";", new[]
            {
                "revive=" + (Revive ? "1" : "0"),
                "reviveSeconds=" + ReviveSeconds.ToString("R", c),
                "reviveHealth=" + ReviveHealthPercent.ToString(c),
                "bleedOut=" + BleedOutSeconds.ToString(c),
                "enemyHealth=" + EnemyHealthPerExtraPlayer.ToString(c),
                "share=" + (ShareMoneyAndConsumables ? "1" : "0"),
            });
        }

        /// <summary>Client: apply the host's rules. Unknown keys are ignored (newer host).</summary>
        public static void Apply(string s)
        {
            var c = CultureInfo.InvariantCulture;
            bool shareBefore = ShareMoneyAndConsumables;
            foreach (var part in (s ?? "").Split(';'))
            {
                int eq = part.IndexOf('='); if (eq <= 0) continue;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                try
                {
                    switch (k)
                    {
                        case "revive": Revive = v == "1"; break;
                        case "reviveSeconds": ReviveSeconds = Math.Max(0.5f, float.Parse(v, c)); break;
                        case "reviveHealth": ReviveHealthPercent = Math.Max(1, Math.Min(100, int.Parse(v, c))); break;
                        case "bleedOut": BleedOutSeconds = Math.Max(0, int.Parse(v, c)); break;
                        case "enemyHealth": EnemyHealthPerExtraPlayer = Math.Max(0, Math.Min(300, int.Parse(v, c))); break;
                        case "share": ShareMoneyAndConsumables = v == "1"; break;
                    }
                }
                catch { }
            }
            _fromHost = true;
            Plugin.L.LogInfo("[settings] host rules: " + Serialize());
            if (shareBefore != ShareMoneyAndConsumables) WorldSync.Reset();   // re-baseline inventory under the new rule
        }

        /// <summary>One line for the UI.</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Revive ? $"Revive on ({ReviveSeconds:0.#} s, {ReviveHealthPercent}% HP{(BleedOutSeconds > 0 ? $", bleed-out {BleedOutSeconds} s" : "")})" : "Revive off");
            sb.Append("  ·  ").Append(ShareMoneyAndConsumables ? "Shared money & consumables" : "Own money & consumables");
            sb.Append("  ·  ").Append(EnemyHealthPerExtraPlayer > 0 ? $"Enemies +{EnemyHealthPerExtraPlayer}% HP per extra player" : "Vanilla enemy health");
            return sb.ToString();
        }

        /// <summary>Item types that stay per player when sharing is off.</summary>
        public static bool IsPersonal(Item it)
        {
            if (ShareMoneyAndConsumables || it == null) return false;
            try
            {
                var t = it.Type;
                return t == Item.ItemType.MONEY || t == Item.ItemType.SUPPLIES || t == Item.ItemType.WELL_SUPPLIES || t == Item.ItemType.FLASK;
            }
            catch { return false; }
        }

        public static bool IsPersonal(string itemName)
        {
            if (ShareMoneyAndConsumables) return false;
            try { return IsPersonal(Inventory.GetItemByName(itemName)); } catch { return false; }
        }
    }
}
