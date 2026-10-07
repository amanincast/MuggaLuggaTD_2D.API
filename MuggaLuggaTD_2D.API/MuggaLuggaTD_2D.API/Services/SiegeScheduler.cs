using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Sweeps once a minute for sieges whose windows have closed, and moves them on.
///
/// <para>This is the one place in the game that runs on a clock rather than on requests, and it is
/// here for one reason: a region's hold must be frozen at the moment muster <i>actually</i> closes.
/// Everything else about a siege would be correct evaluated lazily - every read advances what is due
/// - but a hold frozen whenever somebody next happened to look would let the defender keep
/// reinforcing after the window that was theirs had shut.</para>
///
/// <para>Nothing depends on it for correctness beyond that. If it stops, reads still advance sieges,
/// just with a looser freeze. A failed sweep is logged and the next one tries again.</para>
/// </summary>
public class SiegeScheduler : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SiegeScheduler> _logger;

    /// <summary>
    /// When the factions last took a turn. Starts at a restart, so a restart is a quarter hour of
    /// quiet rather than a burst.
    /// </summary>
    private DateTime _lastFactionTurn = DateTime.UtcNow;

    public SiegeScheduler(IServiceScopeFactory scopes, ILogger<SiegeScheduler> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                // A fresh scope per sweep: the DbContext is scoped, and a long-lived one would keep
                // every siege it ever loaded tracked forever.
                using var scope = _scopes.CreateScope();
                var sieges = scope.ServiceProvider.GetRequiredService<WorldSiegeService>();

                int advanced = await sieges.AdvanceAllDueAsync();
                if (advanced > 0)
                    _logger.LogInformation("Siege sweep advanced {Count} siege(s).", advanced);

                // Fortification works finish on the same sweep, so a region's walls rise within a
                // minute of their time even if nobody opens the map.
                var fortify = scope.ServiceProvider.GetRequiredService<FortifyService>();
                int raised = await fortify.CompleteAllDueAsync();
                if (raised > 0)
                    _logger.LogInformation("Fortify sweep finished {Count} work(s).", raised);

                // A faction's siege is settled at its muster's close, so it is swept every minute as a
                // player's muster is (npc-factions.md phase 3).
                var factions = scope.ServiceProvider.GetRequiredService<FactionService>();
                int settled = await factions.SettleAllDueSiegesAsync(DateTime.UtcNow, _logger);
                if (settled > 0)
                    _logger.LogInformation("Faction sieges settled: {Count}.", settled);

                // The NPC factions take their turn every quarter hour, not every minute
                // (docs/design/npc-factions.md §4).
                var now = DateTime.UtcNow;
                if (now - _lastFactionTurn >= FactionDecisionRules.SweepInterval)
                {
                    _lastFactionTurn = now;
                    int acted = await factions.ActAllAsync(now, _logger);
                    if (acted > 0)
                        _logger.LogInformation("Faction turn: {Count} thing(s) done.", acted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Siege sweep failed; the next one will try again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
