using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>A live Rift contract as the rift book shows it. Times are UTC.</summary>
public record RiftContractView(
    RiftContractKind Kind, RiftContractStatus Status, long Price, string OwnerName, string? HolderName,
    DateTime PostedAt, DateTime? HeldUntil, bool Mine, bool HeldByMe);

public record PostRiftContractRequest(RiftContractKind Kind, long Price);

public enum RiftContractError { None, NoSuchRift, NotYours, CannotPost, AlreadyPosted, BadPrice, NoContract, Held, OwnContract, InsufficientGold }

public record RiftContractOutcome(RiftContractError Error, string? Message = null)
{
    public bool Succeeded => Error == RiftContractError.None;
    public static readonly RiftContractOutcome Ok = new(RiftContractError.None);
}

/// <summary>
/// Rift contracts (Invasions spec R7, phase 5). A Lesser Rift's lord posts one, Sell or Hire; the first player to
/// take it holds the right to close the rift for <see cref="RiftRules.ContractHold"/>. The server holds the gold:
/// a Hire's fee from the moment it is posted, a Sell's price from the moment it is taken. Closing the rift as its
/// holder pays it out (<see cref="SettleContractAsync"/>); every other ending hands it back.
/// </summary>
public partial class RiftService
{
    /// <summary>The rift's live contract (posted or taken), or null.</summary>
    public static Task<RiftContract?> LiveContractAsync(ApplicationDbContext context, Guid riftId) =>
        context.RiftContracts.AsNoTracking()
            .Where(c => c.RiftId == riftId && (c.Status == RiftContractStatus.Posted || c.Status == RiftContractStatus.Taken))
            .OrderByDescending(c => c.PostedAt)
            .FirstOrDefaultAsync();

    /// <summary>The lord posts a contract on their Lesser Rift. A Hire's fee leaves their purse now.</summary>
    public async Task<RiftContractOutcome> PostContractAsync(Guid gameInstanceId, string userId, Guid riftId,
        RiftContractKind kind, long price)
    {
        await AdvanceAsync(gameInstanceId);   // the rift's lord follows its land first
        var rift = await _context.Rifts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == riftId && r.GameInstanceId == gameInstanceId);
        if (rift == null) return new(RiftContractError.NoSuchRift, "No such rift.");
        if (!RiftRules.CanPostContract(rift.Kind, rift.Stage))
            return new(RiftContractError.CannotPost, rift.Kind == RiftKind.Great
                ? "A Great Rift is everyone's to close: it takes no contract."
                : "Only an open or invading rift can be contracted out.");
        if (!string.Equals(rift.TargetUserId, userId, StringComparison.Ordinal))
            return new(RiftContractError.NotYours, "Only the lord of this land can contract its rift out.");
        if (!RiftRules.IsValidPrice(price))
            return new(RiftContractError.BadPrice,
                $"Name between {RiftRules.ContractMinPrice:N0} and {RiftRules.ContractMaxPrice:N0} gold.");
        if (await LiveContractAsync(_context, riftId) != null)
            return new(RiftContractError.AlreadyPosted, "This rift is already contracted out. Withdraw that one first.");

        if (kind == RiftContractKind.Hire)
        {
            if (_gold == null) return new(RiftContractError.InsufficientGold, "The purse could not be reached.");
            var spent = await _gold.SpendAsync(gameInstanceId, userId, price, $"rift-contract-hire rift={riftId}");
            if (!spent.Succeeded) return new(RiftContractError.InsufficientGold, spent.Message);
        }

        var now = Clock();
        var contract = new RiftContract
        {
            GameInstanceId = gameInstanceId,
            RiftId = riftId,
            RegionId = rift.RegionId,
            Kind = kind,
            Status = RiftContractStatus.Posted,
            OwnerUserId = userId,
            Price = price,
            PostedAt = now,
            Escrow = kind == RiftContractKind.Hire ? price : 0,
        };
        _context.RiftContracts.Add(contract);
        await _context.SaveChangesAsync();

