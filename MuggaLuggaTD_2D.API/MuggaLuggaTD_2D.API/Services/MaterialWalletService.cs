using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum WalletError
{
    None,
    NothingToSpend,
    UnknownMaterial,
    InsufficientMaterials
}

public record WalletOutcome(WalletError Error, string? Message = null)
{
    public bool Succeeded => Error == WalletError.None;
}

/// <summary>
/// The player's material balances: granted by the server when a run is claimed, spent through
/// endpoints, never written by the client.
///
/// <para>Spending is deliberately permissive about <i>why</i> — it takes a reason for the log and
/// then deducts. Nothing is minted by spending, so the worst a forged call can do is destroy the
/// caller's own materials. Granting is the side that has to be guarded, and only run claims and
/// other server-resolved actions can do it.</para>
/// </summary>
public class MaterialWalletService
{
    private readonly ApplicationDbContext _context;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<MaterialWalletService> _logger;

    public MaterialWalletService(
        ApplicationDbContext context, ISessionLog sessionLog, ILogger<MaterialWalletService> logger)
    {
        _context = context;
        _sessionLog = sessionLog;
        _logger = logger;
    }

    /// <summary>Everything this player holds in this realm, highest count first.</summary>
    public async Task<List<PlayerMaterial>> ReadAsync(Guid gameInstanceId, string userId)
        => await _context.PlayerMaterials
            .Where(m => m.GameInstanceId == gameInstanceId && m.UserId == userId)
            .OrderByDescending(m => m.Quantity)
            .ThenBy(m => m.MaterialName)
            .ToListAsync();

    /// <summary>
    /// Adds materials. Called by the actions that pay out — a claimed run today, and whatever else
    /// the design pays in materials later. Saves, so the caller does not have to.
    /// </summary>
    public async Task GrantAsync(
        Guid gameInstanceId, string userId, IReadOnlyList<MaterialGrant> grants, string reason)
    {
        if (grants == null || grants.Count == 0)
            return;

        var existing = await _context.PlayerMaterials
            .Where(m => m.GameInstanceId == gameInstanceId && m.UserId == userId)
            .ToListAsync();

        foreach (var grant in grants)
        {
            if (string.IsNullOrWhiteSpace(grant?.MaterialName) || grant.Quantity <= 0)
                continue;

            var row = existing.FirstOrDefault(m => m.MaterialName == grant.MaterialName);
            if (row == null)
            {
                row = new PlayerMaterial
                {
                    GameInstanceId = gameInstanceId,
                    UserId = userId,
                    MaterialName = grant.MaterialName,
                    Quantity = 0
                };
                _context.PlayerMaterials.Add(row);
                existing.Add(row);
            }

            row.Quantity += grant.Quantity;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        _sessionLog.Log("WALLET-GRANT",
            $"user={userId} instance={gameInstanceId} reason={reason} " +
            $"materials={string.Join(",", grants.Select(g => $"{g.MaterialName}x{g.Quantity}"))}");
    }

    /// <summary>
    /// Deducts materials, all of them or none. Returns what went wrong rather than throwing, so the
    /// controller can answer "you do not have those" without a stack trace.
    /// </summary>
    public async Task<WalletOutcome> SpendAsync(
        Guid gameInstanceId, string userId, IReadOnlyList<MaterialGrant> costs, string reason)
    {
        if (costs == null || costs.Count == 0)
            return new WalletOutcome(WalletError.NothingToSpend, "No materials were named.");

        // Fold duplicates first, or two lines of the same material each check against the full balance.
        var required = costs
            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.MaterialName) && c.Quantity > 0)
            .GroupBy(c => c.MaterialName)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Quantity));

        if (required.Count == 0)
            return new WalletOutcome(WalletError.NothingToSpend, "No materials were named.");

        var rows = await _context.PlayerMaterials
            .Where(m => m.GameInstanceId == gameInstanceId && m.UserId == userId)
            .ToListAsync();

        foreach (var (name, quantity) in required)
        {
            var row = rows.FirstOrDefault(m => m.MaterialName == name);
            if (row == null || row.Quantity < quantity)
            {
                _sessionLog.Log("WALLET-REFUSE",
                    $"user={userId} instance={gameInstanceId} reason={reason} " +
                    $"material={name} wanted={quantity} held={row?.Quantity ?? 0}");

                return new WalletOutcome(WalletError.InsufficientMaterials,
                    $"Not enough {name}: {quantity} needed, {row?.Quantity ?? 0} held.");
            }
        }

        foreach (var (name, quantity) in required)
        {
            var row = rows.First(m => m.MaterialName == name);
            row.Quantity -= quantity;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        _sessionLog.Log("WALLET-SPEND",
            $"user={userId} instance={gameInstanceId} reason={reason} " +
            $"materials={string.Join(",", required.Select(r => $"{r.Key}x{r.Value}"))}");

        return new WalletOutcome(WalletError.None);
    }
}
