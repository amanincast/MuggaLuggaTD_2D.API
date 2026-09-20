using System;

namespace MuggaLuggaTD.Shared.World
{
    /// <summary>
    /// A small seeded generator whose sequence is fixed by this source and nothing else.
    ///
    /// <para><b>Why not System.Random.</b> The world map derives every region's interior from a seed,
    /// and the server validates a PvE target by regenerating the region the client says it entered.
    /// That only works if both sides produce byte-identical layouts. `System.Random` does not promise
    /// that: its algorithm has changed between .NET releases, and the client runs it on Unity's Mono
    /// or IL2CPP while the server runs it on .NET 8. Any divergence would show up as the server
    /// rejecting legitimate entries for a region that "does not contain" the site the player is
    /// standing on — a failure that looks like a cheating player rather than a runtime difference.
    ///
    /// <para>This is SplitMix64: a fully specified sequence of unchecked 64-bit integer operations
    /// with no floating point, no platform types and no library calls, so it cannot drift.</para>
    /// </summary>
    public struct DeterministicRandom
    {
        private const ulong Gamma = 0x9E3779B97F4A7C15UL;

        private ulong _state;

        public DeterministicRandom(ulong seed)
        {
            // Seed 0 would still produce a fine sequence, but mixing it keeps callers that pass small
            // ordinals (region 0, region 1) from starting in a visibly related place.
            _state = seed ^ Gamma;
        }

        /// <summary>Derives an independent stream from this one, for a sub-part of the same subject.</summary>
        public static DeterministicRandom ForSubject(ulong seed, ulong subject)
        {
            return new DeterministicRandom(Mix(seed ^ Mix(subject)));
        }

        public ulong NextUInt64()
        {
            unchecked
            {
                _state += Gamma;
                return Mix(_state);
            }
        }

        private static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>A value in [0, exclusiveMax). Rejection sampled, so the result is unbiased.</summary>
        public int Next(int exclusiveMax)
        {
            if (exclusiveMax <= 0)
                throw new ArgumentOutOfRangeException(nameof(exclusiveMax), "Bound must be positive.");

            // Modulo alone would favour the low end of the range. Rejecting the short final block
            // costs an occasional extra draw and keeps the distribution flat, which matters because
            // site placement and biome choice both lean on it.
            ulong bound = (ulong)exclusiveMax;
            ulong limit = ulong.MaxValue - (ulong.MaxValue % bound) - 1;

            ulong value;
            do
            {
                value = NextUInt64();
            }
            while (value > limit);

            return (int)(value % bound);
        }

        /// <summary>A value in [inclusiveMin, exclusiveMax).</summary>
        public int Next(int inclusiveMin, int exclusiveMax)
        {
            if (exclusiveMax <= inclusiveMin)
                throw new ArgumentOutOfRangeException(nameof(exclusiveMax), "Range must be non-empty.");

            return inclusiveMin + Next(exclusiveMax - inclusiveMin);
        }

        /// <summary>True with the given chance, expressed in hundredths of a percent (10000 = certain).</summary>
        public bool Chance(int outOfTenThousand)
        {
            if (outOfTenThousand <= 0) return false;
            if (outOfTenThousand >= 10000) return true;
            return Next(10000) < outOfTenThousand;
        }

        /// <summary>Picks an index weighted by the given weights. Weights must be non-negative.</summary>
        public int NextWeighted(int[] weights)
        {
            if (weights == null || weights.Length == 0)
                throw new ArgumentException("At least one weight is required.", nameof(weights));

            long total = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] < 0)
                    throw new ArgumentException("Weights must not be negative.", nameof(weights));
                total += weights[i];
            }

            if (total <= 0)
                return Next(weights.Length);

            long roll = Next((int)Math.Min(total, int.MaxValue));
            for (int i = 0; i < weights.Length; i++)
            {
                roll -= weights[i];
                if (roll < 0) return i;
            }

            return weights.Length - 1;
        }

        /// <summary>Shuffles in place (Fisher-Yates), so callers get a deterministic ordering.</summary>
        public void Shuffle<T>(System.Collections.Generic.IList<T> items)
        {
            if (items == null) return;

            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = Next(i + 1);
                var swap = items[i];
                items[i] = items[j];
                items[j] = swap;
            }
        }
    }
}