        _sessionLog.Log("RIFT-CONTRACT", $"realm={gameInstanceId} rift={riftId} posted {kind} price={price} by={userId}");
        if (_letters != null)
        {
            string owner = await NameOfAsync(userId);
            foreach (var member in await MembersAsync(gameInstanceId))
                if (!string.Equals(member, userId, StringComparison.Ordinal))
                    await _letters.SendAsync(gameInstanceId, member, LetterKind.RiftContractPosted, now,
                        $"rift-contract:{contract.Id}:posted", rift.RegionId, rift.Id.ToString(), owner, $"{kind}:{price}");
        }
        await BroadcastAsync(rift);
        return RiftContractOutcome.Ok;
    }

    /// <summary>The lord takes down a contract nobody has taken. A Hire's fee comes back.</summary>
    public async Task<RiftContractOutcome> WithdrawContractAsync(Guid gameInstanceId, string userId, Guid riftId)
    {
        var refund = 0L;
        var outcome = await Concurrency.RetryAsync(_context, async () =>
        {
            await Concurrency.RefreshAsync<RiftContract>(_context, c => c.RiftId == riftId);
            var contract = await TrackedLiveAsync(gameInstanceId, riftId);
            if (contract == null) return new RiftContractOutcome(RiftContractError.NoContract, "This rift has no contract.");
            if (!string.Equals(contract.OwnerUserId, userId, StringComparison.Ordinal))
                return new RiftContractOutcome(RiftContractError.NotYours, "Only the lord who posted it can withdraw it.");
            if (contract.Status == RiftContractStatus.Taken)
                return new RiftContractOutcome(RiftContractError.Held,
                    "Someone has taken it. It comes back to you if they do not close the rift in time.");

            refund = contract.Escrow;
            contract.Escrow = 0;
            contract.Status = RiftContractStatus.Withdrawn;
            contract.EndedAt = Clock();
            await _context.SaveChangesAsync();
            return RiftContractOutcome.Ok;
        });
        if (!outcome.Succeeded) return outcome;

        if (refund > 0 && _gold != null)
            await _gold.CreditAsync(gameInstanceId, userId, refund, $"rift-contract-withdrawn rift={riftId}");
        _sessionLog.Log("RIFT-CONTRACT", $"realm={gameInstanceId} rift={riftId} withdrawn by={userId} refund={refund}");
        await BroadcastContractAsync(gameInstanceId, riftId);
        return outcome;
    }

    /// <summary>
    /// A player takes a posted contract, and holds the right to close the rift for <see cref="RiftRules.ContractHold"/>.
    /// A Sell's price leaves their purse now. The first to take it has it: the row's revision decides a race.
    /// </summary>
    public async Task<RiftContractOutcome> TakeContractAsync(Guid gameInstanceId, string userId, Guid riftId)
    {
        await AdvanceAsync(gameInstanceId);
        var rift = await _context.Rifts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == riftId && r.GameInstanceId == gameInstanceId);
        if (rift == null) return new(RiftContractError.NoSuchRift, "No such rift.");
        if (!RiftRules.CanSeal(rift.Stage)) return new(RiftContractError.CannotPost, "That rift is closed.");

        var now = Clock();
        RiftContract? taken = null;
        var outcome = await Concurrency.RetryAsync(_context, async () =>
        {
            await Concurrency.RefreshAsync<RiftContract>(_context, c => c.RiftId == riftId);
            var contract = await TrackedLiveAsync(gameInstanceId, riftId);
            if (contract == null) return new RiftContractOutcome(RiftContractError.NoContract, "This rift has no contract.");
            if (string.Equals(contract.OwnerUserId, userId, StringComparison.Ordinal))
                return new RiftContractOutcome(RiftContractError.OwnContract, "This is your own contract.");
            if (contract.Status == RiftContractStatus.Taken)
                return new RiftContractOutcome(RiftContractError.Held, "Someone else took it first.");

            contract.Status = RiftContractStatus.Taken;
            contract.HolderUserId = userId;
            contract.TakenAt = now;
            contract.HeldUntil = now + RiftRules.ContractHold;
            await _context.SaveChangesAsync();
            taken = contract;
            return RiftContractOutcome.Ok;
        });
        if (!outcome.Succeeded) return outcome;

        if (taken!.Kind == RiftContractKind.Sell)
        {
            var spent = _gold == null
                ? new GoldOutcome(GoldError.InsufficientGold, "The purse could not be reached.")
                : await _gold.SpendAsync(gameInstanceId, userId, taken.Price, $"rift-contract-sell rift={riftId}");
            if (!spent.Succeeded)
            {
                // Could not pay: it goes back up as it was.
                await Concurrency.RetryAsync(_context, async () =>
                {
                    await _context.Entry(taken).ReloadAsync();
                    if (taken.Status != RiftContractStatus.Taken || taken.HolderUserId != userId) return;
                    taken.Status = RiftContractStatus.Posted;
                    taken.HolderUserId = null;
                    taken.TakenAt = null;
                    taken.HeldUntil = null;
                    await _context.SaveChangesAsync();
                });
                return new(RiftContractError.InsufficientGold, spent.Message);
            }
            await Concurrency.RetryAsync(_context, async () =>
            {
                await _context.Entry(taken).ReloadAsync();
                taken.Escrow += taken.Price;
                await _context.SaveChangesAsync();
            });
        }

        _sessionLog.Log("RIFT-CONTRACT", $"realm={gameInstanceId} rift={riftId} {taken.Kind} taken by={userId} until={taken.HeldUntil:O}");
        if (_letters != null)
            await _letters.SendAsync(gameInstanceId, taken.OwnerUserId, LetterKind.RiftContractTaken, now,
                $"rift-contract:{taken.Id}:taken:{now.Ticks}", rift.RegionId, rift.Id.ToString(), await NameOfAsync(userId),
                $"{taken.Kind}:{taken.Price}");
        await BroadcastAsync(rift);
        return outcome;
    }

    /// <summary>
    /// A rift was sealed by <paramref name="sealerUserId"/>: its holder's contract pays out; any other live contract
    /// ends, and its gold goes back.
    /// </summary>
    private async Task SettleContractAsync(Rift rift, string sealerUserId)
    {
        var live = await TrackedLiveAsync(rift.GameInstanceId, rift.Id);
        if (live == null) return;

        bool fulfilled = live.Status == RiftContractStatus.Taken &&
                         string.Equals(live.HolderUserId, sealerUserId, StringComparison.Ordinal);
        await EndContractAsync(live, fulfilled ? RiftContractStatus.Fulfilled : RiftContractStatus.Void, "sealed");
    }

    /// <summary>
    /// Ends a live contract: <see cref="RiftContractStatus.Fulfilled"/> pays its gold out (a Sell to the lord, a Hire
    /// to the closer); any other ending hands it back (a Sell's to its taker, a Hire's to the lord).
    /// </summary>
    private async Task EndContractAsync(RiftContract contract, RiftContractStatus ending, string why)
    {
        long gold = 0;
        string? to = null;
        bool ended = await Concurrency.RetryAsync(_context, async () =>
        {
            if (!RiftRules.IsLive(contract.Status)) return false;
            gold = contract.Escrow;
            to = ending == RiftContractStatus.Fulfilled
                ? RiftRules.PaysTo(contract.Kind, contract.OwnerUserId, contract.HolderUserId!)
                : RiftRules.RefundTo(contract.Kind, contract.OwnerUserId, contract.HolderUserId!);
            contract.Escrow = 0;
            contract.Status = ending;
            contract.EndedAt = Clock();
            await _context.SaveChangesAsync();
            return true;
        });
        if (!ended) return;

        if (gold > 0 && !string.IsNullOrEmpty(to) && _gold != null)
            await _gold.CreditAsync(contract.GameInstanceId, to, gold, $"rift-contract-{ending.ToString().ToLowerInvariant()} rift={contract.RiftId}");
        _sessionLog.Log("RIFT-CONTRACT", $"realm={contract.GameInstanceId} rift={contract.RiftId} {contract.Kind} {ending} ({why}) gold={gold} to={to}");

        if (_letters != null && gold > 0 && !string.IsNullOrEmpty(to))
        {
            var kind = ending == RiftContractStatus.Fulfilled ? LetterKind.RiftContractPaid : LetterKind.RiftContractEnded;
            string detail = ending == RiftContractStatus.Fulfilled ? $"{contract.Kind}:{gold}" : $"{contract.Kind}:{gold}:{why}";
            await _letters.SendAsync(contract.GameInstanceId, to, kind, Clock(),
                $"rift-contract:{contract.Id}:{ending}", contract.RegionId, contract.RiftId.ToString(), null, detail);
        }
    }

    /// <summary>
    /// Holds that ran out (R7: "if they run out of time, the contract goes back up"), unless the holder is in a fight
    /// at the rift begun during the hold, which keeps it until <see cref="RiftRules.ContractFightGrace"/> has passed.
    /// A Sell's price goes back to its taker.
    /// </summary>
    private async Task<int> LapseHoldsAsync(Guid gameInstanceId, DateTime now)
    {
        var lapsed = await _context.RiftContracts
            .Where(c => c.GameInstanceId == gameInstanceId && c.Status == RiftContractStatus.Taken && c.HeldUntil <= now)
            .ToListAsync();
        int changed = 0;
        foreach (var contract in lapsed)
        {
            string prefix = RiftRules.SitePrefix + contract.RiftId + ":";
            var fightSince = now - RiftRules.ContractFightGrace;
            bool fighting = await _context.PveRuns.AnyAsync(r => r.GameInstanceId == gameInstanceId && r.UserId == contract.HolderUserId &&
                                                                 r.ClaimedAt == null && r.LocationId.StartsWith(prefix) &&
                                                                 r.StartedAt >= contract.TakenAt && r.StartedAt >= fightSince);
            if (fighting) continue;

            string holder = contract.HolderUserId!;
            long refund = contract.Kind == RiftContractKind.Sell ? contract.Escrow : 0;
            contract.Escrow -= refund;
            contract.Status = RiftContractStatus.Posted;
            contract.HolderUserId = null;
            contract.TakenAt = null;
            contract.HeldUntil = null;
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                await _context.Entry(contract).ReloadAsync();
                continue;
            }
            changed++;

            if (refund > 0 && _gold != null)
                await _gold.CreditAsync(gameInstanceId, holder, refund, $"rift-contract-lapsed rift={contract.RiftId}");
            _sessionLog.Log("RIFT-CONTRACT", $"realm={gameInstanceId} rift={contract.RiftId} hold by={holder} lapsed refund={refund}");
            if (_letters != null)
                await _letters.SendAsync(gameInstanceId, holder, LetterKind.RiftContractEnded, now,
                    $"rift-contract:{contract.Id}:lapsed:{now.Ticks}", contract.RegionId, contract.RiftId.ToString(), null,
                    $"{contract.Kind}:{refund}:lapsed");
            await BroadcastContractAsync(gameInstanceId, contract.RiftId);
        }
        return changed;
    }

    /// <summary>The rift has a new lord (it followed the land): its contract ends, and the gold goes back.</summary>
    private async Task VoidForNewLordAsync(Rift rift)
    {
        var live = await TrackedLiveAsync(rift.GameInstanceId, rift.Id);
        if (live != null) await EndContractAsync(live, RiftContractStatus.Void, "lord");
    }

    /// <summary>
    /// A season's end: every live contract's gold goes back, and the realm's contracts go with its rifts. Called after
    /// the rifts are deleted, as the Bazaar's goods are expired.
    /// </summary>
    public static async Task ExpireContractsAsync(ApplicationDbContext context, GoldService gold, ISessionLog sessionLog, Guid gameInstanceId)
    {
        var all = await context.RiftContracts.Where(c => c.GameInstanceId == gameInstanceId).ToListAsync();
        var refunds = all
            .Where(c => RiftRules.IsLive(c.Status) && c.Escrow > 0)
            .Select(c => (To: RiftRules.RefundTo(c.Kind, c.OwnerUserId, c.HolderUserId!), c.Escrow, c.RiftId))
            .Where(r => !string.IsNullOrEmpty(r.To))
            .ToList();
        context.RiftContracts.RemoveRange(all);
        await context.SaveChangesAsync();

        foreach (var (to, escrow, riftId) in refunds)
        {
            await gold.CreditAsync(gameInstanceId, to, escrow, $"rift-contract-season-end rift={riftId}");
            sessionLog.Log("RIFT-CONTRACT", $"realm={gameInstanceId} rift={riftId} season ended refund={escrow} to={to}");
        }
    }

    // -----------------------------------------------------------------

    private Task<RiftContract?> TrackedLiveAsync(Guid gameInstanceId, Guid riftId) =>
        _context.RiftContracts
            .Where(c => c.GameInstanceId == gameInstanceId && c.RiftId == riftId &&
                        (c.Status == RiftContractStatus.Posted || c.Status == RiftContractStatus.Taken))
            .OrderByDescending(c => c.PostedAt)
            .FirstOrDefaultAsync();

    private async Task BroadcastContractAsync(Guid gameInstanceId, Guid riftId)
    {
        var rift = await _context.Rifts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == riftId && r.GameInstanceId == gameInstanceId);
        if (rift != null) await BroadcastAsync(rift);
    }

    /// <summary>Everyone in the realm: its members and its owner.</summary>
    private async Task<List<string>> MembersAsync(Guid gameInstanceId)
    {
        var members = await _context.PlayerGameData.AsNoTracking()
            .Where(p => p.GameInstanceId == gameInstanceId).Select(p => p.UserId).ToListAsync();
        var owner = await _context.GameInstances.AsNoTracking()
            .Where(g => g.Id == gameInstanceId).Select(g => g.OwnerId).FirstOrDefaultAsync();
        if (!string.IsNullOrEmpty(owner)) members.Add(owner);
        return members.Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task<string> NameOfAsync(string userId) =>
        await _context.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => u.DisplayName ?? u.UserName).FirstOrDefaultAsync() ?? "A herald";
}
