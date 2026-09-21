using System;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// When a cleared site offers a fight again.
    ///
    /// <para><b>Why this exists.</b> Clearing a hostile site inside a region you hold is the only
    /// way to restore its resolve, and resolve is the only thing a raider can move. Until now a
    /// cleared site stayed cleared forever, so a region's defence was <i>finite</i> while raiding it
    /// was not: a defender could restore at most (dungeons × <see cref="RegionResolveRules.RestoredPerClear"/>)
    /// resolve ever, against an attacker who could take 5-15 every few hours indefinitely. Over a
    /// long enough horizon the attacker won by arithmetic, however well the defence was played —
    /// which contradicts the design's own "a region they never tend will fall", since it made
    /// tending impossible rather than merely necessary.</para>
    ///
    /// <para><b>Why sites recover rather than resolve regenerating on its own.</b> Tending should be
    /// something a player <i>does</i>. Passive regeneration would restore a neglected region exactly
    /// as fast as a defended one, which is the distinction the design rests on. Recovering sites make
    /// the answer to a raid an action taken in the territory being raided — and they fix a second
    /// problem that had nothing to do with raiding: a region's PvE content was consumed permanently,
    /// so a well-played region slowly emptied itself of anything to do.</para>
    ///
    /// <para><b>The rates.</b> A region holds 2-5 dungeons (one per tier, plus one) and sometimes a
    /// portal, so a defender who actually plays can return roughly 20-50 resolve per recovery window.
    /// An attacker takes 5-15 per four-hour cooldown, so 10-30 across the same eight hours. Those are
    /// deliberately comparable: a committed defender roughly matches a committed attacker one-to-one,
    /// several attackers still outpace one defender, and a defender who does nothing still loses the
    /// region. The attacker's action is one click and the defender's is fifteen minutes of combat,
    /// which is the honest asymmetry here — the attacker must first clear the raid bar and can be
    /// repelled. <b>None of this has been played by two real people yet; treat the numbers as a first
    /// pass, not a balance.</b></para>
    /// </summary>
    public static class SiteRespawnRules
    {
        /// <summary>Hours before a cleared site is worth fighting again.</summary>
        public const int RespawnHours = 8;

        public static TimeSpan RespawnAfter => TimeSpan.FromHours(RespawnHours);

        /// <summary>
        /// True when a site's fight has been spent and has not yet come back.
        ///
        /// <para>A site cleared before this rule existed carries no timestamp. Those are treated as
        /// recovered rather than as cleared forever: it is the self-healing direction, and the
        /// alternative leaves permanently dead ground in every world that predates the change.</para>
        /// </summary>
        public static bool IsCleared(SiteOverride siteOverride, DateTime utcNow)
        {
            if (siteOverride == null || !siteOverride.Cleared) return false;
            if (siteOverride.ClearedAtUtcTicks <= 0) return false;

            return utcNow - new DateTime(siteOverride.ClearedAtUtcTicks, DateTimeKind.Utc) < RespawnAfter;
        }

        public static bool IsCleared(SiteOverride siteOverride) => IsCleared(siteOverride, DateTime.UtcNow);

        /// <summary>
        /// Whether a named site in a region has been spent. The lookup lives here so the dossier,
        /// the markers and the server all ask the same question the same way.
        /// </summary>
        public static bool IsCleared(WorldRegionData region, string siteId)
        {
            if (region?.SiteOverrides == null || string.IsNullOrEmpty(siteId)) return false;

            return region.SiteOverrides.TryGetValue(siteId, out var over) && IsCleared(over);
        }

        /// <summary>
        /// When a cleared site comes back, or <see cref="DateTime.MinValue"/> when it is available
        /// now. The dossier shows this so a player can see what their region will have to offer.
        /// </summary>
        public static DateTime RecoversAt(SiteOverride siteOverride)
        {
            if (siteOverride == null || !siteOverride.Cleared || siteOverride.ClearedAtUtcTicks <= 0)
                return DateTime.MinValue;

            return new DateTime(siteOverride.ClearedAtUtcTicks, DateTimeKind.Utc) + RespawnAfter;
        }
    }
}
