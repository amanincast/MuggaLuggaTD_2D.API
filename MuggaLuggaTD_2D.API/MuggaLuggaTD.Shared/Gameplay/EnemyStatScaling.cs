using System;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>
    /// How an enemy's health and damage grow with its level.
    ///
    /// <para><b>Why this is shared.</b> The client fights the enemy and the server prices the reward
    /// for beating it, and they used to disagree: the client scaled health by <c>1 + 0.15·L</c> and the
    /// server's pricing by <c>1 + 0.15·(L−1)</c>, so every clear was paid 7–15% below the fight it
    /// actually demanded. One formula, in one place, is the fix.</para>
    ///
    /// <para><b>Why damage is no longer compounding.</b> Abilities are shared between players and
    /// enemies: an enemy's Magic Burst hit for exactly the 50 a player's does, against a player with
    /// 40 health. Worse, it compounded at 10% a level, so a level-20 enemy hit about six times harder
    /// than a level-1 one while player health did not move at all. Every enemy hit killed a mage at
    /// every level. Lowering the ability's damage would have nerfed the player casting it too, so the
    /// lever is an <b>enemy-side factor</b>: enemies deal a fraction of what the ability says, and it
    /// grows linearly. See design doc 03.</para>
    /// </summary>
    public static class EnemyStatScaling
    {
        /// <summary>Extra health per level, as a share of the base. Additive, not compounding.</summary>
        public const float HealthPerLevel = 0.15f;

        /// <summary>
        /// What share of an ability's listed damage an enemy actually deals. The ability's own number
        /// is what a <i>player</i> hits for; an enemy swinging the same weapon hits for less, because
        /// the player faces a wave of them at once.
        /// </summary>
        public const float EnemyDamageFactor = 0.4f;

        /// <summary>Extra enemy damage per level, as a share of the base. Additive, not compounding.</summary>
        public const float DamagePerLevel = 0.06f;

        /// <summary>
        /// An enemy's health at <paramref name="level"/>. Level 1 is the base value, so a "50 health"
        /// enemy in content has 50 health in the first fight the player meets it.
        /// </summary>
        public static long ScaledHealth(long baseHealth, long level, float healthPerLevel = HealthPerLevel)
        {
            if (level < 1) level = 1;
            return (long)Math.Round(baseHealth * (1f + healthPerLevel * (level - 1)));
        }

        /// <summary>
        /// What an enemy of <paramref name="level"/> hits for with an ability whose listed damage is
        /// <paramref name="abilityDamage"/>.
        /// </summary>
        public static long EnemyAbilityDamage(long abilityDamage, long level)
        {
            if (level < 1) level = 1;
            double scaled = abilityDamage * EnemyDamageFactor * (1f + DamagePerLevel * (level - 1));

            // A hit that rounds to nothing is a hit that never lands; keep the floor at 1.
            long rounded = (long)Math.Round(scaled);
            return rounded < 1 ? 1 : rounded;
        }
    }
}
