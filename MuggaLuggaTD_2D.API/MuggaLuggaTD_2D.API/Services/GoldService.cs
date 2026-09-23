using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using System.Text.Json.Nodes;

namespace MuggaLuggaTD_2D.API.Services;

public enum GoldError
{
    None,
    NothingToSpend,
    InsufficientGold
}

public record GoldOutcome(GoldError Error, string? Message = null, long Balance = 0)
{
    public bool Succeeded => Error == GoldError.None;
}

/// <summary>
/// The player's purse: paid by claimed runs and by ground held, spent through endpoints, never
/// written by the client.
///
/// <para><b>The accrual is the whole design.</b> Gold from land is stored as settled + rate + when,
/// so holding a region for a week and holding it for a second cost the server exactly the same
/// arithmetic and no background work. <see cref="SettleAllAsync"/> is called from inside
/// <c>SeasonScoreService.SettleAllAsync</c> rather than from each place a holding can change —
/// there are five such places, they already settle scores, and a sixth that forgot to settle gold
/// would be an accrual bug nobody would notice for a season.</para>
///
/// <para>Spending is permissive about <i>why</i>, like the material wallet: nothing is minted by
/// spending, so the worst a forged call can do is burn the caller's own gold. Granting is the
/// guarded side, and only server-resolved actions reach it.</para>
/// </summary>
public class GoldService
{
    private readonly ApplicationDbContext _context;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<GoldService> _logger;

    public GoldService(ApplicationDbContext context, ISessionLog sessionLog, ILogger<GoldService> logger)
    {
        _context = context;
        _sessionLog = sessionLog;
        _logger = logger;
    }

    /// <summary>
    /// What the player has right now, projected forward from their settled purse without writing
    /// anything. Reading a balance must never be what makes it correct.
    /// </summary>
    public async Task<long> BalanceAsync(Guid gameInstanceId, string userId, DateTime? at = null)
    {
        var row = await _context.PlayerGold
            .FirstOrDefaultAsync(g => g.GameInstanceId == gameInstanceId && g.UserId == userId);

        return row == null ? 0 : ProjectedBalance(row, at ?? DateTime.UtcNow);
    }

    /// <summary>The purse rows for a realm, for callers that already hold the world.</summary>
    public async Task<PlayerGold?> ReadAsync(Guid gameInstanceId, string userId)
        => await _context.PlayerGold
            .FirstOrDefaultAsync(g => g.GameInstanceId == gameInstanceId && g.UserId == userId);

    /// <summary>
    /// Brings every player in the realm up to date and re-rates them against the world as it stands.
    ///
    /// <para>Takes the world and the instant from the caller so that gold and season points settle
    /// against <i>the same</i> world at <i>the same</i> moment. Re-reading either here would let the
    /// two disagree about exactly when a region changed hands.</para>
    /// </summary>
    public async Task SettleAllAsync(
        Guid gameInstanceId, IReadOnlyList<WorldRegionData> regions, IEnumerable<string> userIds, DateTime until)
    {
        var members = userIds?.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
        if (members == null || members.Count == 0) return;

        var rows = await _context.PlayerGold
            .Where(g => g.GameInstanceId == gameInstanceId && members.Contains(g.UserId))
            .ToListAsync();

        foreach (var userId in members)
        {
            var row = rows.FirstOrDefault(g => g.UserId == userId);
            if (row == null)
            {
                row = new PlayerGold
                {
                    GameInstanceId = gameInstanceId,
                    UserId = userId,
                    LastSettledAt = until
                };
                _context.PlayerGold.Add(row);
                rows.Add(row);
            }

            Settle(row, until);
            row.GoldPerHour = GoldRules.RateForHoldings(userId, regions);
            row.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Pays a player a lump — a claimed clear today. Settles first so the lump lands on top of an
    /// up-to-date purse rather than being swept into the next settlement at the wrong rate.
    /// </summary>
    public async Task<long> GrantAsync(Guid gameInstanceId, string userId, long gold, string reason)
    {
        if (gold <= 0 || string.IsNullOrEmpty(userId)) return 0;

        var row = await FindOrCreateAsync(gameInstanceId, userId);
        Settle(row, DateTime.UtcNow);

        row.SettledGold += gold;
        row.LifetimeFromClears += gold;
        row.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        long balance = (long)Math.Floor(row.SettledGold);

        _sessionLog.Log("GOLD-GRANT",
            $"user={userId} instance={gameInstanceId} reason={reason} gold={gold} balance={balance}");

        return balance;
    }

    /// <summary>
    /// Deducts gold, all of it or none. Returns what went wrong rather than throwing, so a caller
    /// can answer "you cannot afford that" without a stack trace.
    /// </summary>
    public async Task<GoldOutcome> SpendAsync(Guid gameInstanceId, string userId, long cost, string reason)
    {
        if (cost <= 0)
            return new GoldOutcome(GoldError.NothingToSpend, "No gold was named.");

        var row = await FindOrCreateAsync(gameInstanceId, userId);
        Settle(row, DateTime.UtcNow);

        long balance = (long)Math.Floor(row.SettledGold);
        if (balance < cost)
        {
            _sessionLog.Log("GOLD-REFUSE",
                $"user={userId} instance={gameInstanceId} reason={reason} wanted={cost} held={balance}");

            return new GoldOutcome(GoldError.InsufficientGold,
                $"Not enough gold: {cost} needed, {balance} held.", balance);
        }

        row.SettledGold -= cost;
        row.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        long remaining = (long)Math.Floor(row.SettledGold);

        _sessionLog.Log("GOLD-SPEND",
            $"user={userId} instance={gameInstanceId} reason={reason} gold={cost} balance={remaining}");

        return new GoldOutcome(GoldError.None, null, remaining);
    }

    // -----------------------------------------------------------------

    /// <summary>
    /// Banks whatever the holdings have paid since the last settlement and moves the marker. Called
    /// before every read that matters and before every write, so the stored figure is always the
    /// truth as of its own timestamp.
    /// </summary>
    private static void Settle(PlayerGold row, DateTime until)
    {
        double earned = GoldRules.Accrued(row.GoldPerHour, row.LastSettledAt, until);

        if (earned > 0)
        {
            row.SettledGold += earned;
            row.LifetimeFromHoldings += earned;
        }

        if (until > row.LastSettledAt)
            row.LastSettledAt = until;
    }

    private static long ProjectedBalance(PlayerGold row, DateTime at)
        => (long)Math.Floor(row.SettledGold + GoldRules.Accrued(row.GoldPerHour, row.LastSettledAt, at));

    private async Task<PlayerGold> FindOrCreateAsync(Guid gameInstanceId, string userId)
    {
        var row = await _context.PlayerGold
            .FirstOrDefaultAsync(g => g.GameInstanceId == gameInstanceId && g.UserId == userId);

        if (row != null) return row;

        row = new PlayerGold
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            LastSettledAt = DateTime.UtcNow
        };

        _context.PlayerGold.Add(row);
        return row;
    }
}
