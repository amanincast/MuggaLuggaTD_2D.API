using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using MuggaLuggaTD.Shared.Gameplay;
using Enums;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Builds a ready-to-play test realm in one step, for a playtest. <b>Development only</b> -
/// <c>dotnet run -- seed-playtest --user &lt;name or email&gt;</c> (see Program.cs).
///
/// <para>Test realms kept going bad because each one carried whatever the last test left in it.
/// This one is <b>rebuilt, not repaired</b>: the realm of that name the user owns is deleted
/// (everything realm-scoped cascades from it) and made again, so a re-run is always a clean
/// start.</para>
///
/// <para>It goes through the same services the game does - the world is provisioned and the
/// player seated by <see cref="WorldProvisioningService"/>, materials and gold are granted through
/// the wallet - so the realm is one the game could have produced. The save is the one piece written
/// directly, and it is written in the shape the client saves and the validator reconciles: every
/// hired character has a <see cref="HiredCharacter"/> record, and the starters carry their
/// templates' own rolls.</para>
/// </summary>
public class PlaytestSeeder
{
    public const string Command = "seed-playtest";
    public const string DefaultRealmName = "Playtest";

    public const long GoldGranted = 20_000;
    public const int MaterialsEach = 200;
    public const int CrystalsEach = 3;
    public const long StarterLevel = 10;

    /// <summary>The hires: one of each rarity, so awakening can be seen at every stage, and both
    /// Cleric signatures, since those are the newest thing to test.</summary>
    public static readonly IReadOnlyList<SeedHire> Hires = new[]
    {
        new SeedHire("Cleric", "cleric_mend", AffinityTypes.Light, CharacterRarity.Rare, 20, "Sister Maren"),
        new SeedHire("Cleric", "cleric_sanctuary", AffinityTypes.Water, CharacterRarity.Epic, 30, "Brother Aldous"),
        new SeedHire("Mage", "mage_blast", AffinityTypes.Fire, CharacterRarity.Legendary, 30, "Ignatia"),
        new SeedHire("Warrior", "warrior_slam", AffinityTypes.Earth, CharacterRarity.Common, 10, "Borak"),
    };

    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly WorldProvisioningService _provisioning;
    private readonly SeasonScoreService _seasons;
    private readonly MaterialWalletService _wallet;
    private readonly GoldService _gold;
    private readonly ILogger<PlaytestSeeder> _logger;

    public PlaytestSeeder(
        ApplicationDbContext context,
        IGameContentProvider content,
        WorldProvisioningService provisioning,
        SeasonScoreService seasons,
        MaterialWalletService wallet,
        GoldService gold,
        ILogger<PlaytestSeeder> logger)
    {
        _context = context;
        _content = content;
        _provisioning = provisioning;
        _seasons = seasons;
        _wallet = wallet;
        _gold = gold;
        _logger = logger;
    }

