using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>One row of any board. Which fields are filled depends on the board.</summary>
public record LeaderboardRow
{
    public int Rank { get; init; }
    public string UserId { get; init; } = "";
    public string Name { get; init; } = "";
    public int Crowns { get; init; }
    public string? RealmName { get; init; }

    /// <summary>The number the board ranks: a rate, a power, kills or regions.</summary>
    public double Value { get; init; }

    // Rate boards
    public int RegionsHeld { get; init; }
    public int SeasonNumber { get; init; }
    public DateTime? EndedAt { get; init; }
    public double DaysIn { get; init; }
    public int SeasonsPlayed { get; init; }
    public double HoldingPoints { get; init; }
    public double ClearingPoints { get; init; }
    public double RaidingPoints { get; init; }

    // Heroes and Companies
    /// <summary>The hero's or the company's name.</summary>
    public string? Subject { get; init; }
    /// <summary>The hero's class, or the company's size ("3 heroes").</summary>
    public string? Detail { get; init; }
    public int Level { get; init; }
}

/// <summary>A page of a board: the top rows, the asker's own row (null if they have none), and when it was built.</summary>
public record LeaderboardPage(List<LeaderboardRow> Rows, LeaderboardRow? Me, DateTime GeneratedAt, List<int>? Months = null)
{
    /// <summary>How many rows the whole board has ("#12 of 140").</summary>
    public int Total { get; init; }
}

/// <summary>
/// The cross-world boards (Leaderboards spec, as revised 2026-10-10).
/// <list type="bullet">
/// <item><b>Rate:</b> a season's average points per hour. NOW projects every realm's running season; THIS MONTH
/// ranks the qualified seasons that ended in a calendar month; ALL-TIME ranks crowns, then best rate.</item>
/// <item><b>Living boards</b> (Heroes, Companies, Slayers, Conquerors): built from what the server holds now, kept
/// as a snapshot and rebuilt when someone asks and it is over an hour old. Nothing ticks.</item>
/// </list>
/// No factions on any board (row 5).
/// </summary>
public class LeaderboardService
{
    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ILogger<LeaderboardService> _logger;

    /// <summary>One rebuild at a time: two viewers arriving together build the board once.</summary>
    private static readonly SemaphoreSlim Building = new(1, 1);

    /// <summary>The clock, so tests can move it.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public LeaderboardService(ApplicationDbContext context, IGameContentProvider content, ILogger<LeaderboardService> logger)
    {
        _context = context;
        _content = content;
        _logger = logger;
    }

    // -----------------------------------------------------------------
    // Rate boards
    // -----------------------------------------------------------------

    /// <summary>Every realm's running season, projected and not stored: points so far over hours played.</summary>
    public async Task<LeaderboardPage> NowAsync(string me)
    {
        var now = Clock();
        var realms = await _context.GameInstances.AsNoTracking()
            .Select(g => new { g.Id, g.Name, g.SeasonNumber, g.SeasonStartedAt, g.SeasonLengthDays })
            .ToListAsync();

        var scores = await _context.SeasonScores.AsNoTracking().ToListAsync();
        var rows = new List<LeaderboardRow>();
        foreach (var realm in realms)
        {
            var endsAt = realm.SeasonStartedAt.AddDays(realm.SeasonLengthDays);
            var until = SeasonScoreRules.AccrueUntil(now, endsAt);
            foreach (var score in scores.Where(s => s.GameInstanceId == realm.Id && s.SeasonNumber == realm.SeasonNumber))
            {
                double points = score.SettledPoints + SeasonScoreRules.Accrued(score.PointsPerHour, score.LastSettledAt, until);
                var rate = LeaderboardRules.NowRate(points, realm.SeasonStartedAt, score.JoinedAt, until);
                if (rate == null || rate <= 0) continue;
                rows.Add(new LeaderboardRow
                {
                    UserId = score.UserId,
                    RealmName = realm.Name,
                    Value = Math.Round(rate.Value, 1),
                    SeasonNumber = realm.SeasonNumber,
                    DaysIn = Math.Round((until - realm.SeasonStartedAt).TotalDays, 1),
                });
            }
        }

        // A player in two realms shows their better one.
        rows = rows.GroupBy(r => r.UserId).Select(g => g.OrderByDescending(r => r.Value).First()).ToList();
        return await PageAsync(rows, me, now);
    }

