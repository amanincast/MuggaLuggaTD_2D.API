using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// The raid rules themselves, as pure arithmetic.
///
/// <para>These are the numbers the dossier shows a player before they march and the numbers the
/// server applies when they do — one implementation, so the two cannot disagree. What is being
/// pinned here is mostly <i>bounds</i>: the design's whole defence against a server that cannot
/// referee a fight is that no single raid is worth very much.</para>
/// </summary>
public class RaidResolverTests
{
    // -----------------------------------------------------------------
    // The bar
    // -----------------------------------------------------------------

    [Fact]
    public void TheRaidBarIsHalfTheSiegeGate()
    {
        // "Requires roughly half the gate — a lower bar than a siege."
        long hold = 10_000;

        Assert.Equal(RegionHoldCalculator.SiegeGate(hold) / 2, RaidResolver.RaidBar(hold));
    }

    [Fact]
    public void AnAttackerAtTheBarMayRaid_AndOneBelowItMayNot()
    {
        long hold = 5_000;
        long bar = RaidResolver.RaidBar(hold);

        Assert.True(RaidResolver.ClearsBar(bar, hold));
        Assert.True(RaidResolver.ClearsBar(bar + 1, hold));
        Assert.False(RaidResolver.ClearsBar(bar - 1, hold));
    }

    [Fact]
    public void AnUndefendedRegionStillHasABarToClear()
    {
        // The hold floor means even an empty region asks for something. A raid bar of zero would be
        // the "walks in with nothing" hole the floor was added to close, reached from a new door.
        long hold = RegionHoldCalculator.Hold(garrisonPower: 0, tier: 1, entrenchment: 0, supply: 1.0, resolve: 100);

        Assert.True(RaidResolver.RaidBar(hold) > 0);
        Assert.False(RaidResolver.ClearsBar(0, hold));
    }

    // -----------------------------------------------------------------
    // The damage
    // -----------------------------------------------------------------

    [Fact]
    public void ARaidThatBarelyClearsTheBarDoesTheLeastDamage()
    {
        long hold = 8_000;

        Assert.Equal(RaidResolver.MinimumResolveDamage,
            RaidResolver.ResolveDamage(RaidResolver.RaidBar(hold), hold));
    }

    [Fact]
    public void ARaidBringingTheWholeHoldDoesTheMost()
    {
        long hold = 8_000;

        Assert.Equal(RaidResolver.MaximumResolveDamage, RaidResolver.ResolveDamage(hold, hold));
    }

    [Fact]
    public void NoRaidEverDoesMoreThanTheMaximum()
    {
        // The bound is the design. An overwhelming attacker raids faster; they never raid a region
        // out of existence in one march, however far ahead they are.
        long hold = 1_000;

        foreach (var march in new double[] { 1_000, 10_000, 1_000_000, double.MaxValue / 2 })
            Assert.Equal(RaidResolver.MaximumResolveDamage, RaidResolver.ResolveDamage(march, hold));
    }

    [Fact]
    public void DamageRisesWithTheAttackersStrength()
    {
        long hold = 10_000;
        long bar = RaidResolver.RaidBar(hold);

        var atBar = RaidResolver.ResolveDamage(bar, hold);
        var halfway = RaidResolver.ResolveDamage((bar + hold) / 2.0, hold);
        var atHold = RaidResolver.ResolveDamage(hold, hold);

        Assert.True(atBar < halfway, $"{atBar} should be less than {halfway}");
        Assert.True(halfway < atHold, $"{halfway} should be less than {atHold}");
    }

    [Fact]
    public void EveryPossibleRaidStaysInsideItsBounds()
    {
        long hold = 6_000;

        for (double march = 0; march <= hold * 3; march += 97)
        {
            var damage = RaidResolver.ResolveDamage(march, hold);
            Assert.InRange(damage, RaidResolver.MinimumResolveDamage, RaidResolver.MaximumResolveDamage);
        }
    }

    // -----------------------------------------------------------------
    // Resolving one
    // -----------------------------------------------------------------

