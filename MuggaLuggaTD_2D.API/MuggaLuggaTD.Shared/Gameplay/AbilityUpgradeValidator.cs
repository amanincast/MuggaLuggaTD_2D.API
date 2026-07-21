using System;
using System.Collections.Generic;
using System.Linq;
using Abilities.Models;
using StateManagement.Models;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// Decides whether an applied ability upgrade is one the game actually offers.
    ///
    /// Upgrades are chosen on level-up from a fixed per-ability pool in AbilityUpgradeData and drive
    /// ability damage — which feeds PvP power. A client that persisted an upgrade outside that pool,
    /// or the same upgrade with an inflated value, would inflate the roster the server computes power
    /// from. So legality is checked against the content pool on the way in, not trusted.
    ///
    /// Matching is on the modifier set, not the name: a renamed or re-described upgrade is still legal
    /// if its actual effect matches a pool entry, and an inflated value fails because the effect no
    /// longer matches anything offered. The client copies the modifier verbatim from content when it
    /// applies an upgrade, so a legitimate save's values are identical to the pool's.
    /// </summary>
    public static class AbilityUpgradeValidator
    {
        /// <summary>
        /// True when <paramref name="applied"/> matches some upgrade in <paramref name="pool"/> by its
        /// modifier set. An empty modifier set is never legal (nothing to grant).
        /// </summary>
        public static bool IsLegal(AbilityUpgradeSaveData applied, IReadOnlyCollection<AbilityUpgrade> pool)
        {
            if (applied?.Modifiers == null || applied.Modifiers.Count == 0 || pool == null)
                return false;

            var appliedSignature = Signature(applied.Modifiers.Select(m => ModifierKey(
                m.UpgradeModifierType, m.Value, m.AffinityType, m.Property)));

            return pool.Any(candidate =>
                candidate?.Modifiers != null
                && candidate.Modifiers.Count > 0
                && Signature(candidate.Modifiers.Select(m => ModifierKey(
                    m.UpgradeModifierType, m.Value, m.AffinityType, m.Property))) == appliedSignature);
        }

        /// <summary>A single modifier as a stable, comparable string.</summary>
        private static string ModifierKey(Enums.AbilityUpgradeModifierTypes type, double? value,
            Enums.AffinityTypes? affinity, string property)
        {
            var affinityPart = affinity.HasValue ? ((int)affinity.Value).ToString() : "-";
            // "R" round-trips a double to the same string on both sides for the same value, so an
            // inflated value produces a different key and fails to match.
            var valuePart = value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
            return $"{(int)type}|{affinityPart}|{property ?? "-"}|{valuePart}";
        }

        /// <summary>Order-independent signature of a modifier set: sorted keys joined together.</summary>
        private static string Signature(IEnumerable<string> modifierKeys)
            => string.Join(";", modifierKeys.OrderBy(k => k, StringComparer.Ordinal));
    }
}
