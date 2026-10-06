using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.DTOs;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// The NPC factions' manpower (<c>docs/design/npc-factions.md</c>, phase 1): read it, settle it, bank
/// a ransom into it, and the Development-only controls the Combat Debug window drives.
///
/// <para>A faction's row is made the first time anything reads it, <b>at full strength</b>: a faction
/// seated by world generation has stood on its land since before anyone arrived. From then on it is
/// settled lazily against the cap its land supports now (<see cref="FactionStrengthRules.Settle"/>),
/// so a faction that has lost land is no stronger than what is left.</para>
///
/// <para>Phase 1 acts on nothing: strength is shown, and ransom feeds it. Raids, sieges and growth
/// spend it in the phases after.</para>
/// </summary>
public class FactionService
{
    private readonly ApplicationDbContext _context;
    private readonly ISessionLog _sessionLog;

    public FactionService(ApplicationDbContext context, ISessionLog sessionLog)
    {
        _context = context;
        _sessionLog = sessionLog;
    }

    /// <summary>Every faction in the realm, settled to now. Null when the realm has no world.</summary>
    public async Task<FactionsResponse?> ReadAsync(Guid gameInstanceId, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var regions = await RegionsAsync(gameInstanceId);
        if (regions == null) return null;

        var rows = await SettleAsync(gameInstanceId, regions, now);
        await _context.SaveChangesAsync();
        return Describe(rows, regions, now);
    }

    /// <summary>
    /// A ransom paid for heroes <paramref name="faction"/> holds, banked as its strength (Mike,
    /// 2026-10-06). Returns the strength after, or null when the realm has no world.
    /// </summary>
    public async Task<double?> BankRansomAsync(Guid gameInstanceId, FactionId faction, long gold, DateTime? utcNow = null)
    {
        if (!FactionStrengthRules.IsFaction(faction) || gold <= 0) return null;
        var now = utcNow ?? DateTime.UtcNow;
        var regions = await RegionsAsync(gameInstanceId);
        if (regions == null) return null;

        var rows = await SettleAsync(gameInstanceId, regions, now);
        var row = rows.First(r => r.Faction == faction);
        double before = row.Strength;
        row.Strength = FactionStrengthRules.BankRansom(row.Strength, gold, FactionStrengthRules.Cap(faction, regions));
        await _context.SaveChangesAsync();

        _sessionLog.Log("FACTION-RANSOM",
            $"instance={gameInstanceId} faction={faction} gold={gold} strength={before:F0}->{row.Strength:F0}");
        return row.Strength;
    }

    /// <summary>
    /// The Combat Debug window's faction controls. An empty or unknown <c>Faction</c> applies to every
    /// faction. <c>SimulateHours</c> lets that much time pass, as if nobody had looked.
    /// </summary>
    public async Task<FactionsResponse?> DebugAsync(Guid gameInstanceId, FactionDebugRequest request, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var regions = await RegionsAsync(gameInstanceId);
        if (regions == null) return null;

        var rows = await SettleAsync(gameInstanceId, regions, now);
        bool all = !Enum.TryParse<FactionId>(request.Faction, ignoreCase: true, out var only) || !FactionStrengthRules.IsFaction(only);

        foreach (var row in rows.Where(r => all || r.Faction == only))
        {
            double cap = FactionStrengthRules.Cap(row.Faction, regions);

            if (request.SimulateHours is > 0)
            {
                // Wind its clock back rather than forward: settling to now then covers those hours.
                var span = TimeSpan.FromHours(request.SimulateHours.Value);
                row.SettledAtUtc -= span;
                if (row.BloodiedUntilUtc.HasValue) row.BloodiedUntilUtc -= span;
                row.Strength = FactionStrengthRules.Settle(row.Strength, row.SettledAtUtc, now, cap, row.BloodiedUntilUtc);
                row.SettledAtUtc = now;
            }

            if (request.Readiness.HasValue)
                row.Strength = Math.Clamp(request.Readiness.Value, 0, 1) * cap;

            if (request.Bloody)
                row.BloodiedUntilUtc = now + FactionStrengthRules.BloodiedFor;

            if (request.ClearBloodied)
                row.BloodiedUntilUtc = null;

            _sessionLog.Log("FACTION-DEBUG",
                $"instance={gameInstanceId} faction={row.Faction} strength={row.Strength:F0}/{cap:F0} " +
                $"bloodiedUntil={row.BloodiedUntilUtc:O} request={request}");
        }

        await _context.SaveChangesAsync();
        return Describe(rows, regions, now);
    }

    /// <summary>A new season's map seats its factions anew, so their rows go with the old one.</summary>
    public static Task ResetRealmAsync(ApplicationDbContext context, Guid realmId)
    {
        context.FactionStates.RemoveRange(context.FactionStates.Where(f => f.GameInstanceId == realmId));
        return Task.CompletedTask;
    }

    /// <summary>Each faction's row, made if missing and settled to <paramref name="now"/>. Not saved.</summary>
    private async Task<List<FactionState>> SettleAsync(Guid gameInstanceId, IReadOnlyCollection<WorldRegionData> regions, DateTime now)
    {
        var rows = await _context.FactionStates.Where(f => f.GameInstanceId == gameInstanceId).ToListAsync();

        foreach (var faction in FactionStrengthRules.All)
        {
            double cap = FactionStrengthRules.Cap(faction, regions);
            var row = rows.FirstOrDefault(r => r.Faction == faction);
            if (row == null)
            {
                row = new FactionState
                {
                    GameInstanceId = gameInstanceId,
                    Faction = faction,
                    Strength = cap,
                    SettledAtUtc = now
                };
                _context.FactionStates.Add(row);
                rows.Add(row);
                continue;
            }

            row.Strength = FactionStrengthRules.Settle(row.Strength, row.SettledAtUtc, now, cap, row.BloodiedUntilUtc);
            if (now > row.SettledAtUtc) row.SettledAtUtc = now;
        }

        return rows;
    }

    private static FactionsResponse Describe(List<FactionState> rows, IReadOnlyCollection<WorldRegionData> regions, DateTime now)
    {
        var list = new List<FactionStrengthResponse>();
        foreach (var faction in FactionStrengthRules.All)
        {
            var row = rows.First(r => r.Faction == faction);
            double cap = FactionStrengthRules.Cap(faction, regions);
            bool bloodied = FactionStrengthRules.IsBloodied(row.BloodiedUntilUtc, now);
            list.Add(new FactionStrengthResponse(
                faction.ToString(),
                (long)Math.Round(row.Strength),
                (long)Math.Round(cap),
                Math.Round(FactionStrengthRules.Readiness(row.Strength, cap), 3),
                FactionStrengthRules.Word(row.Strength, cap, bloodied).ToString(),
                FactionStrengthRules.RegionsHeld(faction, regions),
                bloodied ? row.BloodiedUntilUtc : null));
        }
        return new FactionsResponse(list, now);
    }

    private async Task<List<WorldRegionData>?> RegionsAsync(Guid gameInstanceId)
    {
        var row = await _context.WorldViewGameData.AsNoTracking().FirstOrDefaultAsync(w => w.GameInstanceId == gameInstanceId);
        if (row == null || string.IsNullOrEmpty(row.GameData)) return null;
        var regions = WorldRegionBlob.ReadAllRegions(JsonNode.Parse(row.GameData));
        return regions.Count == 0 ? null : regions;
    }
}
