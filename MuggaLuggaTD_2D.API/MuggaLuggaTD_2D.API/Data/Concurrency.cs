using Microsoft.EntityFrameworkCore;

namespace MuggaLuggaTD_2D.API.Data;

/// <summary>
/// A row two requests may write at once: a purse, a material stack, a Bazaar listing (Hardening 4).
/// <see cref="Revision"/> is a concurrency token that <see cref="ApplicationDbContext"/> raises on
/// every update, so the second of two racing writes finds the row changed under it and is refused,
/// rather than quietly overwriting the first. Callers retry with <see cref="Concurrency.RetryAsync{T}"/>.
/// </summary>
public interface IRevisioned
{
    long Revision { get; set; }
}

public static class Concurrency
{
    public const int Tries = 3;

    /// <summary>
    /// Runs <paramref name="attempt"/>, and if its save lost a race, reloads the rows that changed and
    /// runs it again, up to <see cref="Tries"/> times. The attempt must read what it decides on afresh
    /// each time (a tracked row is reloaded in place, so re-reading it through the context is enough).
    /// </summary>
    public static async Task<T> RetryAsync<T>(DbContext context, Func<Task<T>> attempt)
    {
        for (int tries = 1; ; tries++)
        {
            try
            {
                return await attempt();
            }
            catch (DbUpdateConcurrencyException ex) when (tries < Tries)
            {
                foreach (var entry in ex.Entries)
                {
                    if (entry.State == EntityState.Added) continue;
                    await entry.ReloadAsync();
                }
            }
        }
    }

    /// <summary>
    /// Re-reads rows this context already holds unchanged. A query does not refresh a row the context
    /// tracks, so a purse read earlier in the request would be judged on its old balance - and a spend
    /// refused on a stale figure never saves, so no race is ever noticed. Pending edits are kept.
    /// </summary>
    public static async Task RefreshAsync<T>(DbContext context, Func<T, bool> which) where T : class
    {
        var stale = context.ChangeTracker.Entries<T>()
            .Where(e => e.State == EntityState.Unchanged && which(e.Entity))
            .ToList();
        foreach (var entry in stale)
            await entry.ReloadAsync();
    }

    public static async Task RetryAsync(DbContext context, Func<Task> attempt) =>
        await RetryAsync(context, async () => { await attempt(); return true; });

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction (Hardening 3): a trade that spends
    /// gold, moves goods and updates a listing either happens whole or not at all. Joins a transaction
    /// already open, and is a plain call where the provider has none (the tests' in-memory store).
    /// </summary>
    public static async Task<T> TransactionAsync<T>(DbContext context, Func<Task<T>> work)
    {
        if (!context.Database.IsRelational() || context.Database.CurrentTransaction != null)
            return await work();

        await using var transaction = await context.Database.BeginTransactionAsync();
        var result = await work();
        await transaction.CommitAsync();
        return result;
    }
}
