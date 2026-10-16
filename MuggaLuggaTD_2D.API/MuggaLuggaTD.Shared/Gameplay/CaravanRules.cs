using System;
using System.Collections.Generic;
using System.Linq;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>The sides of the caravan's clearing an enemy lane can open on.</summary>
    public enum CaravanSide
    {
        North = 0,
        East = 1,
        South = 2,
        West = 3
    }

    /// <summary>The pieces of the defence kit (Active Content F, Mike's F4).</summary>
    public enum CaravanPiece
    {
        /// <summary>A short wall with health: enemies break it or walk round it.</summary>
        Palisade = 0,

        /// <summary>A fixed tower that shoots the nearest enemy in range. It can be destroyed.</summary>
        ArcherPost = 1,

        /// <summary>Slows the enemies who cross it. Heroes walk through untouched.</summary>
        TarPit = 2,

        /// <summary>Bursts when an enemy steps on it, then re-arms.</summary>
        FireTrap = 3
    }

    /// <summary>
    /// Caravans, the protection fight (Active Content F; Mike's answers F1-F11, 2026-10-15): "Circle the wagons". The
    /// caravan is halted in a clearing, three wagons each carrying a third of the cargo. Most enemies go for the nearest
    /// wagon; elites, bosses and a share of the rest hunt the heroes. Waves come in by lanes, two or three of the four
    /// sides. Before the first wave there is a setup, and after each wave a lull; the player places a small
    /// kit of defences on fixed spots at any time in the fight (Mike 2026-10-16), paid with build points the fight grants (never gold or goods).
    ///
    /// <para>Shared so the server prices the fight from the same waves and pays from the same share (phase 2). The
    /// fight itself is the client's; nothing here is stored.</para>
    /// </summary>
    public static class CaravanRules
    {
        /// <summary>A caravan fight is a pseudo-site, <c>caravan:{id}</c>.</summary>
        public const string SitePrefix = "caravan:";

        /// <summary>Wagons in the circle; each carries an equal share of the cargo.</summary>
        public const int Wagons = 3;

        /// <summary>The waves are sized as a tier this high (its factor on each wave's roster).</summary>
        public const int Tier = 2;

        /// <summary>Waves to hold off: a tier-2 skirmish's, one shorter (spec: "a little shorter than a clearing").</summary>
        public const int Waves = 5;

        /// <summary>Seconds of setup before the first wave, with no enemies (F2; Mike 2026-10-16: 20 was too short, then 30).</summary>
        public const double SetupSeconds = 30;

        /// <summary>Seconds of quiet after a wave is cleared before the next comes (F2).</summary>
        public const double LullSeconds = 8;

        /// <summary>The next wave comes this long after the last began even if the field is not clear: a slow defence is swamped.</summary>
        public const double MostWaveSeconds = 40;

        /// <summary>Build points the fight starts with (F3; Mike 2026-10-16: 8, was 6).</summary>
        public const int StartingPoints = 8;

        /// <summary>
        /// Once the first wave comes, a build point every this many seconds (Mike 2026-10-16: points over time rather
        /// than +2 a cleared wave; 20 s, then 10). The setup earns none: the starting points are its stock.
        /// </summary>
        public const double SecondsPerPoint = 10;

        /// <summary>Repairing a standing piece to full costs this much.</summary>
        public const int RepairCost = 1;

        /// <summary>One in this many ordinary enemies hunts the heroes rather than the wagons (F5).</summary>
        public const int HunterEvery = 4;

        /// <summary>Lanes a wave opens on: at least this many sides...</summary>
        public const int FewestLanes = 2;

        /// <summary>...and at most this many.</summary>
        public const int MostLanes = 3;

        /// <summary>A defence's price in build points (F3, F4).</summary>
        public static int Cost(CaravanPiece piece)
        {
            switch (piece)
            {
                case CaravanPiece.Palisade: return 1;
                case CaravanPiece.ArcherPost: return 3;
                case CaravanPiece.TarPit: return 2;
                case CaravanPiece.FireTrap: return 2;
                default: return int.MaxValue;
            }
        }

        /// <summary>All the pieces, in the order the build bar shows them.</summary>
        public static readonly CaravanPiece[] Pieces =
            { CaravanPiece.Palisade, CaravanPiece.ArcherPost, CaravanPiece.TarPit, CaravanPiece.FireTrap };

        /// <summary>Build points held <paramref name="sinceFirstWave"/> seconds after the first wave came, before any are spent.</summary>
        public static int PointsEarned(double sinceFirstWave) =>
            StartingPoints + (int)Math.Floor(Math.Max(0, sinceFirstWave) / SecondsPerPoint);

        /// <summary>Seconds until the next build point, <paramref name="sinceFirstWave"/> seconds after the first wave came.</summary>
        public static double SecondsToNextPoint(double sinceFirstWave) =>
            SecondsPerPoint - (Math.Max(0, sinceFirstWave) % SecondsPerPoint);

        /// <summary>
        /// Whether the <paramref name="arrival"/>-th enemy of the fight (0-based) hunts the heroes rather than the wagons:
        /// every elite and boss, and one ordinary enemy in <see cref="HunterEvery"/> (F5). So the player cannot just stand
        /// on a wagon, and cannot leave the wagons to fight elsewhere.
        /// </summary>
        public static bool HuntsHeroes(int arrival, bool elite, bool boss) =>
            elite || boss || (arrival >= 0 && arrival % HunterEvery == HunterEvery - 1);

        /// <summary>
        /// The sides wave <paramref name="wave"/> (1-based) comes in by: two or three of the four, from the arena's seed,
        /// so the same caravan fight always comes the same way, and a side lane is known before the wave arrives.
        /// </summary>
        public static List<CaravanSide> Lanes(int arenaSeed, int wave)
        {
            var random = DeterministicRandom.ForSubject(Naming.Hash("caravan-lanes:" + arenaSeed), Naming.Hash("wave:" + wave));
            int count = random.Next(FewestLanes, MostLanes + 1);
            var sides = new List<CaravanSide> { CaravanSide.North, CaravanSide.East, CaravanSide.South, CaravanSide.West };
            for (int i = sides.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                var swap = sides[i];
                sides[i] = sides[j];
                sides[j] = swap;
            }
            return sides.Take(count).OrderBy(s => (int)s).ToList();
        }

        /// <summary>
        /// Whether the next wave is due. The first comes when the setup is over; each later one after a lull on a clear
        /// field, or once <see cref="MostWaveSeconds"/> have passed since the last began.
        /// </summary>
        /// <param name="wavesBegun">Waves already begun.</param>
        /// <param name="sinceWaveBegan">Seconds since the last wave began (since the fight began, before the first).</param>
        /// <param name="clearFor">Seconds the field has been clear; negative while anything stands.</param>
        public static bool NextWaveDue(int wavesBegun, double sinceWaveBegan, double clearFor)
        {
            if (wavesBegun <= 0) return sinceWaveBegan >= SetupSeconds;
            return clearFor >= LullSeconds || sinceWaveBegan >= MostWaveSeconds;
        }

        /// <summary>The share of the cargo, and of the pay, that got through: a third a wagon still standing (F6).</summary>
        public static double DeliveredShare(int wagonsStanding) =>
            Math.Max(0, Math.Min(Wagons, wagonsStanding)) / (double)Wagons;

        /// <summary>The caravan is lost when no wagon stands.</summary>
        public static bool IsLost(int wagonsStanding) => wagonsStanding <= 0;

        /// <summary>A perfect defence: every wagon standing, none below half its health. A small bonus purse (phase 2).</summary>
        public static bool IsPerfect(int wagonsStanding, double lowestHealthShare) =>
            wagonsStanding >= Wagons && lowestHealthShare >= 0.5;

        // -----------------------------------------------------------------
        // The errand (phase 2): an Escort quest, the road and the pay
        // -----------------------------------------------------------------

        /// <summary>A caravan walks this much slower than a company alone: every region's walk is stretched by it.</summary>
        public const double Pace = 1.5;

        /// <summary>The guaranteed ambush strikes somewhere in this share of the journey's time (F7).</summary>
        public const double FirstAmbushFrom = 0.2, FirstAmbushTo = 0.45;

        /// <summary>A second ambush, on a long route, strikes in this later share, so it always comes after the first.</summary>
        public const double SecondAmbushFrom = 0.55, SecondAmbushTo = 0.8;

        /// <summary>An escort this many hexes away or more is struck twice, always (Mike 2026-10-16).</summary>
        public const int LongRouteHexes = 3;

        /// <summary>
        /// An escort pays this many times a quest's gold, by how far it goes (Mike 2026-10-16): ×1.5 for one hex, ×2 for
        /// two, ×2.5 for three. A long walk and a hard fight. <i>(tune)</i>
        /// </summary>
        public static double EscortGoldFactor(int hexes) => 1.0 + 0.5 * Math.Max(1, hexes);

        /// <summary>
        /// An escort's cargo, and so the goods it pays, by how far it goes (Mike 2026-10-16): as rolled for one hex, +25%
        /// for two, +50% for three. <i>(tune)</i>
        /// </summary>
        public static double EscortCargoFactor(int hexes) => 1.0 + 0.25 * (Math.Max(1, hexes) - 1);

        /// <summary>An escort pays one unit of its cargo in every this many carried, beside the essence. <i>(tune)</i></summary>
        public const int CargoCutEvery = 5;

        /// <summary>A perfect defence (<see cref="IsPerfect"/>) adds this share of the gold. <i>(tune)</i></summary>
        public const double PerfectBonus = 0.25;

        /// <summary>
        /// The fewest seconds a caravan fight can take: its setup and a little more. A claim faster than this is refused.
        /// </summary>
        public const double MinimumFightSeconds = SetupSeconds + 10;

        /// <summary>
        /// When a caravan's road is struck (F7): always once, early in the walk; and an escort
        /// <see cref="LongRouteHexes"/> hexes away or more a second time, always, later in the walk (Mike 2026-10-16).
        /// Shares of the journey's time. <paramref name="random"/> is the caller's, so the server owns the entropy and
        /// tests can fix it.
        /// </summary>
        public static (double First, double? Second) RollAmbushes(int hexes, Random random)
        {
            random = random ?? new Random();
            double first = FirstAmbushFrom + random.NextDouble() * (FirstAmbushTo - FirstAmbushFrom);
            double? second = hexes >= LongRouteHexes
                ? SecondAmbushFrom + random.NextDouble() * (SecondAmbushTo - SecondAmbushFrom)
                : (double?)null;
            return (first, second);
        }

        /// <summary>The gold an escort pays on delivery: the offer's, by the share delivered, and the bonus for a perfect defence.</summary>
        public static long DeliveredGold(long offerGold, int wagonsStanding, bool perfect) =>
            (long)Math.Round(Math.Max(0, offerGold) * DeliveredShare(wagonsStanding) * (perfect ? 1 + PerfectBonus : 1));

        /// <summary>The materials an escort pays on delivery: each of the offer's, by the share delivered (rounded, none below one dropped).</summary>
        public static List<MaterialGrant> DeliveredMaterials(IEnumerable<MaterialGrant> offerMaterials, int wagonsStanding)
        {
            double share = DeliveredShare(wagonsStanding);
            return (offerMaterials ?? Enumerable.Empty<MaterialGrant>())
                .Where(m => m != null)
                .Select(m => new MaterialGrant { MaterialName = m.MaterialName, Quantity = (int)Math.Round(m.Quantity * share) })
                .Where(m => m.Quantity > 0)
                .ToList();
        }

        public static string SiteId(string caravanId) => SitePrefix + caravanId;

        public static bool TryParseSiteId(string siteId, out string caravanId)
        {
            caravanId = null;
            if (string.IsNullOrEmpty(siteId) || !siteId.StartsWith(SitePrefix, StringComparison.Ordinal)) return false;
            caravanId = siteId.Substring(SitePrefix.Length);
            return caravanId.Length > 0;
        }
    }
}
