using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD_2D.API.Services;
using MuggaLuggaTD_2D.API.Tests.TestSupport;
using Npgsql;

namespace MuggaLuggaTD_2D.API.Tests;

/// <summary>
/// Hardening 3 and 4: two writes to one purse cannot lose either, two spends cannot both pass against
/// one balance, and a trade that fails part-way leaves nothing behind.
///
/// <para>The races use two contexts on one in-memory store, which is two requests: one reads, the
/// other writes, then the first writes on what it read. The rollback needs a real transaction, so it
/// runs against a throwaway Postgres database and is skipped when none is reachable.</para>
/// </summary>
public class ConcurrencyTests
{
    private static readonly Guid Realm = Guid.NewGuid();
    private const string User = TestIds.Player;

    private static GoldService Gold(ApplicationDbContext db) => new(db, new FakeSessionLog(), NullLogger<GoldService>.Instance);
    private static MaterialWalletService Wallet(ApplicationDbContext db) => new(db, new FakeSessionLog(), NullLogger<MaterialWalletService>.Instance);

    private static async Task<string> PurseOfAsync(long gold)
    {
        string store = $"race-{Guid.NewGuid()}";
        await using var db = TestDb.Create(store);
        db.PlayerGold.Add(new PlayerGold { GameInstanceId = Realm, UserId = User, SettledGold = gold, LastSettledAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return store;
    }

    [Fact]
    public async Task TwoSpends_FromOneRead_CannotBothPass()
    {
        string store = await PurseOfAsync(100);
        await using var first = TestDb.Create(store);
        await using var second = TestDb.Create(store);

        // The second request has read the purse (100) before the first one spends.
        await second.PlayerGold.SingleAsync();

        Assert.True((await Gold(first).SpendAsync(Realm, User, 80, "first")).Succeeded);
        var late = await Gold(second).SpendAsync(Realm, User, 80, "second");

        Assert.Equal(GoldError.InsufficientGold, late.Error);
        await using var check = TestDb.Create(store);
        Assert.Equal(20, (long)(await check.PlayerGold.SingleAsync()).SettledGold);
    }

    [Fact]
    public async Task AGrantAndASpend_Racing_BothLand()
    {
        string store = await PurseOfAsync(100);
        await using var first = TestDb.Create(store);
        await using var second = TestDb.Create(store);
        await second.PlayerGold.SingleAsync();

        await Gold(first).GrantAsync(Realm, User, 50, "a clear");
        Assert.True((await Gold(second).SpendAsync(Realm, User, 30, "a hire")).Succeeded);

        await using var check = TestDb.Create(store);
        Assert.Equal(120, (long)(await check.PlayerGold.SingleAsync()).SettledGold);
    }

    [Fact]
    public async Task TwoMaterialWrites_Racing_BothLand()
    {
        string store = $"race-{Guid.NewGuid()}";
        await using (var seed = TestDb.Create(store))
        {
            seed.PlayerMaterials.Add(new PlayerMaterial { GameInstanceId = Realm, UserId = User, MaterialName = "Iron Ore", Quantity = 10 });
            await seed.SaveChangesAsync();
        }

        await using var first = TestDb.Create(store);
        await using var second = TestDb.Create(store);
        await second.PlayerMaterials.SingleAsync();

        await Wallet(first).GrantAsync(Realm, User, new[] { new MaterialGrant { MaterialName = "Iron Ore", Quantity = 5 } }, "a clear");
        Assert.True((await Wallet(second).SpendAsync(Realm, User,
            new[] { new MaterialGrant { MaterialName = "Iron Ore", Quantity = 12 } }, "a fortify")).Succeeded);

        await using var check = TestDb.Create(store);
        Assert.Equal(3, (await check.PlayerMaterials.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task AWrite_RaisesTheRevision()
    {
        string store = await PurseOfAsync(10);
        await using var db = TestDb.Create(store);
        await Gold(db).GrantAsync(Realm, User, 5, "a clear");
        await Gold(db).GrantAsync(Realm, User, 5, "a clear");

        Assert.Equal(2, (await db.PlayerGold.SingleAsync()).Revision);
    }

    // -----------------------------------------------------------------
    // Hardening 3, on a real database
    // -----------------------------------------------------------------

    [PostgresFact]
    public async Task ATradeThatFailsAfterTheSpend_LeavesTheGoldWhereItWas()
    {
        await using var scratch = await ScratchPostgres.CreateAsync();
        await using (var seed = scratch.Context())
        {
            var realm = await seed.AddInstanceAsync();
            seed.PlayerGold.Add(new PlayerGold { GameInstanceId = realm.Id, UserId = User, SettledGold = 100, LastSettledAt = DateTime.UtcNow });
            seed.Users.Add(new ApplicationUser { Id = User, UserName = "Player" });
            await seed.SaveChangesAsync();
        }

        await using (var db = scratch.Context())
        {
            var realmId = (await db.GameInstances.SingleAsync()).Id;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Concurrency.TransactionAsync(db, async () =>
            {
                Assert.True((await Gold(db).SpendAsync(realmId, User, 60, "a trade")).Succeeded);
                throw new InvalidOperationException("the goods could not be moved");
#pragma warning disable CS0162
                return 0;
#pragma warning restore CS0162
            }));
        }

        await using var check = scratch.Context();
        Assert.Equal(100, (long)(await check.PlayerGold.SingleAsync()).SettledGold);
    }

    [PostgresFact]
    public async Task TwoSpends_OnPostgres_CannotBothPass()
    {
        await using var scratch = await ScratchPostgres.CreateAsync();
        Guid realmId;
        await using (var seed = scratch.Context())
        {
            realmId = (await seed.AddInstanceAsync()).Id;
            seed.PlayerGold.Add(new PlayerGold { GameInstanceId = realmId, UserId = User, SettledGold = 100, LastSettledAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using var first = scratch.Context();
        await using var second = scratch.Context();
        await second.PlayerGold.SingleAsync();

        Assert.True((await Gold(first).SpendAsync(realmId, User, 80, "first")).Succeeded);
        Assert.Equal(GoldError.InsufficientGold, (await Gold(second).SpendAsync(realmId, User, 80, "second")).Error);

        await using var check = scratch.Context();
        Assert.Equal(20, (long)(await check.PlayerGold.SingleAsync()).SettledGold);
    }
}

/// <summary>Runs only where the local Postgres (Docker) answers; skipped elsewhere.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!ScratchPostgres.Available) Skip = "No local Postgres to run against.";
    }
}

/// <summary>A throwaway database on the local Postgres, made from the model and dropped afterwards.</summary>
public sealed class ScratchPostgres : IAsyncDisposable
{
    private static readonly Lazy<string?> Server = new(FindServer);
    public static bool Available => Server.Value != null;

    private readonly string _connection;
    private readonly string _name;

    private ScratchPostgres(string connection, string name)
    {
        _connection = connection;
        _name = name;
    }

    public static async Task<ScratchPostgres> CreateAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Server.Value!) { Database = $"mltest_{Guid.NewGuid():N}" };
        var scratch = new ScratchPostgres(builder.ConnectionString, builder.Database);
        await using var db = scratch.Context();
        await db.Database.EnsureCreatedAsync();
        return scratch;
    }

    public ApplicationDbContext Context() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(_connection).Options);

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        var admin = new NpgsqlConnectionStringBuilder(_connection) { Database = "postgres" };
        await using var conn = new NpgsqlConnection(admin.ConnectionString);
        await conn.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", conn);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>The API's own connection string (its appsettings.json), if that server answers.</summary>
    private static string? FindServer()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? file = null;
            while (dir != null && file == null)
            {
                var candidate = Path.Combine(dir.FullName, "MuggaLuggaTD_2D.API", "appsettings.json");
                if (File.Exists(candidate)) file = candidate;
                dir = dir.Parent;
            }
            if (file == null) return null;

            string? connection = JsonNode.Parse(File.ReadAllText(file))?["ConnectionStrings"]?["DefaultConnection"]?.GetValue<string>();
            if (connection == null) return null;

            var admin = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Timeout = 2 };
            using var conn = new NpgsqlConnection(admin.ConnectionString);
            conn.Open();
            return admin.ConnectionString;
        }
        catch
        {
            return null;
        }
    }
}
