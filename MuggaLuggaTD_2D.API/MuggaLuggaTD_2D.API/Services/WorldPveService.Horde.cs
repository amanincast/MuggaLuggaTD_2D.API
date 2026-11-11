using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;
using System.Text.Json.Nodes;

namespace MuggaLuggaTD_2D.API.Services;

public partial class WorldPveService
{
    // -----------------------------------------------------------------
    // Reclaiming your home region (Mike 2026-11-10)
    // -----------------------------------------------------------------

    /// <summary>
    /// Opens a fight to free a site from the horde (<c>horde:{siteId}</c>). The region must be overrun and the
    /// player's, the site held, and a company of theirs standing at it. The lair waits for the rest. There is no
    /// lockout: a lost fight costs nothing and may be tried again at once (row 6).
    /// </summary>
    public async Task<(PveOutcome Outcome, Guid RunId, HordeEncounterDto? Encounter)> BeginHordeAsync(
        Guid gameInstanceId, string userId, PveBeginRequest request)
    {
        if (!ContractMatches(request.SharedContractVersion, out var mismatch))
            return (mismatch, Guid.Empty, null);

        var worldRow = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (worldRow == null)
            return (new PveOutcome(PveError.WorldNotFound, "World view data not found."), Guid.Empty, null);
        var world = JsonNode.Parse(worldRow.GameData);

        string? target = HordeRules.TargetOf(request.SiteId);
        var resolved = WorldRegionBlob.ResolveSite(world, target);
        if (resolved == null)
            return (new PveOutcome(PveError.LocationNotFound, "Site not found in this world."), Guid.Empty, null);

        var layout = RegionGenerator.Generate(resolved.Region);
        string? why = HordeRules.WhyNotFree(resolved.Region, layout, target!, userId);
        if (why != null)
            return (new PveOutcome(PveError.NotPveTarget, why), Guid.Empty, null);

        if (request.PartyId == null)
            return (new PveOutcome(PveError.NoCompanyThere, "Send a company there first."), Guid.Empty, null);
        var company = await PartyService.CompanyAtAsync(_context, gameInstanceId, userId, request.PartyId.Value, target!);
        if (company == null)
            return (new PveOutcome(PveError.NoCompanyThere, "That company is not standing there. Send it, and fight when it arrives."), Guid.Empty, null);

        var fighters = MarchingArmy.ReadIds(company.CharacterIdsJson)
            .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        var unavailable = await PartyService.WhyCannotFightAsync(_context, _logger, world, gameInstanceId, userId, fighters);
        if (unavailable != null)
            return (new PveOutcome(PveError.FightersUnavailable, unavailable), Guid.Empty, null);

        var existing = await _context.PveRuns
            .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId && r.LocationId == request.SiteId && r.ClaimedAt == null)
            .ToListAsync();
        if (existing.Count > 0) _context.PveRuns.RemoveRange(existing);

