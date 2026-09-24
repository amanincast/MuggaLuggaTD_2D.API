using System;
using System.Collections.Generic;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// Lays out a world of regions (design 7d): roughly eighty hexes, each with a biome, a tier and
    /// a seed, with the players spaced apart and the NPC factions holding ground between them.
    ///
    /// <para>Deterministic from the world seed, so the same world can be rebuilt rather than stored,
    /// and so the server can reconstruct what the client is looking at.</para>
    /// </summary>
    public static class WorldMapGenerator
    {
        public const int DefaultRegionCount = 78;

        /// <summary>Capitals are seated in this band of rings, leaving the centre neutral heartland.</summary>
        private const int SettlementBandInner = 2;
        private const int SettlementBandOuter = 4;

        /// <summary>
        /// Builds a world. <paramref name="players"/> maps user id to display name; each gets a
        /// capital region, placed as far from the others as the map allows.
        /// </summary>
        public static List<WorldRegionData> Generate(int worldSeed, IReadOnlyList<PlayerSeat> players, int regionCount = DefaultRegionCount)
        {
            if (regionCount < 1) regionCount = 1;

            var hexes = HexCoord.Spiral(regionCount);
            var random = DeterministicRandom.ForSubject((ulong)(uint)worldSeed, 0x0DD1E5);

            var regions = new List<WorldRegionData>(hexes.Count);
            for (int i = 0; i < hexes.Count; i++)
            {
                var hex = hexes[i];
                int distance = HexCoord.Distance(new HexCoord(0, 0), hex);

                regions.Add(new WorldRegionData
                {
                    RegionId = "r" + i,
                    Hex = hex,
                    // Each region's seed is derived from the world's and its own position, so one
                    // region's interior never shifts because another was added or removed.
                    Seed = (int)(uint)DeterministicRandom.ForSubject((ulong)(uint)worldSeed, HexKey(hex)).NextUInt64(),
                    Biome = BiomeFor(hex, distance, ref random),
                    Tier = TierFor(distance),
                    Ownership = LocationOwnership.Neutral,
                    Faction = FactionId.None,
                    Entrenchment = 0,
                    Resolve = 100
                });
            }

            SeatPlayers(regions, players, ref random);
            SeatFactions(regions, ref random);

            return regions;
        }

        /// <summary>A player to seat in the world.</summary>
        public class PlayerSeat
        {
            public string UserId;
            public string DisplayName;

            public PlayerSeat() { }

            public PlayerSeat(string userId, string displayName)
            {
                UserId = userId;
                DisplayName = displayName;
            }
        }

        private static ulong HexKey(HexCoord hex)
        {
            // Two signed ints folded into one key, offset so negatives stay distinct.
            unchecked
            {
                ulong q = (ulong)(uint)(hex.Q + 1000);
                ulong r = (ulong)(uint)(hex.R + 1000);
                return (q << 32) | r;
            }
        }

        /// <summary>
        /// The map gets harder outward from the centre, so a player always has somewhere gentler
        /// behind them and somewhere worth taking ahead.
        /// </summary>
        private static int TierFor(int distanceFromCentre)
        {
            if (distanceFromCentre <= 1) return 1;
            if (distanceFromCentre <= 3) return 2;
            if (distanceFromCentre <= 4) return 3;
            return 4;
        }

        private static BiomeType BiomeFor(HexCoord hex, int distance, ref DeterministicRandom random)
        {
            // The heart of the map is the gentle river vale; the rim is volcanic and highland. In
            // between, weight the roll by distance so biomes form bands rather than confetti.
            if (distance == 0) return BiomeType.Grassland;

            int[] weights =
            {
                Math.Max(0, 60 - (distance * 12)), // Grassland
                30,                                // Forest
                10 + (distance * 4),               // Lakeland
                Math.Max(0, (distance - 1) * 10),  // Highland
                Math.Max(0, (distance - 2) * 12),  // Volcanic
                10 + (distance * 3)                // Swamp
            };

            return (BiomeType)random.NextWeighted(weights);
        }

        /// <summary>
        /// Gives each player a capital, choosing the seat furthest from every seat already taken.
        /// Greedy rather than optimal, which is enough to keep neighbours from starting on top of
        /// one another and is stable for a given world seed.
        ///
        /// <para>Seats are drawn from a band partway out from the centre rather than from the whole
        /// map, so every player begins in comparable country. Seating the first player at the centre
        /// would hand them a uniquely sheltered position no later player could be given, and the
        /// centre is better left as neutral heartland everyone borders.</para>
        /// </summary>
        private static void SeatPlayers(List<WorldRegionData> regions, IReadOnlyList<PlayerSeat> players, ref DeterministicRandom random)
        {
            if (players == null || players.Count == 0) return;

            var band = new List<WorldRegionData>();
            foreach (var region in regions)
            {
                int distance = HexCoord.Distance(new HexCoord(0, 0), region.Hex);
                if (distance >= SettlementBandInner && distance <= SettlementBandOuter)
                    band.Add(region);
            }

            // Nothing in the band (a tiny world) — fall back to the whole map rather than seating
            // nobody.
            if (band.Count == 0) band.AddRange(regions);

            var taken = new List<HexCoord>();

            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null || string.IsNullOrEmpty(player.UserId)) continue;

                WorldRegionData best = null;
                int bestScore = -1;

                if (taken.Count == 0)
                {
                    // The first seat has nothing to be far from, so it is drawn from the band.
                    best = band[random.Next(band.Count)];
                }
                else
                {
                    foreach (var region in band)
                    {
                        if (region.Ownership != LocationOwnership.Neutral) continue;

                        int score = NearestDistance(region.Hex, taken);
                        if (score <= bestScore) continue;

                        bestScore = score;
                        best = region;
                    }
                }

                if (best == null || best.Ownership != LocationOwnership.Neutral)
                    return; // Band is full: more players than the world can seat apart.

                best.Ownership = LocationOwnership.Player;
                best.OwnerUserId = player.UserId;
                best.OwnerDisplayName = player.DisplayName;
                best.Faction = FactionId.Player;
                best.IsCapital = true;
                // A seat starts fortified enough to survive the first days of a world.
                best.Entrenchment = 2;
                best.Tier = 1;
                best.Biome = BiomeType.Grassland;

                taken.Add(best.Hex);
            }
        }

        private static int NearestDistance(HexCoord hex, List<HexCoord> others)
        {
            int nearest = int.MaxValue;
            for (int i = 0; i < others.Count; i++)
            {
                int d = HexCoord.Distance(hex, others[i]);
                if (d < nearest) nearest = d;
            }

            return nearest;
        }

        /// <summary>
        /// Hands the outer ring's harder regions to the NPC factions. Static flavour owners for now:
        /// they hold ground and defend it, but nothing makes them act. World events that let them
        /// threaten and take player territory come later.
        /// </summary>
        private static void SeatFactions(List<WorldRegionData> regions, ref DeterministicRandom random)
        {
            foreach (var region in regions)
            {
                if (region.Ownership != LocationOwnership.Neutral) continue;
                if (region.Tier < 3) continue;

                // Roughly half the dangerous ground has a banner over it; the rest is wild.
                if (!random.Chance(5000)) continue;

                bool ashkin = region.Biome == BiomeType.Volcanic || region.Biome == BiomeType.Highland;

                region.Ownership = LocationOwnership.Enemy;
                region.Faction = ashkin ? FactionId.Ashkin : FactionId.Grimjaw;
                region.OwnerDisplayName = ashkin ? "The Ashkin" : "Grimjaw Clan";
                region.Entrenchment = random.Next(1, 4);
                region.Resolve = 100;
            }
        }
    }
}
