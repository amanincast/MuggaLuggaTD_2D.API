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

        // The lair counts its tries: every one before the win was lost or given up, and from the third loss the
        // warlord eases (HordeRules.LairEase). Counted here, as a loss is never reported, only a win claimed.
        if (HordeRules.IsLair(resolved.Region, layout, target))
        {
            var tracked = await _context.WorldViewGameData.FirstAsync(w => w.GameInstanceId == gameInstanceId);
            var live = JsonNode.Parse(tracked.GameData)!;
            var liveSite = WorldRegionBlob.ResolveSite(live, target)!;
            WorldRegionBlob.TryLair(liveSite.RegionNode, userId);
            tracked.GameData = live.ToJsonString();
            tracked.UpdatedAt = DateTime.UtcNow;
            resolved = WorldRegionBlob.ResolveSite(live, target)!;
        }
        await _context.SaveChangesAsync();

        var encounter = HordeEncounterOf(resolved, layout, userId);
        int tries = HordeRules.IsWild(resolved.Region)
            ? HordeRules.StakeOf(resolved.Region, userId)?.LairTries ?? 0
            : resolved.Region.Horde?.LairTries ?? 0;
        _logger.LogInformation("Horde run {RunId} opened by {User} at {Site} (level {Level}, lair {Lair}, wild {Wild}, try {Try}, eased {Eased}).",
            run.Id, userId, target, encounter.Level, encounter.IsLair, HordeRules.IsWild(resolved.Region), tries,
            HordeRules.LairEase(resolved.Region, userId: userId));
        return (new PveOutcome(PveError.None), run.Id, encounter);
    }

    private static HordeEncounterDto HordeEncounterOf(SiteResolution resolved, RegionLayout layout, string userId) => new(
        resolved.Site.SiteId,
        resolved.Region.RegionId,
        resolved.Site.Type,
        resolved.Region.Biome,
        HordeRules.LevelAt(resolved.Region, layout, resolved.Site.SiteId, userId: userId),
        Math.Max(1, resolved.Region.Tier),
        HordeRules.IsLair(resolved.Region, layout, resolved.Site.SiteId));

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

        var encounter = HordeEncounterOf(resolved, layout, userId);
        WorldRegionBlob.FreeHordeSite(resolved.RegionNode, target!);
        if (encounter.IsLair) WorldRegionBlob.Reclaim(resolved.RegionNode, now);

        // Wild land (phase 2): the warlord's fall cleanses the region for everyone, and pays its sharers.
        if (HordeRules.IsWild(resolved.Region))
            return await CleanseAsync(gameInstanceId, userId, run, request, resolved, layout, encounter, world!);

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

        // Freeing your land counts as First Steps' "clear" (the ledger's fight); the warlord's fall is "reclaim".
        if (_firstSteps != null) await _firstSteps.RecordAsync(gameInstanceId, userId, FirstStepsRules.Clear);
        if (_firstSteps != null && encounter.IsLair) await _firstSteps.RecordAsync(gameInstanceId, userId, FirstStepsRules.Reclaim);

        // The reclaim reward's free worker (row 5), at work at the first resource site freed.
        (Models.HiredWorker Worker, string SiteId)? given = null;
        if (encounter.IsLair && _hiring != null)
            given = await _hiring.GrantReclaimWorkerAsync(gameInstanceId, userId, WorldRegionBlob.ReadRegion(resolved.RegionNode),
                WorldRegionBlob.ReadAllRegions(world));

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
            HordeRules.FreedCount(after, layout), HordeRules.Total(after, layout), HordeRules.LairOpen(after, layout),
            chestRarity, chest, encounter.IsLair ? HordeRules.ReclaimGold : 0,
            given?.Worker.Name, given?.Worker.Trade.ToString(), given?.SiteId);

        _logger.LogInformation("Horde site {Site} freed by {User} (run {RunId}): {Freed}/{Total}, reclaimed={Reclaimed}, {Xp} XP, {Gold} gold.",
            target, userId, run.Id, win.Freed, win.Total, win.Reclaimed, rewards.Experience, gold);

        return (new PveOutcome(PveError.None), new PveClaimResponse(
            run.LocationId, nameof(ConquestOutcome.RemoveLocation), rewards.Experience, rewards.Items, 0, materials,
            gold, goldBalance, WorldRewards: true, LockedUntil: null, WorldRewardsBackAt: null, Horde: win), world);
    }

    /// <summary>
    /// A wild warlord fell (Reclaiming phase 2): the region is cleansed for everyone, and nobody gains land by it. The
    /// killer is paid the fight; everyone with a stake (row P2-4: 2+ of its warbands broken, the killer too if so) gets
    /// the cleansing chest. Their season points and letters are paid by the caller, after the world is written.
    /// </summary>
    private async Task<(PveOutcome Outcome, PveClaimResponse? Response, JsonNode? World)> CleanseAsync(
        Guid gameInstanceId, string userId, PveRun run, PveClaimRequest request, SiteResolution resolved, RegionLayout layout,
        HordeEncounterDto encounter, JsonNode world)
    {
        var rewards = RunRewardCalculator.Calculate(encounter.Level, encounter.Tier, _content.RunTuning,
            _content.DroppableItems, Random.Shared, site: HordeRules.SiteType);
        var materials = MaterialRewardCalculator.Calculate(encounter.Level, encounter.Tier, _content.RunTuning,
            _content.Materials, Random.Shared, site: HordeRules.SiteType);

        // The chest is the warlord's full strength (row P2-7), whatever it eased to for whoever killed it.
        int chestLevel = Math.Max(1, resolved.Region.Horde!.BaseLevel) + HordeRules.LairLevelsAbove;
        var rarity = HordeRules.CleansingChest(encounter.Tier);
        var sharers = HordeRules.Stakeholders(resolved.Region);
        var mine = new List<StateManagement.Models.ItemSaveData>();
        foreach (var sharer in sharers)
        {
            var chest = QuestRules.RollChest(_content.DroppableItems.ToList(), Random.Shared, chestLevel, rarity);
            await _items.GrantAsync(gameInstanceId, sharer, chest, $"horde-cleanse run={run.Id} region={resolved.Region.RegionId}");
            if (sharer == userId) mine = chest;
        }
        bool shared = sharers.Contains(userId);

        await _wallet.GrantAsync(gameInstanceId, userId, materials, $"horde-claim run={run.Id}");
        await _items.GrantAsync(gameInstanceId, userId, rewards.Items, $"horde-claim run={run.Id}");
        long goldBalance = await _gold.GrantAsync(gameInstanceId, userId, rewards.Gold, $"horde-claim run={run.Id}");
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
        var win = new HordeWinDto(HordeRules.TargetOf(run.LocationId)!, resolved.Region.RegionId, Reclaimed: false,
            HordeRules.FreedCount(after, layout), HordeRules.Total(after, layout), LairOpen: false,
            shared ? rarity : null, mine, 0, Cleansed: true, Shared: shared) { Sharers = sharers };

        _logger.LogInformation("Wild region {Region} cleansed by {User} (run {RunId}): {Sharers} sharer(s), {Rarity} chests, {Xp} XP.",
            resolved.Region.RegionId, userId, run.Id, sharers.Count, rarity, rewards.Experience);

        return (new PveOutcome(PveError.None), new PveClaimResponse(
            run.LocationId, nameof(ConquestOutcome.RemoveLocation), rewards.Experience, rewards.Items, 0, materials,
            rewards.Gold, goldBalance, WorldRewards: true, LockedUntil: null, WorldRewardsBackAt: null, Horde: win), world);
    }

    // -----------------------------------------------------------------
    // Packs on the roads (phase 1b): fought where they are met, on a walk
    // -----------------------------------------------------------------

    /// <summary>
    /// Opens a fight with a pack (<c>hordepack:{packId}</c>) as a walking company meets it. The region must be overrun and
    /// the player's, the pack standing, and a company of theirs at rest in that region. Where on the road the pack was
    /// met is the client's to say; the server holds the roster and refuses a pack already broken.
    /// </summary>
    public async Task<(PveOutcome Outcome, Guid RunId, HordeEncounterDto? Encounter)> BeginPackAsync(
        Guid gameInstanceId, string userId, PveBeginRequest request)
    {
        if (!ContractMatches(request.SharedContractVersion, out var mismatch))
            return (mismatch, Guid.Empty, null);

        var worldRow = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (worldRow == null)
            return (new PveOutcome(PveError.WorldNotFound, "World view data not found."), Guid.Empty, null);
        var world = JsonNode.Parse(worldRow.GameData);

        string? packId = HordeRules.PackOf(request.SiteId);
        var regionNode = WorldRegionBlob.FindRegion(world, SiteSpec.RegionIdOf(packId));
        if (packId == null || regionNode == null)
            return (new PveOutcome(PveError.LocationNotFound, "There is no such warband."), Guid.Empty, null);
        var region = WorldRegionBlob.ReadRegion(regionNode);
        string? why = HordeRules.WhyNotBreak(region, packId, userId);
        if (why != null)
            return (new PveOutcome(PveError.NotPveTarget, why), Guid.Empty, null);

        if (request.PartyId == null)
            return (new PveOutcome(PveError.NoCompanyThere, "Walk there with a company first."), Guid.Empty, null);
        var company = await PartyService.CompanyInRegionAsync(_context, gameInstanceId, userId, request.PartyId.Value, region.RegionId);
        if (company == null)
            return (new PveOutcome(PveError.NoCompanyThere, "That company is not in this land."), Guid.Empty, null);

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

        var pack = HordeRules.Packs(region).First(p => p.Id == packId);
        _logger.LogInformation("Pack run {RunId} opened by {User} against {Pack} (level {Level}, {Size}).", run.Id, userId, packId, pack.Level, pack.Size);
        return (new PveOutcome(PveError.None), run.Id, PackEncounterOf(region, pack));
    }

    private static HordeEncounterDto PackEncounterOf(WorldRegionData region, HordeRules.Pack pack) => new(
        HordeRules.PackSiteId(pack.Id), region.RegionId, HordeRules.SiteType, region.Biome, pack.Level, Math.Max(1, region.Tier),
        IsLair: false, PackId: pack.Id, Size: pack.Size);

    /// <summary>
    /// Pays a broken pack, scaled to its size against a camp's waves, and marks it broken for good (row 7). The world
    /// goes back for the caller to persist and broadcast.
    /// </summary>
    private async Task<(PveOutcome Outcome, PveClaimResponse? Response, JsonNode? World)> ClaimPackAsync(
        Guid gameInstanceId, string userId, PveRun run, PveClaimRequest request)
    {
        run.ClaimedAt = DateTime.UtcNow;

        var worldRow = await _context.WorldViewGameData.FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        string? packId = HordeRules.PackOf(run.LocationId);
        var world = worldRow == null ? null : JsonNode.Parse(worldRow.GameData);
        var regionNode = world == null ? null : WorldRegionBlob.FindRegion(world, SiteSpec.RegionIdOf(packId));
        if (regionNode == null || packId == null)
        {
            await _context.SaveChangesAsync();
            return (new PveOutcome(PveError.LocationNotFound, "That warband is gone."), null, null);
        }
        var region = WorldRegionBlob.ReadRegion(regionNode);
        string? why = HordeRules.WhyNotBreak(region, packId, userId);
        if (why != null)
        {
            await _context.SaveChangesAsync();
            return (new PveOutcome(PveError.NotPveTarget, why), null, null);
        }

        var pack = HordeRules.Packs(region).First(p => p.Id == packId);
        WorldRegionBlob.BreakHordePack(regionNode, packId, userId);

        // A pack is a skirmish: a camp's pay at its level, scaled by its size against a camp's planned fight.
        var rewards = RunRewardCalculator.Calculate(pack.Level, Math.Max(1, region.Tier), _content.RunTuning,
            _content.DroppableItems, Random.Shared, site: HordeRules.SiteType);
        var materials = MaterialRewardCalculator.Calculate(pack.Level, Math.Max(1, region.Tier), _content.RunTuning,
            _content.Materials, Random.Shared, site: HordeRules.SiteType);
        double share = HordeRules.PackShareOfACamp(pack.Size);
        rewards.Experience = Math.Max(1, (long)Math.Round(rewards.Experience * share));
        rewards.Gold = GoldRules.GoldForClear(rewards.Experience);
        if (rewards.Items.Count > 1) rewards.Items = rewards.Items.Take(1).ToList();
        foreach (var grant in materials) grant.Quantity = Math.Max(1, (int)Math.Round(grant.Quantity * share));

        await _wallet.GrantAsync(gameInstanceId, userId, materials, $"pack-claim run={run.Id}");
        await _items.GrantAsync(gameInstanceId, userId, rewards.Items, $"pack-claim run={run.Id}");
        long goldBalance = await _gold.GrantAsync(gameInstanceId, userId, rewards.Gold, $"pack-claim run={run.Id}");

        if (_quests != null)
        {
            await _quests.RecordAsync(gameInstanceId, userId, new QuestDeed
            {
                RegionId = region.RegionId,
                Kills = new Dictionary<string, int>(_quests.ClampKills(
                    request.Kills, region.Biome, Math.Max(1, region.Tier), HordeRules.SiteType))
            });
        }
        await _context.SaveChangesAsync();

        var after = WorldRegionBlob.ReadRegion(regionNode);
        var layout = RegionGenerator.Generate(after);
        var win = new HordeWinDto(packId, region.RegionId, false,
            HordeRules.FreedCount(after, layout), HordeRules.Total(after, layout), HordeRules.LairOpen(after, layout),
            null, new List<StateManagement.Models.ItemSaveData>(), 0);

        _logger.LogInformation("Pack {Pack} broken by {User} (run {RunId}): {Freed}/{Total}, {Xp} XP, {Gold} gold.",
            packId, userId, run.Id, win.Freed, win.Total, rewards.Experience, rewards.Gold);

        return (new PveOutcome(PveError.None), new PveClaimResponse(
            run.LocationId, nameof(ConquestOutcome.RemoveLocation), rewards.Experience, rewards.Items, 0, materials,
            rewards.Gold, goldBalance, WorldRewards: true, LockedUntil: null, WorldRewardsBackAt: null, Horde: win), world);
    }
}