    /// <summary>The qualified seasons that ended in <paramref name="month"/> (yyyymm), each player's best.</summary>
    public async Task<LeaderboardPage> MonthAsync(int month, string me)
    {
        var results = await _context.SeasonResults.AsNoTracking()
            .Where(r => r.EndedMonth == month && r.Qualifies)
            .ToListAsync();

        var rows = results
            .GroupBy(r => r.UserId)
            .Select(g => g.OrderByDescending(r => r.AverageRate).First())
            .Select(r => new LeaderboardRow
            {
                UserId = r.UserId,
                RealmName = r.RealmName,
                Value = r.AverageRate,
                RegionsHeld = r.RegionsHeld,
                SeasonNumber = r.SeasonNumber,
                EndedAt = r.SeasonEndedAt,
                HoldingPoints = r.HoldingPoints,
                ClearingPoints = r.ClearingPoints,
                RaidingPoints = r.RaidingPoints,
            })
            .ToList();

        var months = await _context.SeasonResults.AsNoTracking()
            .Where(r => r.Qualifies && r.EndedMonth > 0)
            .Select(r => r.EndedMonth).Distinct().OrderByDescending(m => m).ToListAsync();

        var page = await PageAsync(rows, me, Clock());
        return page with { Months = months };
    }

    /// <summary>Crowns, then the best qualified rate, then seasons played.</summary>
    public async Task<LeaderboardPage> AllTimeAsync(string me)
    {
        var grouped = await _context.SeasonResults.AsNoTracking()
            .GroupBy(r => r.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Crowns = g.Count(r => r.Crowned),
                Best = g.Where(r => r.Qualifies).Max(r => (double?)r.AverageRate) ?? 0,
                Seasons = g.Count(),
            })
            .ToListAsync();

        var rows = grouped
            .OrderByDescending(g => g.Crowns).ThenByDescending(g => g.Best).ThenByDescending(g => g.Seasons)
            .Select(g => new LeaderboardRow { UserId = g.UserId, Crowns = g.Crowns, Value = g.Best, SeasonsPlayed = g.Seasons })
            .ToList();

