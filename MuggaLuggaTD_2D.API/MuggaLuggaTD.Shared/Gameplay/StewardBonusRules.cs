using System;
using System.Collections.Generic;
using System.Linq;
using MuggaLuggaTD.Shared.World;
using StateManagement.Models;

namespace MuggaLuggaTD.Shared.Gameplay
{
    /// <summary>What a steward's class makes it at its keep (Reclaiming phase 4, future-work item 9).</summary>
    public enum StewardCalling
    {
        None = 0,

        /// <summary>A Warrior: the region's hold is stronger.</summary>
        Warden = 1,

        /// <summary>An Archer: the region's resource sites yield more goods.</summary>
        Huntmaster = 2,

        /// <summary>A Mage: the region pays more gold.</summary>
        Magister = 3,

        /// <summary>A Cleric: the region's resolve is worn down less, and built back faster.</summary>
        Chaplain = 4,
    }

    /// <summary>
    /// <para><b>A steward's bonus by class</b> (Reclaiming phase 4, spec §7; Mike 2026-11-16: "I would trust your initial
    /// numbers"). The hero stationed as steward at the keep of land claimed from the wild (<see cref="StewardRules"/>) gives
    /// that region one bonus, by its class, as long as it stands there:</para>
    /// <list type="bullet">
    /// <item><b>Warrior, the Warden:</b> hold +10%, up to +20%. It counts against sieges, raids and a returning horde.</item>
    /// <item><b>Archer, the Huntmaster:</b> goods from the region's resource sites +15%, up to +30%.</item>
    /// <item><b>Mage, the Magister:</b> the region's gold +15%, up to +30%. Season points are not touched.</item>
    /// <item><b>Cleric, the Chaplain:</b> resolve lost to raids and the tide -30%, up to -50%; resolve restored by
    /// clearing the region's own sites +30%, up to +50%.</item>
    /// </list>
    /// <para><b>It grows with the steward's level:</b> the low figure at the level the land needs
    /// (<see cref="StewardRules.LevelFor"/>), rising evenly to the high one at <see cref="LevelsToFull"/> levels above it.
    /// The class and level are read when the keep is garrisoned and stored on the region
    /// (<see cref="WorldRegionData.StewardClass"/>, <see cref="WorldRegionData.StewardLevel"/>), so both sides agree without
    /// the roster. A stationed hero doesn't fight, so its level doesn't move while it stands there.</para>
    /// <para>Only stewarded land has a bonus: the seat and its ring need no steward, and a keep garrisoned there names none.</para>
    /// </summary>
    public static class StewardBonusRules
    {
        /// <summary>Levels above the land's steward level at which a bonus is at its fullest.</summary>
        public const int LevelsToFull = 10;

        public const double WardenHoldLow = 0.10, WardenHoldHigh = 0.20;
        public const double HuntmasterGoodsLow = 0.15, HuntmasterGoodsHigh = 0.30;
        public const double MagisterGoldLow = 0.15, MagisterGoldHigh = 0.30;
        public const double ChaplainLossLow = 0.30, ChaplainLossHigh = 0.50;
        public const double ChaplainGainLow = 0.30, ChaplainGainHigh = 0.50;

        /// <summary>The calling of a hero of this class name ("Warrior", "Archer", "Mage", "Cleric"); None for anything else.</summary>
        public static StewardCalling CallingOf(string className)
        {
            if (string.IsNullOrEmpty(className)) return StewardCalling.None;
            switch (className.Trim().ToLowerInvariant())
            {
                case "warrior": return StewardCalling.Warden;
                case "archer": return StewardCalling.Huntmaster;
                case "mage": return StewardCalling.Magister;
                case "cleric": return StewardCalling.Chaplain;
                default: return StewardCalling.None;
            }
        }

