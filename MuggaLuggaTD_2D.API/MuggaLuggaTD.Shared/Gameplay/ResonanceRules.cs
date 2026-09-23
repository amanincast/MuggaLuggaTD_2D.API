using System.Collections.Generic;
using System.Linq;
using Enums;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What a party built around one affinity gets for it. Design doc 05 section 3.
    ///
    /// <para>This is the reason a player goes looking for a <i>particular</i> recruit rather than the
    /// strongest one: three Water signatures are worth more together than three unrelated better ones.
    /// It is the demand side of the Tavern.</para>
    ///
    /// <para><b>A pure function over the party's rolls</b>, with no Unity types, so the Guild Hall can
    /// show a party's resonance before the fight and the combat scene can apply the same numbers
    /// during it. It deliberately does <b>not</b> feed
    /// <see cref="PartyPowerCalculator"/>: whether a garrison's composition counts for hold is left
    /// open by the design (section 3, "optional and decided later"), and until that is decided the
    /// server prices a roster without it.</para>
    /// </summary>
    public static class ResonanceRules
    {
        /// <summary>Below this many sharing an affinity, nothing happens.</summary>
        public const int MinimumToResonate = 2;

        /// <summary>At this many, the affinity's status also spreads on application.</summary>
        public const int AttunementAt = 4;

        /// <summary>At this many, the affinity's status lasts longer.</summary>
        public const int LongerStatusAt = 3;

        /// <summary>How much longer, once <see cref="LongerStatusAt"/> is reached.</summary>
        public const float LongerStatusMultiplier = 1.5f;

        /// <summary>
        /// The bonus each step is worth: two sharing is +10%, three +20%, four +30%. Anything above
        /// four is capped there, because the party is four.
        /// </summary>
        public static float DamageBonusFor(int sharing)
        {
            if (sharing < MinimumToResonate) return 0f;
            return System.Math.Min(sharing, AttunementAt) switch
            {
                2 => 0.10f,
                3 => 0.20f,
                _ => 0.30f
            };
        }

        /// <summary>
        /// The resonances a party has, one per affinity that at least
        /// <see cref="MinimumToResonate"/> of its members share. A party of four unrelated rolls
        /// resonates with nothing; a party of two Water and two Fire resonates with both.
        /// </summary>
        public static IReadOnlyList<ResonanceBand> Assess(IEnumerable<AffinityTypes?> partyAffinities)
        {
            if (partyAffinities == null)
                return new List<ResonanceBand>();

            return partyAffinities
                .Where(a => a.HasValue)
                .GroupBy(a => a.Value)
                .Where(g => g.Count() >= MinimumToResonate)
                .Select(g => new ResonanceBand(g.Key, g.Count()))
                .OrderByDescending(b => b.Sharing)
                .ThenBy(b => b.Affinity)
                .ToList();
        }

        /// <summary>The band for one affinity, or null when the party does not resonate with it.</summary>
        public static ResonanceBand For(IEnumerable<AffinityTypes?> partyAffinities, AffinityTypes affinity)
            => Assess(partyAffinities).FirstOrDefault(b => b.Affinity == affinity);
    }

    /// <summary>One affinity the party shares, and what that is worth.</summary>
    public sealed class ResonanceBand
    {
        public ResonanceBand(AffinityTypes affinity, int sharing)
        {
            Affinity = affinity;
            Sharing = sharing;
        }

        public AffinityTypes Affinity { get; }

        /// <summary>How many of the party carry this as their signature affinity.</summary>
        public int Sharing { get; }

        /// <summary>Added to damage the party deals <b>of this affinity only</b>.</summary>
        public float DamageBonus => ResonanceRules.DamageBonusFor(Sharing);

        /// <summary>Multiplies how long this affinity's status lasts when the party applies it.</summary>
        public float StatusDurationMultiplier
            => Sharing >= ResonanceRules.LongerStatusAt ? ResonanceRules.LongerStatusMultiplier : 1f;

        /// <summary>
        /// Attunement: at four, this affinity's status spreads to one nearby enemy when it lands.
        /// </summary>
        public bool Attunes => Sharing >= ResonanceRules.AttunementAt;
    }
}
