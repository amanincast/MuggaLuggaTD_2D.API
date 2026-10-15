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
    // Treasure maps' caches (Active Content D)
    // -----------------------------------------------------------------

    /// <summary>
    /// Opens a run against a map's cache (<c>cache:{map id}</c>). The map must be the player's, held and unopened, and a
    /// company of theirs at rest in the region it points into. A lost fight keeps the map, to try again.
    /// </summary>
    public async Task<(PveOutcome Outcome, Guid RunId, CacheEncounterDto? Encounter)> BeginCacheAsync(
        Guid gameInstanceId, string userId, PveBeginRequest request)
    {
        if (!ContractMatches(request.SharedContractVersion, out var mismatch))
            return (mismatch, Guid.Empty, null);
        if (_maps == null || !TreasureMapRules.TryParseSiteId(request.SiteId, out var mapId))
            return (new PveOutcome(PveError.LocationNotFound, "No such cache."), Guid.Empty, null);

        var state = await _maps.HeldAsync(gameInstanceId, userId, mapId);
        if (state == null)
            return (new PveOutcome(PveError.NotPveTarget, "You hold no map to that cache."), Guid.Empty, null);

        if (request.PartyId == null)
            return (new PveOutcome(PveError.NoCompanyThere, "Send a company there first."), Guid.Empty, null);
        var company = await PartyService.CompanyInRegionAsync(_context, gameInstanceId, userId, request.PartyId.Value, state.Region.RegionId);
        if (company == null)
            return (new PveOutcome(PveError.NoCompanyThere,
                "That company is not at rest where the map points. Send it there, and dig when it arrives."), Guid.Empty, null);

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
            LocationType = (int)TreasureMapRules.SiteType,
            FighterIdsJson = MarchingArmy.WriteIds(fighters),
            StartedAt = DateTime.UtcNow
        };
        _context.PveRuns.Add(run);
        await _context.SaveChangesAsync();

        var encounter = new CacheEncounterDto(mapId, state.Region.RegionId, state.Map.LandmarkSiteId, state.Region.Biome, state.Level, state.Tier);
        _logger.LogInformation("Cache run {RunId} opened by {User} for map {Map} in {Region} (tier {Tier}, level {Level}).",
            run.Id, userId, mapId, state.Region.RegionId, encounter.Tier, encounter.Level);
        return (new PveOutcome(PveError.None), run.Id, encounter);
    }

    /// <summary>
    /// Pays a won cache fight and opens the cache, once: the run's own experience, gear and materials at the cache's level
    /// and tier, twice a clear's gold in place of the run's, a Rare (one in five Legendary) chest, and now and then
    /// another map.
    /// </summary>
    private async Task<(PveOutcome Outcome, PveClaimResponse? Response)> ClaimCacheAsync(
        Guid gameInstanceId, string userId, PveRun run, PveClaimRequest request)
    {
        TreasureMapState? state = null;
        if (_maps != null && TreasureMapRules.TryParseSiteId(run.LocationId, out var mapId))
            state = await _maps.HeldAsync(gameInstanceId, userId, mapId);

        run.ClaimedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        if (state == null || !await _maps!.TryOpenAsync(state.Map.Id))
            return (new PveOutcome(PveError.LocationNotFound, "That cache has already been opened."), null);

        var rewards = RunRewardCalculator.Calculate(state.Level, state.Tier, _content.RunTuning,
            _content.DroppableItems, Random.Shared, site: TreasureMapRules.SiteType);
        var materials = MaterialRewardCalculator.Calculate(state.Level, state.Tier, _content.RunTuning,
            _content.Materials, Random.Shared, site: TreasureMapRules.SiteType);
        long gold = TreasureMapRules.CacheGold(rewards.Gold);

        var rarity = TreasureMapRules.ChestRarity(Random.Shared);
        int rosterLevel = await RealmGoalService.RosterLevelAsync(_context, _logger, gameInstanceId, userId);
        var chest = QuestRules.RollChest(_content.DroppableItems.ToList(), Random.Shared, rosterLevel, rarity);

        await _wallet.GrantAsync(gameInstanceId, userId, materials, $"cache-claim run={run.Id}");
        await _items.GrantAsync(gameInstanceId, userId, rewards.Items.Concat(chest).ToList(), $"cache-claim run={run.Id}");
        long goldBalance = await _gold.GrantAsync(gameInstanceId, userId, gold, $"cache-claim run={run.Id}");

        // Now and then the cache holds another map: a chain.
        TreasureMapView? next = null;
        if (Random.Shared.NextDouble() < TreasureMapRules.ChainChance)
            next = await _maps.GrantAsync(gameInstanceId, userId, run.LocationId, state.Map.Id);

        if (_quests != null)
        {
            await _quests.RecordAsync(gameInstanceId, userId, new QuestDeed
            {
                RegionId = state.Region.RegionId,
                Kills = new Dictionary<string, int>(_quests.ClampKills(
                    request.Kills, state.Region.Biome, state.Tier, TreasureMapRules.SiteType,
                    FactionCatalog.PeopleHolderOf(state.Region)))
            });
        }

        _logger.LogInformation("Cache opened by {User} (run {RunId}, map {Map}): {Gold} gold, {Chest} chest, {Xp} XP, chain={Chain}.",
            userId, run.Id, state.Map.Id, gold, rarity, rewards.Experience, next != null);

        var win = new CacheWinDto(state.Map.Id, state.Region.RegionId, gold, rarity, chest, next);
        return (new PveOutcome(PveError.None), new PveClaimResponse(
            run.LocationId, nameof(ConquestOutcome.RemoveLocation), rewards.Experience, rewards.Items, 0, materials,
            gold, goldBalance, WorldRewards: false, LockedUntil: null, WorldRewardsBackAt: null, Cache: win));
    }
}