        /// <summary>
        /// A hero's class: its signature's class, or else the class of the recruit sheet it was made from (a hero from
        /// before signatures). Null when neither says.
        /// </summary>
        public static string ClassOf(CharacterSaveData hero, IEnumerable<SignatureDefinition> signatures, IEnumerable<RecruitSheet> sheets)
        {
            if (hero == null) return null;
            if (!string.IsNullOrEmpty(hero.SignatureId) && signatures != null)
            {
                var signature = signatures.FirstOrDefault(s => s != null && s.SignatureId == hero.SignatureId);
                if (!string.IsNullOrEmpty(signature?.Class)) return signature.Class;
            }
            if (!string.IsNullOrEmpty(hero.LinkName) && sheets != null)
            {
                var sheet = sheets.FirstOrDefault(s => s != null && string.Equals(s.Sheet, hero.LinkName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(sheet?.Class)) return sheet.Class;
            }
            return null;
        }

        /// <summary>The calling at work in this region now: its steward stands at the keep of land held. None otherwise.</summary>
        public static StewardCalling CallingAt(WorldRegionData region)
        {
            if (region == null || region.Ownership != LocationOwnership.Player || !region.StewardNeeded) return StewardCalling.None;
            if (!StewardRules.HasSteward(region) || HordeRules.IsOverrun(region)) return StewardCalling.None;
            return CallingOf(region.StewardClass);
        }

        /// <summary>How far the bonus has grown, 0 at the land's steward level to 1 at <see cref="LevelsToFull"/> above it.</summary>
        public static double Growth(int stewardLevel, int tier)
        {
            int above = stewardLevel - StewardRules.LevelFor(tier);
            if (above <= 0) return 0;
            if (above >= LevelsToFull) return 1;
            return (double)above / LevelsToFull;
        }

        /// <summary>The bonus of a calling at a level on land of a tier: +0.10 for "+10%". Zero for None.</summary>
        public static double Bonus(StewardCalling calling, int stewardLevel, int tier)
        {
            double g = Growth(stewardLevel, tier);
            switch (calling)
            {
                case StewardCalling.Warden: return Lerp(WardenHoldLow, WardenHoldHigh, g);
                case StewardCalling.Huntmaster: return Lerp(HuntmasterGoodsLow, HuntmasterGoodsHigh, g);
                case StewardCalling.Magister: return Lerp(MagisterGoldLow, MagisterGoldHigh, g);
                case StewardCalling.Chaplain: return Lerp(ChaplainLossLow, ChaplainLossHigh, g);
                default: return 0;
            }
        }

        /// <summary>The bonus at work in this region now (see <see cref="Bonus"/>), or zero.</summary>
        public static double BonusAt(WorldRegionData region)
        {
            var calling = CallingAt(region);
            return calling == StewardCalling.None ? 0 : Bonus(calling, region.StewardLevel, region.Tier);
        }

        /// <summary>What the region's hold is multiplied by: a Warden's 1.10 to 1.20, or 1.</summary>
        public static double HoldFactor(WorldRegionData region) =>
            CallingAt(region) == StewardCalling.Warden ? 1.0 + BonusAt(region) : 1.0;

        /// <summary>What its resource sites' goods are multiplied by: a Huntmaster's 1.15 to 1.30, or 1.</summary>
        public static double GoodsFactor(WorldRegionData region) =>
            CallingAt(region) == StewardCalling.Huntmaster ? 1.0 + BonusAt(region) : 1.0;

        /// <summary>What its gold is multiplied by: a Magister's 1.15 to 1.30, or 1.</summary>
        public static double GoldFactor(WorldRegionData region) =>
            CallingAt(region) == StewardCalling.Magister ? 1.0 + BonusAt(region) : 1.0;

        /// <summary>What resolve lost there is multiplied by: a Chaplain's 0.70 to 0.50, or 1.</summary>
        public static double ResolveLossFactor(WorldRegionData region) =>
            CallingAt(region) == StewardCalling.Chaplain ? 1.0 - BonusAt(region) : 1.0;

        /// <summary>What resolve restored there by a clear is multiplied by: a Chaplain's 1.30 to 1.50, or 1.</summary>
        public static double ResolveGainFactor(WorldRegionData region)
        {
            if (CallingAt(region) != StewardCalling.Chaplain) return 1.0;
            return 1.0 + Lerp(ChaplainGainLow, ChaplainGainHigh, Growth(region.StewardLevel, region.Tier));
        }

        /// <summary>A loss of resolve, softened by the region's Chaplain: whole points, rounded to the nearest.</summary>
        public static int SoftenLoss(WorldRegionData region, int loss)
        {
            if (loss <= 0) return loss;
            return (int)Math.Round(loss * ResolveLossFactor(region), MidpointRounding.AwayFromZero);
        }

        /// <summary>Resolve restored by a clear, raised by the region's Chaplain: whole points, rounded to the nearest.</summary>
        public static int RaiseGain(WorldRegionData region, int gain)
        {
            if (gain <= 0) return gain;
            return (int)Math.Round(gain * ResolveGainFactor(region), MidpointRounding.AwayFromZero);
        }

        /// <summary>The calling's title: "Warden".</summary>
        public static string TitleOf(StewardCalling calling) => calling == StewardCalling.None ? "" : calling.ToString();

        /// <summary>
        /// What a calling does, at a level on land of a tier: "Warden · hold +14%". Empty for None. One line for the dossier,
        /// the garrison picker and the codex.
        /// </summary>
        public static string Describe(StewardCalling calling, int stewardLevel, int tier)
        {
            if (calling == StewardCalling.None) return "";
            int pct = Percent(Bonus(calling, stewardLevel, tier));
            switch (calling)
            {
                case StewardCalling.Warden: return $"Warden · hold +{pct}%";
                case StewardCalling.Huntmaster: return $"Huntmaster · goods +{pct}%";
                case StewardCalling.Magister: return $"Magister · gold +{pct}%";
                case StewardCalling.Chaplain:
                    int gain = Percent(Lerp(ChaplainGainLow, ChaplainGainHigh, Growth(stewardLevel, tier)));
                    return $"Chaplain · resolve loss -{pct}%, restored +{gain}%";
                default: return "";
            }
        }

        /// <summary>What the region's steward gives it now ("Warden · hold +14%"), or empty.</summary>
        public static string DescribeAt(WorldRegionData region)
        {
            var calling = CallingAt(region);
            return calling == StewardCalling.None ? "" : Describe(calling, region.StewardLevel, region.Tier);
        }

        /// <summary>What each class would give as a steward, in a word: for the codex and the garrison picker.</summary>
        public static string WhatItGives(StewardCalling calling)
        {
            switch (calling)
            {
                case StewardCalling.Warden: return "a stronger hold";
                case StewardCalling.Huntmaster: return "more goods from its sites";
                case StewardCalling.Magister: return "more gold";
                case StewardCalling.Chaplain: return "steadier resolve";
                default: return "nothing more";
            }
        }

        /// <summary>A bonus as whole percent, half up: 0.165 is 17 (the nudge keeps 0.165 from reading as 16.4999).</summary>
        private static int Percent(double bonus) => (int)Math.Round(bonus * 100 + 1e-9, MidpointRounding.AwayFromZero);

        private static double Lerp(double low, double high, double t) => low + (high - low) * t;
    }
}
