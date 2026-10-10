using System;
using System.Collections.Generic;
using System.Linq;
using Enums;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    public enum RealmGoalKind
    {
        /// <summary>Slay a number of one people (<see cref="RealmGoalOffer.Subject"/>), anywhere in the realm.</summary>
        Slay = 0,

        /// <summary>Clear a number of fightable sites: camps, ruins, dungeons, portals, keeps.</summary>
        Clear = 1,

        /// <summary>Win a number of ambushes on the road.</summary>
        Ambush = 2,
    }

    /// <summary>A day's goal as the rules set it: what, of whom, and how many.</summary>
    public class RealmGoalOffer
    {
        public RealmGoalKind Kind { get; set; }

        /// <summary>The people, for a Slay goal; null otherwise.</summary>
        public string Subject { get; set; }

        public int Target { get; set; }
    }

    /// <summary>
    /// Realm goals (Active Content B; Mike 2026-10-10: <i>daily</i>, not weekly): one target a day that
    /// the whole realm works toward, "Slay 900 Goblins", with a chest for everyone who helped.
    ///
    /// <para><b>The goal is a pure function</b> of the realm, the UTC day, the peoples its land holds and
    /// how many people play it, so the server can make it whenever it is first asked for and nothing
    /// needs scheduling. The same deeds that advance quests advance it (<see cref="CountOf"/>).</para>
    /// </summary>
    public static class RealmGoalRules
    {
        /// <summary>Per active player, per kind (tune). A realm of one is asked for twice this.</summary>
        public const int SlayPerPlayer = 150;
        public const int ClearPerPlayer = 8;
        public const int AmbushPerPlayer = 3;

        /// <summary>Kinds by weight: Slay 50, Clear 35, Ambush 15 (tune).</summary>
        private static readonly int[] KindWeights = { 50, 35, 15 };

        /// <summary>The share of the goal a player must have done to be paid (2%, tune).</summary>
        public const double ShareToBePaid = 0.02;

        /// <summary>The share that lifts the chest from Magic to Rare (10%, tune).</summary>
        public const double ShareForRare = 0.10;

        /// <summary>What a reached goal adds to gold from clears for the rest of that day (tune).</summary>
        public const double BoonGoldFactor = 1.10;

        /// <summary>The milestones announced in the war log, in quarters.</summary>
        public const int Quarters = 4;

        /// <summary>The UTC day <paramref name="at"/> falls in, as yyyymmdd.</summary>
        public static int DayOf(DateTime at) => (at.Year * 10000) + (at.Month * 100) + at.Day;

        /// <summary>When the goal of the day holding <paramref name="at"/> ends: the next UTC midnight.</summary>
        public static DateTime EndOf(DateTime at) => at.Date.AddDays(1);

        /// <summary>
        /// The day's goal. <paramref name="peoples"/> are the peoples the realm's land can field (from
        /// its biomes); a realm with none is never set a Slay goal.
        /// </summary>
        public static RealmGoalOffer For(string realmId, int day, IReadOnlyList<string> peoples, int activePlayers)
        {
            var random = DeterministicRandom.ForSubject(
                Naming.Hash($"{(realmId ?? "").ToLowerInvariant()}:goal"), (ulong)day);

            var weights = (int[])KindWeights.Clone();
            var folk = (peoples ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (folk.Count == 0) weights[(int)RealmGoalKind.Slay] = 0;

            var kind = (RealmGoalKind)random.NextWeighted(weights);
            int players = Math.Max(2, activePlayers);
            var offer = new RealmGoalOffer { Kind = kind };

            switch (kind)
            {
                case RealmGoalKind.Slay:
                    offer.Subject = folk[random.Next(folk.Count)];
                    offer.Target = SlayPerPlayer * players;
                    break;
                case RealmGoalKind.Clear:
                    offer.Target = ClearPerPlayer * players;
                    break;
                default:
                    offer.Target = AmbushPerPlayer * players;
                    break;
            }
            return offer;
        }

        /// <summary>How much one deed advances a goal of this kind and subject.</summary>
        public static int CountOf(RealmGoalKind kind, string subject, QuestDeed deed)
        {
            if (deed == null) return 0;
            switch (kind)
            {
                case RealmGoalKind.Slay:
                    return subject != null && deed.Kills != null && deed.Kills.TryGetValue(subject, out var n) ? Math.Max(0, n) : 0;
                case RealmGoalKind.Clear:
                    return string.IsNullOrEmpty(deed.ClearedSiteId) ? 0 : 1;
                default:
                    return deed.AmbushWon ? 1 : 0;
            }
        }

        /// <summary>The chest a share earns: none below <see cref="ShareToBePaid"/>, then Magic, then Rare.</summary>
        public static ItemRarityTypes? ChestFor(int contributed, int target)
        {
            if (target <= 0 || contributed <= 0) return null;
            double share = (double)contributed / target;
            if (share < ShareToBePaid) return null;
            return share >= ShareForRare ? ItemRarityTypes.Rare : ItemRarityTypes.Magic;
        }

        /// <summary>How many quarters of the goal a count has reached (0 to 4).</summary>
        public static int QuarterOf(int count, int target) =>
            target <= 0 ? 0 : Math.Min(Quarters, (int)((long)count * Quarters / target));

        /// <summary>The goal in a line: "Slay 900 Goblins", "Clear 24 sites", "Win 9 ambushes".</summary>
        public static string Describe(RealmGoalKind kind, string subject, int target)
        {
            switch (kind)
            {
                case RealmGoalKind.Slay: return $"Slay {target:N0} {subject}";
                case RealmGoalKind.Clear: return $"Clear {target:N0} sites";
                default: return $"Win {target:N0} ambushes";
            }
        }
    }
}
