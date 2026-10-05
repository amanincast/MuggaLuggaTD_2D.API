using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum PveError
{
    None = 0,
    WorldNotFound,
    LocationNotFound,
    NotPveTarget,
    RunNotFound,
    RunAlreadyClaimed,
    RunTooFast,
    ContractMismatch,
    NoConquestEffect,
    FightersUnavailable,
    NoCompanyThere,
    SiteLocked
}

public record PveOutcome(PveError Error, string? Message = null)
{
    public bool Succeeded => Error == PveError.None;
}

/// <summary>
/// Server-side PvE conquest.
///
/// Unlike PvP, the server cannot recompute the result: PvE is real-time bullet-hell combat that the
/// server does not simulate, so it has no way to know whether the player actually won. What it can
/// own — and now does — is everything around that:
///
///   - eligibility: the location exists, is a legitimate PvE target, and is not another player's
///   - proof of attempt: a claim must reference a run this player opened against this location
///   - single use: a run can be claimed once, and the claim re-validates against the live world
///   - the state transition: the server applies the capture or removal and writes the world itself
///
/// Previously the client applied the conquest locally and pushed the whole world blob, so a
/// conquest could be fabricated outright. It no longer can. What remains possible is cheating
/// *within* the combat scene to produce a genuine-looking win; that is inherent to client-side
/// real-time combat and is not addressed here.
/// </summary>
public class WorldPveService
{
    /// <summary>
    /// A run claimed faster than this never happened — the combat scene cannot be completed in less.
    /// Deliberately generous: this is a floor against instant scripted claims, not a balance knob.
    /// </summary>
    public static readonly TimeSpan MinimumRunDuration = TimeSpan.FromSeconds(10);

    /// <summary>Open runs older than this are treated as abandoned.</summary>
    public static readonly TimeSpan RunExpiry = TimeSpan.FromHours(6);

    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly MaterialWalletService _wallet;
    private readonly GoldService _gold;
    private readonly TavernService _tavern;
    private readonly ILogger<WorldPveService> _logger;

    private readonly ItemLedgerService _items;

    /// <summary>Ticks First Steps where they happen; optional so tests can build this without it.</summary>
    private readonly FirstStepsService? _firstSteps;

    /// <summary>A clear resets the refresh price at the Hiring Hall; optional so tests can build this without it.</summary>
    private readonly HiringService? _hiring;

    public WorldPveService(
        ApplicationDbContext context,
        IGameContentProvider content,
        MaterialWalletService wallet,
        GoldService gold,
        TavernService tavern,
        ILogger<WorldPveService> logger,
        ItemLedgerService items,
        FirstStepsService? firstSteps = null,
        HiringService? hiring = null)
    {
        _hiring = hiring;
        _firstSteps = firstSteps;
        _items = items;
        _context = context;
        _content = content;
        _wallet = wallet;
        _gold = gold;
        _tavern = tavern;
        _logger = logger;
    }

    /// <summary>
    /// Opens a run against a PvE location. Called as the player enters combat, so a later claim has
    /// something to prove itself against.
    /// </summary>
    public async Task<(PveOutcome Outcome, Guid RunId)> BeginAsync(
        Guid gameInstanceId, string userId, PveBeginRequest request)
    {
        if (!ContractMatches(request.SharedContractVersion, out var mismatch))
            return (mismatch, Guid.Empty);

        var worldRow = await _context.WorldViewGameData
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);

        if (worldRow == null)
            return (new PveOutcome(PveError.WorldNotFound, "World view data not found."), Guid.Empty);

        var world = JsonNode.Parse(worldRow.GameData);

        // The site is resolved by regenerating its region from the stored seed, so a client naming a
        // site the world does not actually contain is refused here rather than at claim time.
        var resolved = WorldRegionBlob.ResolveSite(world, request.SiteId);
        if (resolved == null)
            return (new PveOutcome(PveError.LocationNotFound, "Site not found in this world."), Guid.Empty);

        var check = ValidatePveTarget(resolved, userId);
        if (!check.Succeeded)
            return (check, Guid.Empty);

