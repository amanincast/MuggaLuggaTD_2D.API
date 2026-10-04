using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// The cost of a lost auto-fight (<c>docs/design/auto-fight.md</c> §4; Mike, 2026-10-04).
    ///
    /// <para>An auto company that loses keeps everything it holds. Its members are <b>Bloodied</b>
    /// instead: <b>barred from every fight</b> (a hand run, a raid, a siege muster, and auto-fights)
    /// until they have recovered, <see cref="Recovery"/> after the loss. The company walks home and
    /// rests, then takes up its order again by itself.</para>
    ///
    /// <para>Nothing ticks: the server stores when each member recovers and asks this rule.</para>
    /// </summary>
    public static class BloodiedRules
    {
        /// <summary>How long a Bloodied character needs to recover.</summary>
        public static readonly TimeSpan Recovery = TimeSpan.FromMinutes(30);

        /// <summary>When a character bloodied at <paramref name="lostAtUtc"/> is fit to fight again.</summary>
        public static DateTime RecoversAt(DateTime lostAtUtc) => lostAtUtc + Recovery;

        /// <summary>Whether a character recovering until <paramref name="recoversAtUtc"/> is still Bloodied.</summary>
        public static bool IsBloodied(DateTime? recoversAtUtc, DateTime utcNow) =>
            recoversAtUtc.HasValue && utcNow < recoversAtUtc.Value;

        /// <summary>What is left of the recovery, or zero.</summary>
        public static TimeSpan Remaining(DateTime? recoversAtUtc, DateTime utcNow) =>
            IsBloodied(recoversAtUtc, utcNow) ? recoversAtUtc!.Value - utcNow : TimeSpan.Zero;
    }
}