    /// <summary>Runs the command line form. Returns the process exit code.</summary>
    public static async Task<int> RunFromCommandLineAsync(IServiceProvider services, string[] args)
    {
        string? user = ArgAfter(args, "--user");
        string realm = ArgAfter(args, "--name") ?? DefaultRealmName;

        if (string.IsNullOrWhiteSpace(user))
        {
            Console.Error.WriteLine($"usage: dotnet run -- {Command} --user <username or email> [--name <realm>]");
            return 2;
        }

        using var scope = services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<PlaytestSeeder>();

        try
        {
            var result = await seeder.SeedAsync(user, realm);
            Console.WriteLine(result.Describe());
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public async Task<SeedResult> SeedAsync(string userNameOrEmail, string realmName)
    {
        var normalized = userNameOrEmail.ToUpperInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u =>
                       u.NormalizedUserName == normalized || u.NormalizedEmail == normalized)
                   ?? throw new InvalidOperationException($"No user '{userNameOrEmail}'.");

        // Rebuilt, not repaired. Everything realm-scoped cascades from the instance.
        var stale = await _context.GameInstances
            .Where(g => g.OwnerId == user.Id && g.Name == realmName)
            .ToListAsync();
        _context.GameInstances.RemoveRange(stale);
        await _context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var instance = new GameInstance
        {
            Name = realmName,
            OwnerId = user.Id,
            AccessType = GameInstanceAccessType.InviteOnly,
            Capacity = 10,
            SeasonLengthDays = 30,
            SeasonStartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        _context.GameInstances.Add(instance);
        await _context.SaveChangesAsync();

        await _provisioning.EnsureWorldAsync(instance.Id);
        await _provisioning.EnsureSeatAsync(instance.Id, user.Id, user.UserName);

        var save = new UserSaveData { Username = user.UserName, LastSaveTime = now };
        save.Characters.AddRange(BuildStarters());

        foreach (var hire in Hires)
        {
            var (character, record) = BuildHire(hire, instance.Id, user.Id);
            save.Characters.Add(character);
            _context.HiredCharacters.Add(record);
        }

        // March out with the hires: they are what a playtest usually wants to see.
        save.ActiveCharacterIds.AddRange(save.Characters
            .Skip(save.Characters.Count - Hires.Count).Take(4).Select(c => c.Id));

        _context.PlayerGameData.Add(new PlayerGameData
        {
            GameInstanceId = instance.Id,
            UserId = user.Id,
            GameData = JsonSerializer.Serialize(save),
            CreatedAt = now,
            UpdatedAt = now
        });
        await _context.SaveChangesAsync();

        // After the save row exists: settling finds a realm's players through it, and it is what
        // gives the capital its scoreboard row and gold rate from the first minute.
        await _seasons.SettleAllAsync(instance.Id);

        var grants = _content.Materials
            .Where(m => !string.IsNullOrEmpty(m.MaterialName))
            .Select(m => new MaterialGrant
            {
                MaterialName = m.MaterialName,
                Quantity = IsCrystal(m.MaterialName) ? CrystalsEach : MaterialsEach
            })
            .ToList();
        await _wallet.GrantAsync(instance.Id, user.Id, grants, "playtest-seed");
        await _gold.GrantAsync(instance.Id, user.Id, GoldGranted, "playtest-seed");

        _logger.LogInformation("Seeded playtest realm {Realm} ({Id}) for {User}", realmName, instance.Id, user.UserName);

        return new SeedResult(instance.Id, realmName, user.UserName ?? userNameOrEmail, stale.Count,
            save.Characters.Select(c => $"{c.CharacterName} - {c.SignatureId} {c.SignatureAffinity} {c.Rarity} lvl {c.Level}").ToList(),
            grants.Count, GoldGranted);
    }

    private IEnumerable<CharacterSaveData> BuildStarters()
    {
        var allies = _content.Documents.TryGetValue("CharacterData", out var doc)
            ? doc["Allies"]?.AsArray()
            : null;

        foreach (var node in allies ?? new System.Text.Json.Nodes.JsonArray())
        {
            var linkName = (string?)node?["LinkName"];
            var sheet = _content.RecruitSheets.FirstOrDefault(s => s.Sheet == linkName);
            if (node == null || !sheet.IsStarter())
                continue;

            yield return NewCharacter(
                Guid.NewGuid().ToString(), (string?)node["TypeName"] ?? linkName!, linkName!, node,
                sheet.SignatureId, sheet.SignatureAffinity, CharacterRarity.Common, StarterLevel);
        }
    }

    private (CharacterSaveData Character, HiredCharacter Record) BuildHire(
        SeedHire hire, Guid instanceId, string userId)
    {
        // A face of the right class: a sheet with no roll of its own, as the Tavern would use.
        var face = _content.RecruitSheets.FirstOrDefault(s => s.Class == hire.Class && !s.IsStarter())
                   ?? throw new InvalidOperationException($"Content has no {hire.Class} face to hire onto.");

        var node = _content.Documents["CharacterData"]["Allies"]!.AsArray()
            .First(a => (string?)a?["LinkName"] == face.Sheet)!;

        var id = Guid.NewGuid().ToString();
        var character = NewCharacter(id, hire.Name, face.Sheet, node,
            hire.SignatureId, hire.Affinity, hire.Rarity, hire.Level);

        var record = new HiredCharacter
        {
            GameInstanceId = instanceId,
            UserId = userId,
            CharacterId = id,
            Name = hire.Name,
            Sheet = face.Sheet,
            CharacterClass = hire.Class,
            SignatureId = hire.SignatureId,
            Affinity = hire.Affinity,
            Rarity = hire.Rarity
        };

        return (character, record);
    }

    /// <summary>
    /// A saved character as the client writes one. Abilities are left empty on purpose: the kit is
    /// derived from class, signature and affinity on load, and health from the class and level.
    /// </summary>
    private static CharacterSaveData NewCharacter(
        string id, string name, string linkName, System.Text.Json.Nodes.JsonNode template,
        string? signatureId, AffinityTypes? affinity, CharacterRarity rarity, long level)
    {
        return new CharacterSaveData
        {
            Id = id,
            TypeName = (string?)template["TypeName"] ?? name,
            CharacterName = name,
            LinkName = linkName,
            PrefabAssetLocation = (string?)template["PrefabAssetLocation"],
            SpriteLibraryAssetLocation = (string?)template["SpriteLibraryAssetLocation"],
            SignatureId = signatureId,
            SignatureAffinity = affinity,
            Rarity = rarity,
            Level = level,
            MovementSpeed = new ModifiablePropertySaveData<float?>
            {
                BaseValue = (float?)template["MovementSpeed"]?["BaseValue"]
            },
            MaxHealth = new ModifiablePropertySaveData<long?>
            {
                BaseValue = (long?)template["MaxHealth"]?["BaseValue"]
            },
            Equipment = new EquipmentSaveData()
        };
    }

    private static bool IsCrystal(string materialName) =>
        materialName.EndsWith(" Crystal", StringComparison.Ordinal);

    private static string? ArgAfter(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

public record SeedHire(string Class, string SignatureId, AffinityTypes Affinity, CharacterRarity Rarity, long Level, string Name);

public record SeedResult(
    Guid RealmId, string RealmName, string User, int RealmsReplaced,
    IReadOnlyList<string> Characters, int MaterialStacks, long Gold)
{
    public string Describe() =>
        $"Seeded '{RealmName}' ({RealmId}) for {User}" +
        (RealmsReplaced > 0 ? $", replacing {RealmsReplaced} old realm(s) of that name" : "") + "\n" +
        $"  characters:\n    {string.Join("\n    ", Characters)}\n" +
        $"  wallet: {MaterialStacks} material stacks, {Gold:N0} gold";
}