    [Fact]
    public void ARepelledRaidChangesNothing()
    {
        // A roll of 1 against a much stronger defender loses. Losing costs the attacker their
        // cooldown and nothing else — there is no consolation attrition.
        var result = RaidResolver.Resolve(marchingPower: 3_000, hold: 10_000, resolveBefore: 80, d20Roll: 1);

        Assert.False(result.AttackerWins);
        Assert.Equal(0, result.ResolveDamage);
        Assert.Equal(80, result.ResolveAfter);
    }

    [Fact]
    public void AWinningRaidTakesResolveOffTheRegion()
    {
        var result = RaidResolver.Resolve(marchingPower: 10_000, hold: 10_000, resolveBefore: 80, d20Roll: 20);

        Assert.True(result.AttackerWins);
        Assert.Equal(RaidResolver.MaximumResolveDamage, result.ResolveDamage);
        Assert.Equal(80 - RaidResolver.MaximumResolveDamage, result.ResolveAfter);
    }

    [Fact]
    public void ARaidCannotDriveResolveBelowZero()
    {
        var result = RaidResolver.Resolve(marchingPower: 10_000, hold: 10_000, resolveBefore: 3, d20Roll: 20);

        Assert.Equal(0, result.ResolveAfter);

        // And it reports what it actually took, not what it rolled — the log has to add up.
        Assert.Equal(3, result.ResolveDamage);
    }

    [Fact]
    public void ARaidOnAnAlreadyBrokenRegionTakesNothingFurther()
    {
        var result = RaidResolver.Resolve(marchingPower: 10_000, hold: 10_000, resolveBefore: 0, d20Roll: 20);

        Assert.True(result.AttackerWins);
        Assert.Equal(0, result.ResolveDamage);
        Assert.Equal(0, result.ResolveAfter);
    }

    [Fact]
    public void TheFightIsSettledByTheSameDiceEverythingElseUses()
    {
        // A raid is a contest, not a guaranteed tick of damage for anyone who clears the bar.
        for (int roll = 1; roll <= 20; roll++)
        {
            var result = RaidResolver.Resolve(marchingPower: 5_000, hold: 5_000, resolveBefore: 100, d20Roll: roll);
            var fight = PassivePvPResolver.Resolve(5_000, 5_000, roll);

            Assert.Equal(fight.AttackerWins, result.AttackerWins);
            Assert.Equal(fight.Total, result.Total);
            Assert.Equal(fight.Modifier, result.Modifier);
            Assert.Equal(roll, result.D20Roll);
        }
    }

    [Fact]
    public void EvenAMatchedRaidCanBeRepelled()
    {
        // At equal power the modifier is zero, so the low half of the die loses. Raiding is a
        // campaign because each step can fail, not only because of the cooldown.
        int repelled = 0;
        for (int roll = 1; roll <= 20; roll++)
        {
            if (!RaidResolver.Resolve(5_000, 5_000, 100, roll).AttackerWins) repelled++;
        }

        Assert.Equal(10, repelled);
    }

    // -----------------------------------------------------------------
    // How long it takes to wear a region down
    // -----------------------------------------------------------------

    [Fact]
    public void WearingARegionDownTakesAnAttackerSeveralRaidsAcrossHours()
    {
        // The design's claim, checked: "taking a region is five-plus successful raids across a day
        // or more of real time". An attacker who is comfortably ahead still cannot do it in one
        // sitting, because the cooldown gates every step.
        long hold = RegionHoldCalculator.Hold(garrisonPower: 2_000, tier: 2, entrenchment: 2, supply: 1.0, resolve: 100);
        double march = hold; // as strong as the region itself, so raids land for maximum damage

        int resolve = RegionResolveRules.Maximum;
        int raids = 0;
        while (resolve > 25 && raids < 100)
        {
            resolve = RaidResolver.Resolve(march, hold, resolve, d20Roll: 20).ResolveAfter;
            raids++;
        }

        Assert.True(raids >= 5, $"Only {raids} raids to grind a region down — too few.");
        Assert.True(raids * RaidResolver.CooldownHours >= 20,
            "Wearing a region down should span the better part of a day of real time.");
    }
}
