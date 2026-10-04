using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// When a player may fight a dungeon or portal again, and when clearing it shapes the realm again
    /// (Mike, 2026-10-03: "the game has to stay active - we don't want players sitting, waiting
    /// around for respawns").
    ///
    /// <para><b>Clears are per player.</b> A site used to be spent for eight hours for everyone in the
    /// realm, written into the shared world, so one player's clear left everybody else in that region
    /// with nothing to fight. Now a clear is recorded against the player who made it, and nobody
    /// else's map changes.</para>
    ///
    /// <para><b>The lockout is the rotation.</b> After clearing a site a player may not fight it again
    /// for <see cref="Lockout"/> (10 minutes). Every clear pays its full experience, gold, gear and
    /// materials, so farming stays worthwhile, but a party must move on to its next fight rather than
    /// re-running the best site on repeat.</para>
    ///
    /// <para><b>What shapes the realm is rationed separately.</b> Restoring a region's resolve, bringing
    /// a recruit to the Tavern, resetting the Tavern's and the Hiring Hall's refresh price, and the
    /// season points for a clear come once per player per site every <see cref="WorldRewardWindow"/>
    /// (8 hours). Without this a ten-minute loop would farm defence, recruits and the scoreboard.
    /// Eight hours is the old respawn, so the defender/raider arithmetic it was tuned for still holds:
    /// a defender can restore one site's worth of resolve per site per eight hours, as before.</para>
    ///
    /// <para><b>Not tapered.</b> Repeat clears of one site still pay in full. A taper over the reward
    /// window would punish exactly the rotation the lockout asks for (a player cycling five sites
    /// would hit it on every one after the first lap). If farming outpaces the economy, a taper is
    /// the lever to add here.</para>
    /// </summary>
    public static class SiteRotationRules
    {
        /// <summary>How long after clearing a site the same player must fight elsewhere.</summary>
        public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(10);

        /// <summary>How often clearing one site may restore resolve, bring a recruit and score.</summary>
        public static readonly TimeSpan WorldRewardWindow = TimeSpan.FromHours(8);

        /// <summary>
        /// When a player may fight a site again, or <see cref="DateTime.MinValue"/> when they have
        /// never cleared it.
        /// </summary>
        public static DateTime LockedUntil(DateTime? lastClearedUtc) =>
            lastClearedUtc.HasValue ? lastClearedUtc.Value + Lockout : DateTime.MinValue;

        /// <summary>True while a player must fight elsewhere.</summary>
        public static bool IsLocked(DateTime? lastClearedUtc, DateTime utcNow) => utcNow < LockedUntil(lastClearedUtc);

        /// <summary>
        /// When clearing a site will shape the realm again for this player, or
        /// <see cref="DateTime.MinValue"/> when it would now.
        /// </summary>
        public static DateTime WorldRewardsBackAt(DateTime? lastWorldRewardUtc) =>
            lastWorldRewardUtc.HasValue ? lastWorldRewardUtc.Value + WorldRewardWindow : DateTime.MinValue;

        /// <summary>Whether a clear now restores resolve, brings a recruit, resets prices and scores.</summary>
        public static bool WorldRewardsDue(DateTime? lastWorldRewardUtc, DateTime utcNow) =>
            utcNow >= WorldRewardsBackAt(lastWorldRewardUtc);
    }
}
