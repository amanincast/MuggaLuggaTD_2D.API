using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// The cost of a lost fight (<c>docs/design/auto-fight.md</c> §4; Mike, 2026-10-04).
    ///
    /// <para>A character is <b>Bloodied</b> for <see cref="Recovery"/> after a lost auto-fight, a lost
    /// ambush, an abandoned run, a lost raid or a repelled siege. Coming home by ransom is not one: the
    /// ransom is the cost (Mike).</para>
    ///
    /// <para>What it means depends on who is in charge of the fight (Mike, 2026-10-04):</para>
    /// <list type="bullet">
    /// <item>A fight the player <b>chose</b> takes a Bloodied hero at <see cref="Penalty"/> less: a
    /// hand-played run, ambush or siege assault (health and damage), and a raid or siege muster
    /// (their part of the march's power, <see cref="Weaken"/>).</item>
    /// <item>A fight nobody is steering <b>bars</b> them: auto mode, which rests until they recover
    /// and then carries on, and a garrison post.</item>
    /// </list>
    ///
    /// <para>Nothing ticks: the server stores when each member recovers and asks this rule.</para>
    /// </summary>
    public static class BloodiedRules
    {
        /// <summary>How long a Bloodied character needs to recover.</summary>
        public static readonly TimeSpan Recovery = TimeSpan.FromMinutes(30);

        /// <summary>How much less a Bloodied hero is worth in a fight the player chose: a quarter.</summary>
        public const double Penalty = 0.25;

        /// <summary>What a Bloodied hero's health and damage are multiplied by in a hand-played fight.</summary>
        public const double Strength = 1.0 - Penalty;

        /// <summary>When a character bloodied at <paramref name="lostAtUtc"/> is fit to fight again.</summary>
        public static DateTime RecoversAt(DateTime lostAtUtc) => lostAtUtc + Recovery;

        /// <summary>Whether a character recovering until <paramref name="recoversAtUtc"/> is still Bloodied.</summary>
        public static bool IsBloodied(DateTime? recoversAtUtc, DateTime utcNow) =>
            recoversAtUtc.HasValue && utcNow < recoversAtUtc.Value;

        /// <summary>What is left of the recovery, or zero.</summary>
        public static TimeSpan Remaining(DateTime? recoversAtUtc, DateTime utcNow) =>
            IsBloodied(recoversAtUtc, utcNow) ? recoversAtUtc!.Value - utcNow : TimeSpan.Zero;

        /// <summary>
        /// A march's power with its Bloodied heroes at a quarter less: what they add to it,
        /// <paramref name="withThem"/> less <paramref name="withoutThem"/>, is cut by <see cref="Penalty"/>.
        /// Party power is not a plain sum (resonance, the strongest few), so their share is measured
        /// rather than added up.
        /// </summary>
        public static double Weaken(double withThem, double withoutThem) =>
            withThem - Penalty * Math.Max(0, withThem - withoutThem);
    }
}