        return await PageAsync(rows, me, Clock(), alreadyOrdered: true,
            sameRank: (a, b) => a.Crowns == b.Crowns && Math.Abs(a.Value - b.Value) < 0.0001 && a.SeasonsPlayed == b.SeasonsPlayed);
    }

    // -----------------------------------------------------------------
    // Living boards
    // -----------------------------------------------------------------

    /// <summary>
    /// A living board for a bracket (or, for Slayers, a window), rebuilt when its snapshot is over an hour old.
    /// </summary>
    public async Task<LeaderboardPage> LivingAsync(LeaderboardBoard board, string bracket, string me)
    {
        if (board == LeaderboardBoard.Rate) throw new ArgumentException("Rate is not a living board.");

        var snapshot = await _context.LeaderboardSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Board == board.ToString() && s.Bracket == bracket);

        if (snapshot == null || Clock() - snapshot.BuiltAt > LeaderboardRules.LivingMaxAge)
        {
            await Building.WaitAsync();
            try
            {
                // Someone may have built it while this one waited.
                snapshot = await _context.LeaderboardSnapshots
                    .FirstOrDefaultAsync(s => s.Board == board.ToString() && s.Bracket == bracket);
                if (snapshot == null || Clock() - snapshot.BuiltAt > LeaderboardRules.LivingMaxAge)
                    snapshot = await RebuildAsync(board, bracket, snapshot);
            }
            finally { Building.Release(); }
        }

        var rows = JsonSerializer.Deserialize<List<LeaderboardRow>>(snapshot.RowsJson) ?? new List<LeaderboardRow>();
        var names = await NamesAsync(rows.Select(r => r.UserId).Distinct().ToList());
        rows = rows.Select(r => r with { Name = names.TryGetValue(r.UserId, out var n) ? n : r.Name }).ToList();

        // A player can hold several rows (heroes, companies): their own row is their best.
        var mine = rows.FirstOrDefault(r => r.UserId == me);
        return new LeaderboardPage(rows.Take(LeaderboardRules.Top).ToList(), mine, snapshot.BuiltAt) { Total = rows.Count };
    }

    /// <summary>Builds a living board now and stores it.</summary>
    private async Task<LeaderboardSnapshot> RebuildAsync(LeaderboardBoard board, string bracket, LeaderboardSnapshot? snapshot)
    {
        var rows = board switch
        {
            LeaderboardBoard.Heroes => await HeroRowsAsync(Bracket(bracket)),
            LeaderboardBoard.Companies => await CompanyRowsAsync(Bracket(bracket)),
            LeaderboardBoard.Conquerors => await ConquerorRowsAsync(Bracket(bracket)),
            _ => await SlayerRowsAsync(Enum.TryParse<SlayerWindow>(bracket, true, out var w) ? w : SlayerWindow.Week),
        };

        var ranked = Rank(rows.OrderByDescending(r => r.Value).ToList(), (a, b) => Math.Abs(a.Value - b.Value) < 0.0001);
        var crowns = await CrownsAsync(ranked.Select(r => r.UserId).Distinct().ToList());
        ranked = ranked.Select(r => r with { Crowns = crowns.TryGetValue(r.UserId, out var c) ? c : 0 }).ToList();

        if (snapshot == null)
        {
            snapshot = new LeaderboardSnapshot { Board = board.ToString(), Bracket = bracket };
            _context.LeaderboardSnapshots.Add(snapshot);
        }
        snapshot.BuiltAt = Clock();
        snapshot.RowsJson = JsonSerializer.Serialize(ranked);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Leaderboard {Board}/{Bracket} rebuilt: {Rows} row(s).", board, bracket, ranked.Count);
        return snapshot;
    }

    private static SeasonBracket Bracket(string bracket) =>
        Enum.TryParse<SeasonBracket>(bracket, true, out var b) ? b : SeasonBracket.Standard;

    /// <summary>The realms of a bracket, with their names.</summary>
    private async Task<Dictionary<Guid, string>> RealmsInAsync(SeasonBracket bracket)
    {
        var realms = await _context.GameInstances.AsNoTracking()
            .Select(g => new { g.Id, g.Name, g.SeasonLengthDays })
            .ToListAsync();
        return realms.Where(r => LeaderboardRules.BracketOf(r.SeasonLengthDays) == bracket)
            .ToDictionary(r => r.Id, r => r.Name);
    }

    /// <summary>Every hero in the bracket's realms, priced as the server prices them (<c>PartyPowerCalculator</c>).</summary>
    private async Task<List<LeaderboardRow>> HeroRowsAsync(SeasonBracket bracket)
    {
        var realms = await RealmsInAsync(bracket);
        var rows = new List<LeaderboardRow>();
        foreach (var (realmId, realmName) in realms)
        {
            var saves = await _context.PlayerGameData.AsNoTracking()
                .Where(p => p.GameInstanceId == realmId).Select(p => new { p.UserId, p.GameData }).ToListAsync();
            foreach (var row in saves)
            {
                var save = ReadSave(row.GameData);
                if (save?.Characters == null) continue;
                foreach (var hero in save.Characters.Where(c => c != null))
                {
                    float power = PartyPowerCalculator.CalculateCharacterPower(hero, save.InventoryItems, _content.AbilityTemplates, _content.Signatures);
                    if (power <= 0) continue;
                    rows.Add(new LeaderboardRow
                    {
                        UserId = row.UserId,
                        RealmName = realmName,
                        Value = Math.Round(power, 1),
                        Subject = Naming.ForCharacter(hero.CharacterName, hero.LinkName, hero.Id, hero.TypeName) ?? hero.TypeName,
                        Detail = hero.TypeName,
                        Level = (int)Math.Max(1, hero.Level),
                    });
                }
            }
        }
        return rows;
    }

    /// <summary>Every company in the bracket's realms: its heroes' power together.</summary>
    private async Task<List<LeaderboardRow>> CompanyRowsAsync(SeasonBracket bracket)
    {
        var realms = await RealmsInAsync(bracket);
        var parties = await _context.PlayerParties.AsNoTracking()
            .Where(p => realms.Keys.Contains(p.GameInstanceId)).ToListAsync();
        var rows = new List<LeaderboardRow>();
        foreach (var group in parties.GroupBy(p => (p.GameInstanceId, p.UserId)))
        {
            var data = await _context.PlayerGameData.AsNoTracking()
                .Where(p => p.GameInstanceId == group.Key.GameInstanceId && p.UserId == group.Key.UserId)
                .Select(p => p.GameData).FirstOrDefaultAsync();
            var save = ReadSave(data);
            if (save == null) continue;
            foreach (var party in group)
            {
                var ids = MarchingArmy.ReadIds(party.CharacterIdsJson);
                float power = PartyPowerCalculator.CalculatePartyPower(save, ids, _content.AbilityTemplates, _content.Signatures);
                if (power <= 0) continue;
                int heroes = save.Characters.Count(c => c != null && ids.Contains(c.Id));
                rows.Add(new LeaderboardRow
                {
                    UserId = group.Key.UserId,
                    RealmName = realms[group.Key.GameInstanceId],
                    Value = Math.Round(power, 1),
                    Subject = party.Name,
                    Detail = $"{heroes} hero{(heroes == 1 ? "" : "es")}",
                });
            }
        }
        return rows;
    }

    /// <summary>Regions each player holds right now, in the bracket's realms.</summary>
    private async Task<List<LeaderboardRow>> ConquerorRowsAsync(SeasonBracket bracket)
    {
        var realms = await RealmsInAsync(bracket);
        var worlds = await _context.WorldViewGameData.AsNoTracking()
            .Where(w => realms.Keys.Contains(w.GameInstanceId)).Select(w => new { w.GameInstanceId, w.GameData }).ToListAsync();
        var rows = new List<LeaderboardRow>();
        foreach (var world in worlds)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(world.GameData); }
            catch (JsonException) { continue; }
            var regions = WorldRegionBlob.ReadAllRegions(node);
            foreach (var holder in regions.Where(r => r.Ownership == LocationOwnership.Player && !string.IsNullOrEmpty(r.OwnerUserId))
                         .GroupBy(r => r.OwnerUserId))
            {
                rows.Add(new LeaderboardRow
                {
                    UserId = holder.Key,
                    RealmName = realms[world.GameInstanceId],
                    Value = holder.Count(),
                    RegionsHeld = holder.Count(),
                });
            }
        }
        return rows;
    }

    /// <summary>Enemies slain in every realm: the last 7 days, or all-time.</summary>
    private async Task<List<LeaderboardRow>> SlayerRowsAsync(SlayerWindow window)
    {
        var query = _context.SlayerTallies.AsNoTracking();
        if (window == SlayerWindow.Week)
        {
            var from = Clock().Date.AddDays(-(LeaderboardRules.SlayerWindowDays - 1));
            query = query.Where(t => t.Day >= from);
        }
        var sums = await query.GroupBy(t => t.UserId)
            .Select(g => new { UserId = g.Key, Kills = g.Sum(t => t.Kills) })
            .ToListAsync();
        return sums.Where(s => s.Kills > 0)
            .Select(s => new LeaderboardRow { UserId = s.UserId, Value = s.Kills })
            .ToList();
    }

    // -----------------------------------------------------------------
    // Counting kills (fed by QuestService.RecordAsync: every fight's clamped kills)
    // -----------------------------------------------------------------

    /// <summary>
    /// Adds a fight's kills to the player's day (the Slayers board) and to their season's score. Never throws:
    /// a board must not cost anyone their quest progress.
    /// </summary>
    public static async Task TallyKillsAsync(ApplicationDbContext context, ILogger logger, Guid realm, string userId,
        IReadOnlyDictionary<string, int>? kills, DateTime now)
    {
        long slain = kills?.Values.Where(k => k > 0).Sum(k => (long)k) ?? 0;
        if (slain <= 0 || string.IsNullOrEmpty(userId)) return;
        var day = now.Date;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var tally = await context.SlayerTallies.FirstOrDefaultAsync(t => t.UserId == userId && t.Day == day);
                if (tally == null) context.SlayerTallies.Add(new SlayerTally { UserId = userId, Day = day, Kills = slain });
                else tally.Kills += slain;

                var season = await context.GameInstances.AsNoTracking().Where(g => g.Id == realm).Select(g => g.SeasonNumber).FirstOrDefaultAsync();
                var score = await context.SeasonScores.FirstOrDefaultAsync(s =>
                    s.GameInstanceId == realm && s.UserId == userId && s.SeasonNumber == season);
                if (score != null) score.Kills += slain;

                await context.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException ex) when (attempt == 0)
            {
                // Another claim made today's row first: forget this try and add to theirs.
                logger.LogDebug(ex, "Slayer tally raced; trying again.");
                foreach (var entry in context.ChangeTracker.Entries()
                             .Where(e => e.Entity is SlayerTally || e.Entity is SeasonScore).ToList())
                    entry.State = EntityState.Detached;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not count {Kills} kill(s) for {User} on the Slayers board.", slain, userId);
                return;
            }
        }
    }

    // -----------------------------------------------------------------
    // Debug (Development; Combat Debug → Seasons)
    // -----------------------------------------------------------------

    /// <summary>
    /// Writes 30 made-up qualified results across the last 3 months, for players named "Chronicler 1" to "10",
    /// so the boards can be seen full on a local server. The players are made if they don't exist.
    /// </summary>
    public async Task<int> DebugSeedAsync()
    {
        var now = Clock();
        var random = new Random(1234);
        var users = new List<ApplicationUser>();
        for (int i = 1; i <= 10; i++)
        {
            string userName = $"chronicler{i}";
            var user = await _context.Users.FirstOrDefaultAsync(u => u.UserName == userName);
            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = userName, NormalizedUserName = userName.ToUpperInvariant(),
                    Email = $"{userName}@seed.test", NormalizedEmail = $"{userName}@seed.test".ToUpperInvariant(),
                    DisplayName = $"Chronicler {i}", SecurityStamp = Guid.NewGuid().ToString(),
                };
                _context.Users.Add(user);
            }
            users.Add(user);
        }

        int written = 0;
        for (int m = 0; m < 3; m++)
        {
            var ended = new DateTime(now.Year, now.Month, 1, 12, 0, 0, DateTimeKind.Utc).AddMonths(-m).AddDays(5 + m);
            if (ended > now) ended = now.AddHours(-1);
            for (int k = 0; k < 10; k++)
            {
                var user = users[k];
                int days = new[] { 7, 30, 90 }[k % 3];
                double rate = Math.Round(20 + random.NextDouble() * 160, 1);
                _context.SeasonResults.Add(new SeasonResult
                {
                    UserId = user.Id,
                    SeasonNumber = 100 + m,
                    Rank = k % 4 + 1,
                    TotalPoints = Math.Round(rate * days * 24),
                    HoldingPoints = Math.Round(rate * days * 24 * 0.7),
                    ClearingPoints = Math.Round(rate * days * 24 * 0.2),
                    RaidingPoints = Math.Round(rate * days * 24 * 0.1),
                    RegionsHeld = 1 + k % 5,
                    SeasonStartedAt = ended.AddDays(-days),
                    SeasonEndedAt = ended,
                    RealmName = $"Seeded Realm {k % 3 + 1}",
                    PlayerName = user.DisplayName,
                    SeasonLengthDays = days,
                    PlayersInSeason = 4,
                    AverageRate = rate,
                    Crowned = k % 4 == 0,
                    HoursInSeason = days * 24,
                    Qualifies = true,
                    EndedMonth = LeaderboardRules.MonthOf(ended),
                });
                written++;
            }
        }
        await _context.SaveChangesAsync();
        return written;
    }

    // -----------------------------------------------------------------

    /// <summary>Ranks the rows (ties share a rank), names them, and returns the top with the asker's own row.</summary>
    private async Task<LeaderboardPage> PageAsync(List<LeaderboardRow> rows, string me, DateTime at,
        bool alreadyOrdered = false, Func<LeaderboardRow, LeaderboardRow, bool>? sameRank = null)
    {
        var ordered = alreadyOrdered ? rows : rows.OrderByDescending(r => r.Value).ToList();
        var ranked = Rank(ordered, sameRank ?? ((a, b) => Math.Abs(a.Value - b.Value) < 0.0001));

        var shown = ranked.Take(LeaderboardRules.Top).ToList();
        var mine = ranked.FirstOrDefault(r => r.UserId == me);
        var ids = shown.Select(r => r.UserId).Append(me).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        var names = await NamesAsync(ids);
        var crowns = await CrownsAsync(ids);

        LeaderboardRow Dress(LeaderboardRow r) => r with
        {
            Name = names.TryGetValue(r.UserId, out var n) ? n : "Unknown",
            Crowns = r.Crowns > 0 ? r.Crowns : crowns.TryGetValue(r.UserId, out var c) ? c : 0,
        };

        return new LeaderboardPage(shown.Select(Dress).ToList(), mine == null ? null : Dress(mine), at) { Total = ranked.Count };
    }

    private static List<LeaderboardRow> Rank(List<LeaderboardRow> ordered, Func<LeaderboardRow, LeaderboardRow, bool> sameRank)
    {
        var ranked = new List<LeaderboardRow>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            int rank = i > 0 && sameRank(ordered[i - 1], ordered[i]) ? ranked[i - 1].Rank : i + 1;
            ranked.Add(ordered[i] with { Rank = rank });
        }
        return ranked;
    }

    private async Task<Dictionary<string, string>> NamesAsync(IReadOnlyList<string> userIds)
    {
        var users = await _context.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName, u.DisplayName })
            .ToListAsync();
        return users.ToDictionary(u => u.Id, u => PlayerNames.Of(u.DisplayName, u.UserName), StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, int>> CrownsAsync(IReadOnlyList<string> userIds)
    {
        var counts = await _context.SeasonResults.AsNoTracking()
            .Where(r => userIds.Contains(r.UserId) && r.Crowned)
            .GroupBy(r => r.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync();
        return counts.ToDictionary(c => c.UserId, c => c.Count, StringComparer.Ordinal);
    }

    private StateManagement.Models.UserSaveData? ReadSave(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return Newtonsoft.Json.JsonConvert.DeserializeObject<StateManagement.Models.UserSaveData>(json); }
        catch (Newtonsoft.Json.JsonException) { return null; }
    }
}
