using System;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// How a region's resolve moves (see <c>docs/design/siege.md</c> §2-3).
    ///
    /// <para>Resolve is morale, 0 to 100, and it multiplies the whole of a region's hold. Raiding
    /// wears it down; clearing the region's <i>own</i> dungeons builds it back. Those two are the
    /// attack and the answer, and they are the reason the own-region PvE fix mattered — a player who
    /// could not fight inside their own territory had no way to respond to being raided.</para>
    ///
    /// <para><b>The ceiling this used to have is gone.</b> Sites stayed cleared forever, so a
    /// defender could restore only (dungeons × <see cref="RestoredPerClear"/>) resolve <i>ever</i>,
    /// against unlimited raiding — the attacker won by arithmetic however well the defence was
    /// played. Cleared sites now recover; see <see cref="SiteRespawnRules"/>, which carries the
    /// rates and the reasoning.</para>
    /// </summary>
    public static class RegionResolveRules
    {
        /// <summary>Full morale. A region captured by force starts here — see WorldRegionBlob.CaptureRegion.</summary>
        public const int Maximum = 100;

        /// <summary>Broken morale. Hold still has a floor below this — resolve counts for no less than 25%.</summary>
        public const int Minimum = 0;

        /// <summary>Resolve restored by clearing one of a region's own fightable sites.</summary>
        public const int RestoredPerClear = 10;

        /// <summary>
        /// What clearing <paramref name="siteType"/> inside your own region gives its resolve back.
        ///
        /// <para>Only the hostile sites count. Taking a settlement or a node is not a show of force
        /// that would steady anybody — and those are not fights in the first place.</para>
        /// </summary>
        public static int RestoredByClearing(LocationType siteType)
        {
            switch (siteType)
            {
                case LocationType.Dungeon:
                case LocationType.Portal:
                    return RestoredPerClear;
                default:
                    return 0;
            }
        }

        /// <summary>Applies a change to a region's resolve, keeping it inside its bounds.</summary>
        public static int Apply(int resolve, int delta)
        {
            return Clamp(resolve + delta);
        }

        public static int Clamp(int resolve)
        {
            if (resolve < Minimum) return Minimum;
            if (resolve > Maximum) return Maximum;
            return resolve;
        }
    }
}
