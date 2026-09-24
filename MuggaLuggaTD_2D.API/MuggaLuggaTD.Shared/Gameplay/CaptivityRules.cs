using System;
using System.Collections.Generic;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// What happens to characters who were defending a region their owner lost.
    ///
    /// <para><b>They are taken, and then they come home.</b> Taking them is what makes winning a siege
    /// worth the eight hours it costs to muster one — a region that changes hands with its defenders
    /// walking away free is a region that was only ever worth its income. But holding them forever
    /// would make losing a region cost the army needed to take it back, and the roster cap falls with
    /// the land as well (<see cref="RosterCapRules"/>), so a single lost siege would compound into
    /// three separate punishments landing at once.</para>
    ///
    /// <para><b>Three ways out, and the player chooses which.</b> Wait <see cref="PrisonerReturnHours"/>
    /// and they walk home by themselves; retake the region and they are freed on the spot; or pay a
    /// ransom in gold and have them back now. The first makes a defeat cost a session rather than a
    /// season, the second is the best story the system has, and the third is for the player who needs
    /// the army back for a counterattack and would rather pay than wait.</para>
    ///
    /// <para>Eight hours deliberately matches <see cref="SiegeAssaultRules"/>' muster window and
    /// <see cref="SiteRespawnRules"/>' recovery: the game already asks a player to come back in eight
    /// hours for two other things, and a fourth number would be a fourth thing to learn.</para>
    ///
    /// <para><b>Nothing ticks.</b> Captivity is a stamp plus a rule, read on the spot, exactly like a
    /// cleared site's recovery. A stamp of zero reads as <i>returned</i> rather than as <i>captured at
    /// the dawn of time</i>, so a prisoner written before this rule existed is already home — the
    /// self-healing direction.</para>
    /// </summary>
    public static class CaptivityRules
    {
        /// <summary>How long a captured character is held before walking home on their own.</summary>
        public const double PrisonerReturnHours = 8.0;

        /// <summary>
        /// Gold to ransom one prisoner back immediately.
        ///
        /// <para>Priced to be a real decision rather than a formality — buying a captured company of
        /// four back costs roughly eight tier-1 clears — but deliberately under
        /// <see cref="RosterCapRules.FirstSlotCostGold"/>, because this buys back something the player
        /// already owns rather than buying something new. It also has to stay worth <i>not</i> paying:
        /// waiting is free, so a ransom priced like a disaster would simply never be used.</para>
        /// </summary>
        public const long RansomPerPrisonerGold = 500;

        /// <summary>When a character captured at <paramref name="capturedAtUtcTicks"/> walks home.</summary>
        public static DateTime ReturnsAt(long capturedAtUtcTicks)
            => new DateTime(capturedAtUtcTicks, DateTimeKind.Utc).AddHours(PrisonerReturnHours);

        /// <summary>
        /// Whether a capture stamped <paramref name="capturedAtUtcTicks"/> is still holding anybody.
        ///
        /// <para>Ask this rather than reading <see cref="SiteOverride.CapturedCharacterIds"/> directly.
        /// The ids outlive the captivity — clearing them out is cosmetic and happens when something
        /// else touches the site — so the list says who <i>was</i> taken and this says whether they are
        /// still there. The marker, the party picker and the server's "may I ransom" must not
        /// disagree.</para>
        /// </summary>
        public static bool IsHeld(long capturedAtUtcTicks, DateTime nowUtc)
        {
            if (capturedAtUtcTicks <= 0) return false;
            return nowUtc < ReturnsAt(capturedAtUtcTicks);
        }

        /// <summary>Whether this site is holding anybody right now.</summary>
        public static bool IsHolding(SiteOverride siteOverride, DateTime nowUtc)
        {
            if (siteOverride == null) return false;
            if (siteOverride.CapturedCharacterIds == null || siteOverride.CapturedCharacterIds.Count == 0)
                return false;

            return IsHeld(siteOverride.CapturedAtUtcTicks, nowUtc);
        }

        /// <summary>What it costs to ransom <paramref name="prisoners"/> back.</summary>
        public static long RansomCostGold(int prisoners)
            => prisoners <= 0 ? 0 : prisoners * RansomPerPrisonerGold;

        /// <summary>
        /// Every one of <paramref name="myCharacterIds"/> being held anywhere in the world, keyed by the
        /// site holding them.
        ///
        /// <para>Shared, and it takes the <b>whole world</b> rather than one region, because "where are
        /// my characters" is a world-scoped question. The client used to ask it of the region it
        /// happened to have open, which meant a garrison was only locked while its owner was standing
        /// in it, and a captured company was only discovered by walking back into the region that had
        /// just been taken from them.</para>
        /// </summary>
        public static Dictionary<string, List<string>> HeldIdsIn(
            IEnumerable<WorldRegionData> allRegions, ISet<string> myCharacterIds, DateTime nowUtc)
        {
            var held = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (allRegions == null || myCharacterIds == null || myCharacterIds.Count == 0) return held;

            foreach (var region in allRegions)
            {
                if (region?.SiteOverrides == null) continue;

                foreach (var pair in region.SiteOverrides)
                {
                    if (!IsHolding(pair.Value, nowUtc)) continue;

                    List<string> mine = null;
                    foreach (var id in pair.Value.CapturedCharacterIds)
                    {
                        if (string.IsNullOrEmpty(id) || !myCharacterIds.Contains(id)) continue;
                        (mine ??= new List<string>()).Add(id);
                    }

                    if (mine != null) held[pair.Key] = mine;
                }
            }

            return held;
        }

        /// <summary>
        /// Characters stationed anywhere in the world at a site inside a region
        /// <paramref name="userId"/> holds.
        ///
        /// <para>Whole-world for the same reason as <see cref="HeldIdsIn"/>: a garrison in a region the
        /// player is not looking at is still a garrison, and letting them march it out on a dungeon run
        /// while the server still counts its power toward that region's hold is a hole in both
        /// directions.</para>
        /// </summary>
        public static HashSet<string> GarrisonedIdsOf(
            IEnumerable<WorldRegionData> allRegions, string userId)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (allRegions == null || string.IsNullOrEmpty(userId)) return set;

            foreach (var region in allRegions)
            {
                if (region?.SiteOverrides == null || !region.IsOwnedByPlayer(userId)) continue;

                foreach (var pair in region.SiteOverrides)
                {
                    var ids = pair.Value?.GarrisonCharacterIds;
                    if (ids == null) continue;

                    foreach (var id in ids)
                        if (!string.IsNullOrEmpty(id)) set.Add(id);
                }
            }

            return set;
        }
    }
}