        var run = new PveRun
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            LocationId = request.SiteId,
            LocationType = (int)HordeRules.SiteType,
            FighterIdsJson = MarchingArmy.WriteIds(fighters),
            StartedAt = DateTime.UtcNow
        };
        _context.PveRuns.Add(run);
        await _context.SaveChangesAsync();

        var encounter = HordeEncounterOf(resolved, layout);
        _logger.LogInformation("Horde run {RunId} opened by {User} at {Site} (level {Level}, lair {Lair}).",
            run.Id, userId, target, encounter.Level, encounter.IsLair);
        return (new PveOutcome(PveError.None), run.Id, encounter);
    }

    private static HordeEncounterDto HordeEncounterOf(SiteResolution resolved, RegionLayout layout) => new(
        resolved.Site.SiteId,
        resolved.Region.RegionId,
        resolved.Site.Type,
        resolved.Region.Biome,
        HordeRules.LevelAt(resolved.Region, layout, resolved.Site.SiteId),
        Math.Max(1, resolved.Region.Tier),
        HordeRules.IsLair(layout, resolved.Site.SiteId));

    /// <summary>
    /// Pays a won horde fight and frees its site. The lair's win reclaims the region: a Rare chest at its level and
    /// <see cref="HordeRules.ReclaimGold"/> gold (row 5). Returns the changed world, for the caller to persist and
    /// broadcast as any change to the land is.
    /// </summary>
    private async Task<(PveOutcome Outcome, PveClaimResponse? Response, JsonNode? World)> ClaimHordeAsync(
        Guid gameInstanceId, string userId, PveRun run, PveClaimRequest request)
    {
        var now = DateTime.UtcNow;
        run.ClaimedAt = now;

        var worldRow = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (worldRow == null)
        {
            await _context.SaveChangesAsync();
            return (new PveOutcome(PveError.WorldNotFound, "World view data not found."), null, null);
        }
        var world = JsonNode.Parse(worldRow.GameData);
        string? target = HordeRules.TargetOf(run.LocationId);
        var resolved = WorldRegionBlob.ResolveSite(world, target);
        if (resolved == null)
        {
            await _context.SaveChangesAsync();
            return (new PveOutcome(PveError.LocationNotFound, "That site no longer exists."), null, null);
        }

        var layout = RegionGenerator.Generate(resolved.Region);
        string? why = HordeRules.WhyNotFree(resolved.Region, layout, target!, userId);
        if (why != null)
        {
            await _context.SaveChangesAsync();
            return (new PveOutcome(PveError.NotPveTarget, why), null, null);
        }

        var encounter = HordeEncounterOf(resolved, layout);
        WorldRegionBlob.FreeHordeSite(resolved.RegionNode, target!);
        if (encounter.IsLair) WorldRegionBlob.Reclaim(resolved.RegionNode, now);

        var rewards = RunRewardCalculator.Calculate(encounter.Level, encounter.Tier, _content.RunTuning,
            _content.DroppableItems, Random.Shared, site: HordeRules.SiteType);
        var materials = MaterialRewardCalculator.Calculate(encounter.Level, encounter.Tier, _content.RunTuning,
            _content.Materials, Random.Shared, site: HordeRules.SiteType);

        // The reclaim reward (row 5): something to look forward to, and the step into the game proper.
        var chest = new List<StateManagement.Models.ItemSaveData>();
        Enums.ItemRarityTypes? chestRarity = null;
        long gold = rewards.Gold;
        if (encounter.IsLair)
        {
            chestRarity = Enums.ItemRarityTypes.Rare;
            chest = QuestRules.RollChest(_content.DroppableItems.ToList(), Random.Shared, encounter.Level, chestRarity.Value);
            gold += HordeRules.ReclaimGold;
        }

        await _wallet.GrantAsync(gameInstanceId, userId, materials, $"horde-claim run={run.Id}");
        await _items.GrantAsync(gameInstanceId, userId, rewards.Items.Concat(chest).ToList(), $"horde-claim run={run.Id}");
        long goldBalance = await _gold.GrantAsync(gameInstanceId, userId, gold, $"horde-claim run={run.Id}");

        // Freeing your land counts as First Steps' "clear" (the ledger's fight).
        if (_firstSteps != null) await _firstSteps.RecordAsync(gameInstanceId, userId, FirstStepsRules.Clear);

        if (_quests != null)
        {
            await _quests.RecordAsync(gameInstanceId, userId, new QuestDeed
            {
                RegionId = resolved.Region.RegionId,
                Kills = new Dictionary<string, int>(_quests.ClampKills(
                    request.Kills, resolved.Region.Biome, encounter.Tier, HordeRules.SiteType))
            });
        }
        await _context.SaveChangesAsync();

        var after = WorldRegionBlob.ReadRegion(resolved.RegionNode);
        var win = new HordeWinDto(target!, resolved.Region.RegionId, encounter.IsLair,
            HordeRules.FreedCount(after, layout), HordeRules.Total(layout), HordeRules.LairOpen(after, layout),
            chestRarity, chest, encounter.IsLair ? HordeRules.ReclaimGold : 0);

        _logger.LogInformation("Horde site {Site} freed by {User} (run {RunId}): {Freed}/{Total}, reclaimed={Reclaimed}, {Xp} XP, {Gold} gold.",
            target, userId, run.Id, win.Freed, win.Total, win.Reclaimed, rewards.Experience, gold);

        return (new PveOutcome(PveError.None), new PveClaimResponse(
            run.LocationId, nameof(ConquestOutcome.RemoveLocation), rewards.Experience, rewards.Items, 0, materials,
            gold, goldBalance, WorldRewards: true, LockedUntil: null, WorldRewardsBackAt: null, Horde: win), world);
    }
}