        // The rotation (SiteRotationRules): a player who has just cleared this site fights elsewhere
        // for ten minutes. Their own clear only - nobody else is locked out by it.
        var clear = await ClearOfAsync(gameInstanceId, userId, request.SiteId);
        if (SiteRotationRules.IsLocked(clear?.LastClearedAt, DateTime.UtcNow))
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((SiteRotationRules.LockedUntil(clear!.LastClearedAt) - DateTime.UtcNow).TotalMinutes));
            return (new PveOutcome(PveError.SiteLocked,
                $"You cleared this site moments ago. Fight elsewhere; it is open to you again in {minutes} min."), Guid.Empty);
        }

        // You fight where you stand (1.33.0): a company must have walked there, and it is who goes in.
        if (request.PartyId == null)
            return (new PveOutcome(PveError.NoCompanyThere, "Send a company there first."), Guid.Empty);
        var company = await PartyService.CompanyAtAsync(_context, gameInstanceId, userId, request.PartyId.Value, request.SiteId);
        if (company == null)
            return (new PveOutcome(PveError.NoCompanyThere, "That company is not standing there. Send it, and fight when it arrives."), Guid.Empty);

        // Who is going in. A garrisoned, captive or sieging character cannot also be in a dungeon;
        // the server used to take the client's word for the party entirely.
        var fighters = MarchingArmy.ReadIds(company.CharacterIdsJson)
            .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        var why = await PartyService.WhyCannotFightAsync(_context, _logger, world, gameInstanceId, userId, fighters);
        if (why != null)
            return (new PveOutcome(PveError.FightersUnavailable, why), Guid.Empty);

        // One open run per player per site: re-entering replaces the previous attempt rather
        // than accumulating claimable runs.
        var existing = await _context.PveRuns
            .Where(r => r.GameInstanceId == gameInstanceId && r.UserId == userId
                        && r.LocationId == request.SiteId && r.ClaimedAt == null)
            .ToListAsync();
        if (existing.Count > 0)
            _context.PveRuns.RemoveRange(existing);

        var run = new PveRun
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            LocationId = request.SiteId,
            LocationType = (int)resolved.Site.Type,
            FighterIdsJson = MarchingArmy.WriteIds(fighters),
            StartedAt = DateTime.UtcNow
        };

        _context.PveRuns.Add(run);
        await _context.SaveChangesAsync();

        _logger.LogInformation("PvE run {RunId} opened by {User} at {Site}.", run.Id, userId, request.SiteId);
        return (new PveOutcome(PveError.None), run.Id);
    }

    /// <summary>
    /// The player gave the run up. It is closed, so it can never be claimed, and everyone who went in
    /// is Bloodied (BloodiedRules; Mike, 2026-10-04). Giving up an ambush or a siege assault is reported
    /// as a lost claim of its own, which Bloodies them there.
    /// </summary>
    public async Task<(PveOutcome Outcome, PveAbandonResponse? Response)> AbandonAsync(
        Guid gameInstanceId, string userId, PveAbandonRequest request)
    {
        if (!ContractMatches(request.SharedContractVersion, out var mismatch))
            return (mismatch, null);

        var run = await _context.PveRuns.FirstOrDefaultAsync(r =>
            r.Id == request.RunId && r.GameInstanceId == gameInstanceId && r.UserId == userId);
        if (run == null)
            return (new PveOutcome(PveError.RunNotFound, "No such run for this player."), null);
        if (run.ClaimedAt != null)
            return (new PveOutcome(PveError.RunAlreadyClaimed, "That run has already been settled."), null);

        var now = DateTime.UtcNow;
        run.ClaimedAt = now;
        var fighters = MarchingArmy.ReadIds(run.FighterIdsJson);
        await AutoFightService.BloodyAsync(_context, gameInstanceId, userId, fighters, now);
        await _context.SaveChangesAsync();

        var bloodied = await AutoFightService.BloodiedAsync(_context, gameInstanceId, userId, now);
        return (new PveOutcome(PveError.None), new PveAbandonResponse(bloodied
            .Where(b => fighters.Contains(b.Key))
            .Select(b => new BloodiedDto(b.Key, b.Value)).ToList()));
    }

    /// <summary>
    /// Claims the conquest for a completed run. Returns the mutated world for the caller to persist
    /// and broadcast, so persistence stays in one place.
    /// </summary>
    public async Task<(PveOutcome Outcome, PveClaimResponse? Response, JsonNode? UpdatedWorld)> ClaimAsync(
        Guid gameInstanceId, string userId, string? displayName, PveClaimRequest request)
    {
        if (!ContractMatches(request.SharedContractVersion, out var mismatch))
            return (mismatch, null, null);

        var run = await _context.PveRuns.FirstOrDefaultAsync(r =>
            r.Id == request.RunId && r.GameInstanceId == gameInstanceId && r.UserId == userId);

        if (run == null)
            return (new PveOutcome(PveError.RunNotFound, "No such run for this player."), null, null);

        if (run.ClaimedAt != null)
            return (new PveOutcome(PveError.RunAlreadyClaimed, "That run has already been claimed."), null, null);

        var elapsed = DateTime.UtcNow - run.StartedAt;
        if (elapsed < MinimumRunDuration)
            return (new PveOutcome(PveError.RunTooFast, "That run completed implausibly fast."), null, null);

        if (elapsed > RunExpiry)
            return (new PveOutcome(PveError.RunNotFound, "That run has expired."), null, null);

        var worldRow = await _context.WorldViewGameData
            .FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (worldRow == null)
            return (new PveOutcome(PveError.WorldNotFound, "World view data not found."), null, null);

        var world = JsonNode.Parse(worldRow.GameData);
        var resolved = WorldRegionBlob.ResolveSite(world, run.LocationId);
        if (resolved == null)
        {
            // The region is gone, or the world was regenerated under this player's feet. Close the
            // run so it cannot be held open and replayed if that site id ever comes back.
            run.ClaimedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return (new PveOutcome(PveError.LocationNotFound, "That site no longer exists."), null, null);
        }

        // Re-validate against the live world, not against what was true when the run started —
        // another player may have cleared the site or taken the region in the meantime.
        var check = ValidatePveTarget(resolved, userId);
        if (!check.Succeeded)
            return (check, null, null);

        var outcome = ConquestResolver.ResolveOnPlayerVictory(resolved.Site.Type);

        if (outcome == ConquestOutcome.None)
            return (new PveOutcome(PveError.NoConquestEffect, "Winning here has no conquest effect."), null, null);

        switch (outcome)
        {
            case ConquestOutcome.CaptureForPlayer:
                // Taking the keep takes the region: regions are what a player owns.
                WorldRegionBlob.CaptureRegion(resolved.RegionNode, userId, displayName);
                break;
            case ConquestOutcome.RemoveLocation:
                // A cleared dungeon or portal is no longer written into the shared world: the clear
                // is this player's alone (SiteRotationRules), recorded below.
                break;
        }

        // The rotation. A clear locks its clearer out for ten minutes, and shapes the realm (resolve,
        // a recruit, refresh resets, season points) once per player per site every eight hours.
        // Experience, gold, gear and materials are paid on every clear.
        bool worldRewards = true;
        DateTime? lockedUntil = null, worldRewardsBackAt = null;
        if (outcome == ConquestOutcome.RemoveLocation)
        {
            var now = DateTime.UtcNow;
            var clear = await ClearOfAsync(gameInstanceId, userId, run.LocationId);
            if (clear == null)
            {
                clear = new PlayerSiteClear { GameInstanceId = gameInstanceId, UserId = userId, SiteId = run.LocationId };
                _context.PlayerSiteClears.Add(clear);
            }
            worldRewards = SiteRotationRules.WorldRewardsDue(clear.WorldRewardsAt, now);
            clear.LastClearedAt = now;
            if (worldRewards) clear.WorldRewardsAt = now;
            lockedUntil = SiteRotationRules.LockedUntil(clear.LastClearedAt);
            worldRewardsBackAt = SiteRotationRules.WorldRewardsBackAt(clear.WorldRewardsAt);
        }

        // Clearing a hostile site inside your own region steadies it. This is the defender's half of
        // raiding: a rival wears a region's resolve down from outside, and the owner answers by going
        // into the region and dealing with what is under it. Without this, being raided has no reply
        // — which is why the own-region PvE fix had to land before raiding could.
        int resolveRestored = 0;
        if (worldRewards && resolved.Region.IsOwnedByPlayer(userId))
        {
            resolveRestored = RegionResolveRules.RestoredByClearing(resolved.Site.Type);
            if (resolveRestored > 0)
            {
                int before = resolved.Region.Resolve;
                int after = WorldRegionBlob.SetResolve(
                    resolved.RegionNode, RegionResolveRules.Apply(before, resolveRestored));

                // Report what was actually restored, which is less than the roll near full morale.
                resolveRestored = after - before;
            }
        }

        run.ClaimedAt = DateTime.UtcNow;

        // Rewards are rolled here, from the site, rather than accepted from the client. A
        // self-reported total is unbounded, and fabricated XP/gear inflates the same persisted roster
        // that PvP power is computed from.
        var rewards = RunRewardCalculator.Calculate(
            resolved.Site.Level,
            resolved.Site.Tier,
            _content.RunTuning,
            _content.DroppableItems,
            Random.Shared,
            site: resolved.Site.Type);

        // Materials are the Tavern's currency, so they are granted here and held server-side rather
        // than written by the client into its own save.
        var materials = MaterialRewardCalculator.Calculate(
            resolved.Site.Level,
            resolved.Site.Tier,
            _content.RunTuning,
            _content.Materials,
            Random.Shared,
            site: resolved.Site.Type);

        await _wallet.GrantAsync(gameInstanceId, userId, materials, $"pve-claim run={run.Id}");

        // Every item rolled is recorded, so the save that carries it home is believed - and a save
        // carrying anything else is not (ItemLedgerService).
        await _items.GrantAsync(gameInstanceId, userId, rewards.Items, $"pve-claim run={run.Id}");

        // Gold is paid as one figure at the end rather than dropped during the fight, so the client
        // never holds a coin it could have minted. GrantAsync settles the purse first, which means
        // the balance it returns already includes whatever the players land earned mid-run.
        long goldBalance = await _gold.GrantAsync(
            gameInstanceId, userId, rewards.Gold, $"pve-claim run={run.Id}");

        // Clearing a dungeon brings somebody new to the Tavern - one recruit, into a free seat. It
        // does NOT replace the board: farming the materials to afford a recruit used to be the very
        // thing that took that recruit away, which made saving up self-defeating. The tier still
        // raises the rarity odds and the region's biome still nudges the affinity, so where you
        // cleared shows up in who walks in. Design doc 05 §4.
        if (TavernRules.BringsARecruit(resolved.Site.Type))
        {
            // A recruit and the refresh resets are realm rewards: once per site every eight hours,
            // or a ten-minute loop would fill the Tavern and keep both refreshes at their base price.
            if (worldRewards)
            {
                await _tavern.BringARecruitAsync(
                    gameInstanceId, userId, resolved.Site.Tier, resolved.Region.Biome,
                    $"pve-claim run={run.Id} site={run.LocationId}");
                if (_hiring != null) await _hiring.ResetRefreshAsync(gameInstanceId, userId);
            }

            // The same sites count for First Steps' "clear a dungeon" (a dungeon or a portal).
            if (_firstSteps != null) await _firstSteps.RecordAsync(gameInstanceId, userId, FirstStepsRules.Clear);
        }

        _logger.LogInformation(
            "PvE conquest {Outcome} at {Site} by {User} (run {RunId}, {Seconds:F0}s) — {Xp} XP, {Items} item(s), {Materials} material(s).",
            outcome, run.LocationId, userId, run.Id, elapsed.TotalSeconds, rewards.Experience, rewards.Items.Count,
            materials.Sum(m => m.Quantity));

        var response = new PveClaimResponse(
            run.LocationId, outcome.ToString(), rewards.Experience, rewards.Items, resolveRestored, materials,
            rewards.Gold, goldBalance, worldRewards, lockedUntil, worldRewardsBackAt);

        return (new PveOutcome(PveError.None), response, world);
    }

    /// <summary>
    /// A site is a legitimate PvE target when the player is not being handed a rival's territory,
    /// and the site itself has combat to offer. (Whether this player has just cleared it is the
    /// lockout's question, asked in <see cref="BeginAsync"/>.)
    ///
    /// <para>A <b>rival's</b> region is a PvP target and must go through the PvP endpoint, which
    /// resolves a contested fight rather than handing over a capture on the attacker's say-so.</para>
    ///
    /// <para>Your <b>own</b> region is not. Holding a region does not clear the dungeons inside it,
    /// and clearing them is a loop the design leans on — it is how resolve is restored. Rejecting
    /// every site in an owned region shut that off entirely: a player who took a region could never
    /// fight in it again.</para>
    /// </summary>
    private static PveOutcome ValidatePveTarget(SiteResolution resolved, string userId)
    {
        var region = resolved.Region;

        if (region.Ownership == LocationOwnership.Player && !string.IsNullOrEmpty(region.OwnerUserId))
        {
            if (!string.Equals(region.OwnerUserId, userId, StringComparison.Ordinal))
                return new PveOutcome(PveError.NotPveTarget, "That region belongs to another player — lay siege to it instead.");

            // In your own region only the hostile sites are still a fight. The keep and the
            // settlements are yours along with the ground they stand on; the dungeons and portals
            // are not, and never become so — holding the region does not empty them.
            if (!resolved.Site.IsFightable)
                return new PveOutcome(PveError.NotPveTarget, "That site is already yours.");
        }

        if (ConquestResolver.ResolveOnPlayerVictory(resolved.Site.Type) == ConquestOutcome.None)
            return new PveOutcome(PveError.NotPveTarget, "That site has no combat to complete.");

        // A site is never spent for the realm any more: whether this player may fight it again is
        // their own clear's lockout (SiteRotationRules), checked in BeginAsync.
        return new PveOutcome(PveError.None);
    }

    /// <summary>This player's last clear of a site, or null if they have never cleared it.</summary>
    private Task<PlayerSiteClear?> ClearOfAsync(Guid gameInstanceId, string userId, string siteId) =>
        _context.PlayerSiteClears.FirstOrDefaultAsync(c =>
            c.GameInstanceId == gameInstanceId && c.UserId == userId && c.SiteId == siteId);

    /// <summary>
    /// Every site this player has cleared that is still locked to them or not yet shaping the realm
    /// again, for the region view to draw.
    /// </summary>
    public async Task<SiteClearsResponse> ClearsAsync(Guid gameInstanceId, string userId)
    {
        var now = DateTime.UtcNow;
        var since = now - SiteRotationRules.WorldRewardWindow - SiteRotationRules.Lockout;
        var rows = await _context.PlayerSiteClears
            .Where(c => c.GameInstanceId == gameInstanceId && c.UserId == userId
                        && (c.LastClearedAt > since || (c.WorldRewardsAt != null && c.WorldRewardsAt > since)))
            .ToListAsync();

        var clears = rows
            .Select(c => new SiteClearDto(c.SiteId,
                SiteRotationRules.LockedUntil(c.LastClearedAt),
                SiteRotationRules.WorldRewardsBackAt(c.WorldRewardsAt)))
            .Where(c => c.LockedUntil > now || c.WorldRewardsBackAt > now)
            .OrderBy(c => c.SiteId, StringComparer.Ordinal)
            .ToList();
        return new SiteClearsResponse(clears, now);
    }

    private static bool ContractMatches(string clientVersion, out PveOutcome mismatch)
    {
        if (string.Equals(clientVersion, MuggaLuggaTD.Shared.SharedContract.Version, StringComparison.Ordinal))
        {
            mismatch = new PveOutcome(PveError.None);
            return true;
        }

        mismatch = new PveOutcome(PveError.ContractMismatch,
            $"Client gameplay rules v{clientVersion} do not match the server's " +
            $"v{MuggaLuggaTD.Shared.SharedContract.Version}. Update the game to continue.");
        return false;
    }
}
