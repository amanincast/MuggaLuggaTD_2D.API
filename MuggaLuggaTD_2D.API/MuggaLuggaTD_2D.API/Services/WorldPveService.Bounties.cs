using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using System.Text.Json.Nodes;

namespace MuggaLuggaTD_2D.API.Services;

public partial class WorldPveService
{
    // -----------------------------------------------------------------
    // Wanted champions (Active Content A)
    // -----------------------------------------------------------------

    /// <summary>
    /// Opens a run against a wanted champion (<c>bounty:{slot}</c>). It must be roaming and unclaimed; the
    /// player may go after it once every two hours; and a company must be at rest in the region it stands
    /// in now. Returns the encounter, so the client builds the fight the server will pay for.
    /// </summary>
    public async Task<(PveOutcome Outcome, Guid RunId, BountyEncounterDto? Encounter)> BeginBountyAsync(
        Guid gameInstanceId, string userId, PveBeginRequest request)
    {
        if (!ContractMatches(request.SharedContractVersion, out var mismatch))
            return (mismatch, Guid.Empty, null);
        if (_bounties == null || !BountyRules.TryParseSiteId(request.SiteId, out long slot))
            return (new PveOutcome(PveError.LocationNotFound, "No such champion."), Guid.Empty, null);

        var now = DateTime.UtcNow;
        var state = await _bounties.StateAsync(gameInstanceId, slot, now);
        if (state == null)
            return (new PveOutcome(PveError.LocationNotFound, "No such champion."), Guid.Empty, null);
        var champion = state.Champion;
        if (state.Row?.ClaimedByUserId != null)
            return (new PveOutcome(PveError.NotPveTarget, $"{champion.Name} has already been brought down."), Guid.Empty, null);
        if (!BountyRules.IsLive(slot, now, state.SeasonStart))
            return (new PveOutcome(PveError.NotPveTarget, now < BountyRules.SlotStart(slot)
                ? "No such champion has been seen yet."
                : $"{champion.Name} has escaped."), Guid.Empty, null);

        // One attempt every two hours: lose, and wait.
        var last = await _bounties.LastAttemptAsync(gameInstanceId, userId, request.SiteId, now);
        if (last != null)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((last.Value + BountyRules.AttemptEvery - now).TotalMinutes));
            return (new PveOutcome(PveError.SiteLocked,
                $"You went after {champion.Name} lately. You may try again in {minutes} min."), Guid.Empty, null);
        }

        if (request.PartyId == null)
            return (new PveOutcome(PveError.NoCompanyThere, "Send a company there first."), Guid.Empty, null);
        var company = await PartyService.CompanyInRegionAsync(_context, gameInstanceId, userId, request.PartyId.Value, state.RegionId);
        if (company == null)
            return (new PveOutcome(PveError.NoCompanyThere,
                $"That company is not at rest where {champion.Name} stands. Send it there, and fight when it arrives."), Guid.Empty, null);

        var worldRow = await _context.WorldViewGameData.AsNoTracking().FirstAsync(w => w.GameInstanceId == gameInstanceId);
        var fighters = MarchingArmy.ReadIds(company.CharacterIdsJson)
            .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        var why = await PartyService.WhyCannotFightAsync(_context, _logger, JsonNode.Parse(worldRow.GameData), gameInstanceId, userId, fighters);
        if (why != null)
            return (new PveOutcome(PveError.FightersUnavailable, why), Guid.Empty, null);

        var run = new PveRun
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            LocationId = request.SiteId,
            LocationType = (int)BountyRules.SiteType,
            FighterIdsJson = MarchingArmy.WriteIds(fighters),
            StartedAt = now
        };
        _context.PveRuns.Add(run);
        await _context.SaveChangesAsync();

        var encounter = EncounterOf(state);
        _logger.LogInformation("Bounty run {RunId} opened by {User} against {Champion} in {Region} (tier {Tier}, {Mutators}).",
            run.Id, userId, champion.Name, state.RegionId, encounter.Tier, string.Join("+", encounter.Mutators));
        return (new PveOutcome(PveError.None), run.Id, encounter);
    }

    private BountyEncounterDto EncounterOf(BountyState state)
    {
        var (tier, level, gold) = _bounties!.Terms(state);
        var c = state.Champion;
        return new BountyEncounterDto(c.Slot, c.Name, c.Title, c.People, c.Biome, state.RegionId, level, tier, c.Mutators, gold);
    }

    /// <summary>
    /// Pays a won fight with a champion. The first win claims it: the bounty in gold, a Rare (one in five
    /// Legendary) chest and season points (the controller awards those, and tells the realm). A player who
    /// went in before it was claimed, and won after, gets half the gold and no chest. The run's own
    /// experience, gear and materials are paid either way, at the level and tier it was fought at.
    /// </summary>
    private async Task<(PveOutcome Outcome, PveClaimResponse? Response)> ClaimBountyAsync(
        Guid gameInstanceId, string userId, string? displayName, PveRun run, PveClaimRequest request)
    {
        BountyState? state = null;
        if (_bounties != null && BountyRules.TryParseSiteId(run.LocationId, out long slot))
            state = await _bounties.StateAsync(gameInstanceId, slot, run.StartedAt);

        var now = DateTime.UtcNow;
        run.ClaimedAt = now;
        await _context.SaveChangesAsync();
        if (state == null)
            return (new PveOutcome(PveError.LocationNotFound, "That champion is gone."), null);

        var encounter = EncounterOf(state);
        bool first = await _bounties!.TryClaimAsync(gameInstanceId, state.Season, encounter.Slot, userId, state.RegionId);
        string? claimedBy = null;
        if (!first)
        {
            var after = await _bounties.StateAsync(gameInstanceId, encounter.Slot, now);
            var winner = after?.Row?.ClaimedByUserId;
            if (winner != null)
            {
                var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == winner);
                claimedBy = user == null ? "A herald" : PlayerNames.Of(user.DisplayName, user.UserName);
            }
        }
        else claimedBy = displayName;

        var rewards = RunRewardCalculator.Calculate(encounter.Level, encounter.Tier, _content.RunTuning,
            _content.DroppableItems, Random.Shared, site: BountyRules.SiteType);
        var materials = MaterialRewardCalculator.Calculate(encounter.Level, encounter.Tier, _content.RunTuning,
            _content.Materials, Random.Shared, site: BountyRules.SiteType);
        long gold = first ? encounter.Gold : BountyRules.Consolation(encounter.Gold);

        Enums.ItemRarityTypes? chestRarity = null;
        var chest = new List<StateManagement.Models.ItemSaveData>();
        if (first)
        {
            chestRarity = BountyRules.ChestRarity(Random.Shared);
            int level = await RealmGoalService.RosterLevelAsync(_context, _logger, gameInstanceId, userId);
            chest = QuestRules.RollChest(_content.DroppableItems.ToList(), Random.Shared, level, chestRarity.Value);
        }

        await _wallet.GrantAsync(gameInstanceId, userId, materials, $"bounty-claim run={run.Id}");
        await _items.GrantAsync(gameInstanceId, userId, rewards.Items.Concat(chest).ToList(), $"bounty-claim run={run.Id}");
        long goldBalance = await _gold.GrantAsync(gameInstanceId, userId, gold, $"bounty-claim run={run.Id}");

        if (_quests != null)
        {
            await _quests.RecordAsync(gameInstanceId, userId, new QuestDeed
            {
                RegionId = state.RegionId,
                Kills = new Dictionary<string, int>(_quests.ClampKills(
                    request.Kills, encounter.Biome, encounter.Tier, BountyRules.SiteType))
            });
        }

        _logger.LogInformation(
            "Bounty win over {Champion} by {User} (run {RunId}): first={First}, {Gold} gold, chest {Chest}, {Xp} XP.",
            encounter.Name, userId, run.Id, first, gold, chestRarity?.ToString() ?? "none", rewards.Experience);

        var win = new BountyWinDto(encounter.Slot, encounter.Name, encounter.Title, first, gold, chestRarity, chest, claimedBy, state.RegionId);
        return (new PveOutcome(PveError.None), new PveClaimResponse(
            run.LocationId, nameof(ConquestOutcome.RemoveLocation), rewards.Experience, rewards.Items, 0, materials,
            gold, goldBalance, WorldRewards: first, LockedUntil: null, WorldRewardsBackAt: null, Bounty: win));
    }
}
