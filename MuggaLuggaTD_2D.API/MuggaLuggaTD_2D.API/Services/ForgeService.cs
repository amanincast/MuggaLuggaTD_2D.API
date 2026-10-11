using Enums;
using Items.Models;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum ForgeError { None, NoSuchItem, Listed, Worn, Refused, NotEnough }

/// <summary>What a Forge action did: the piece as it is now (none after a salvage), what it used up, and what it gave.</summary>
public record ForgeResponse(
    ItemSaveData? Item, string? RemovedItemId, List<MaterialGrant> Spent, long GoldSpent,
    List<MaterialGrant> Gained, long GoldBalance, string Message);

public record ForgeOutcome(ForgeError Error, string? Message = null, ForgeResponse? Response = null)
{
    public bool Succeeded => Error == ForgeError.None;
}

/// <summary>
/// The Forge (Forge spec, phase 1): Temper, Ascend and Salvage, done by the server on the ledger's copy of a
/// piece. Each action is one transaction:
/// <list type="number">
/// <item>read the piece from the ledger;</item>
/// <item>check the rule (<see cref="ForgeRules"/>);</item>
/// <item>spend the materials, then the gold (the materials go back if the gold can't be paid);</item>
/// <item>rewrite the piece, or revoke it.</item>
/// </list>
/// The ledger writes the change into the stored save as well, so a hero wearing it is stronger at once
/// everywhere power is priced (Mike: "part of the reason for increasing the power").
///
/// <para>It shares the Bazaar's lock, so a piece can't be forged and listed in the same moment.</para>
/// </summary>
public class ForgeService
{
    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ItemLedgerService _items;
    private readonly MaterialWalletService _wallet;
    private readonly GoldService _gold;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<ForgeService> _logger;

    private static readonly Random Dice = new();

    public ForgeService(ApplicationDbContext context, IGameContentProvider content, ItemLedgerService items,
        MaterialWalletService wallet, GoldService gold, ISessionLog sessionLog, ILogger<ForgeService> logger)
    {
        _context = context;
        _content = content;
        _items = items;
        _wallet = wallet;
        _gold = gold;
        _sessionLog = sessionLog;
        _logger = logger;
    }

    /// <summary>Raises a piece one power tier, up to what the player's best hero could find.</summary>
    public Task<ForgeOutcome> TemperAsync(Guid realm, string userId, string? itemId) =>
        RunAsync(realm, userId, itemId, async item =>
        {
            int level = await HighestHeroLevelAsync(realm, userId);
            if (ForgeRules.CantTemper(item, level) is { } why) return new ForgeOutcome(ForgeError.Refused, why);

            var cost = ForgeRules.TemperCost(item);
            if (await PayAsync(realm, userId, cost, $"forge-temper {item.Id}") is { } unpaid) return unpaid;

            var was = item.PowerTier;
            ForgeRules.Temper(item, Dice, PoolOf(item));
            await _items.ReplaceAsync(realm, userId, item, $"forge temper {was}->{item.PowerTier}");
            await _context.SaveChangesAsync();
            return await DoneAsync(realm, userId, item, null, cost, new List<MaterialGrant>(),
                $"Tempered: {item.ItemName} is tier {(int)item.PowerTier}.");
        });

    /// <summary>Raises a piece one rarity, up to Legendary, eating <paramref name="secondId"/>.</summary>
    public Task<ForgeOutcome> AscendAsync(Guid realm, string userId, string? itemId, string? secondId) =>
        RunAsync(realm, userId, itemId, async item =>
        {
            var second = await _items.FindAsync(realm, userId, secondId);
            if (second == null) return new ForgeOutcome(ForgeError.NoSuchItem, "Choose a second piece of the same slot.");
            if (second.Value.Listed) return new ForgeOutcome(ForgeError.Listed, "The second piece is on the Bazaar.");
            if (await _items.IsEquippedAsync(realm, userId, secondId))
                return new ForgeOutcome(ForgeError.Worn, "The second piece is worn. Take it off first.");
            if (ForgeRules.CantAscend(item, second.Value.Item) is { } why) return new ForgeOutcome(ForgeError.Refused, why);

            var cost = ForgeRules.AscendCost(item);
            if (await PayAsync(realm, userId, cost, $"forge-ascend {item.Id}") is { } unpaid) return unpaid;

            var was = item.Rarity;
            ForgeRules.Ascend(item, Dice, PoolOf(item));
            await _items.ReplaceAsync(realm, userId, item, $"forge ascend {was}->{item.Rarity} ate={secondId}");
            await _items.RevokeAsync(realm, userId, secondId!, $"forge ascend fed {item.Id}");
            await _context.SaveChangesAsync();
            return await DoneAsync(realm, userId, item, secondId, cost, new List<MaterialGrant>(),
                $"Ascended: {item.ItemName} is {item.Rarity}.");
        });

    /// <summary>Breaks a piece down into materials.</summary>
    public Task<ForgeOutcome> SalvageAsync(Guid realm, string userId, string? itemId) =>
        RunAsync(realm, userId, itemId, async item =>
        {
            if (!ForgeRules.IsForgeable(item)) return new ForgeOutcome(ForgeError.Refused, "Only gear can be broken down.");
            if (await _items.IsEquippedAsync(realm, userId, itemId))
                return new ForgeOutcome(ForgeError.Worn, "It is worn. Take it off first.");

            var gained = ForgeRules.RollSalvage(ForgeRules.SalvageTable(item, MinorCrystalOf), Dice);
            await _items.RevokeAsync(realm, userId, item.Id, "forge salvage");
            await _context.SaveChangesAsync();
            await _wallet.GrantAsync(realm, userId, gained, $"forge-salvage {item.Id}");
            return await DoneAsync(realm, userId, null, item.Id, new ForgeCost(), gained,
                $"Broken down: {item.ItemName} gave {string.Join(", ", gained.Select(g => $"{g.Quantity} {g.MaterialName}"))}.");
        });

    // -----------------------------------------------------------------

    /// <summary>The common frame: one transaction under the trade lock, the piece read from the ledger.</summary>
    private Task<ForgeOutcome> RunAsync(Guid realm, string userId, string? itemId, Func<ItemSaveData, Task<ForgeOutcome>> act) =>
        Concurrency.TransactionAsync(_context, async () =>
        {
            await BazaarService.TradeLock.WaitAsync();
            try
            {
                var held = await _items.FindAsync(realm, userId, itemId);
                if (held == null) return new ForgeOutcome(ForgeError.NoSuchItem, "You do not hold that piece.");
                if (held.Value.Listed) return new ForgeOutcome(ForgeError.Listed, "That piece is on the Bazaar. Take it down first.");

                var outcome = await act(held.Value.Item);
                if (outcome.Succeeded) _sessionLog.Log("FORGE", $"realm={realm} user={userId} item={itemId} {outcome.Response?.Message}");
                return outcome;
            }
            finally { BazaarService.TradeLock.Release(); }
        });

    /// <summary>Takes the cost, all or none. Null when paid; the refusal otherwise.</summary>
    private async Task<ForgeOutcome?> PayAsync(Guid realm, string userId, ForgeCost cost, string reason)
    {
        if (cost.Gold > 0 && await _gold.BalanceAsync(realm, userId) < cost.Gold)
            return new ForgeOutcome(ForgeError.NotEnough, $"You need {cost.Gold:N0} gold.");

        if (cost.Materials.Count > 0)
        {
            var spent = await _wallet.SpendAsync(realm, userId, cost.Materials, reason);
            if (!spent.Succeeded)
                return new ForgeOutcome(ForgeError.NotEnough,
                    $"You need {string.Join(" and ", cost.Materials.Select(m => $"{m.Quantity} {m.MaterialName}"))}.");
        }
        if (cost.Gold > 0)
        {
            var paid = await _gold.SpendAsync(realm, userId, cost.Gold, reason);
            if (!paid.Succeeded)
            {
                await _wallet.GrantAsync(realm, userId, cost.Materials, reason + " refund");
                return new ForgeOutcome(ForgeError.NotEnough, paid.Message ?? $"You need {cost.Gold:N0} gold.");
            }
        }
        return null;
    }

    private async Task<ForgeOutcome> DoneAsync(Guid realm, string userId, ItemSaveData? item, string? removed,
        ForgeCost cost, List<MaterialGrant> gained, string message)
    {
        long balance = await _gold.BalanceAsync(realm, userId);
        return new ForgeOutcome(ForgeError.None, message,
            new ForgeResponse(item, removed, cost.Materials, cost.Gold, gained, balance, message));
    }

    /// <summary>The explicit pool of the piece's kind, from content, so a new slot rolls what a drop of it could.</summary>
    private List<ItemExplicitTypes>? PoolOf(IItemData item) =>
        _content.DroppableItems.FirstOrDefault(t => t.ItemName == item.ItemName)?.ExplicitPool;

    /// <summary>The Minor crystal of an affinity, from content.</summary>
    private string? MinorCrystalOf(AffinityTypes affinity) =>
        _content.Materials.FirstOrDefault(m => m.Category == MaterialCategory.AffinityCrystal &&
                                               m.Tier == MaterialTier.Tier1 && m.AffinityType == affinity)?.MaterialName;

    /// <summary>The level of the player's best hero, from their stored save.</summary>
    private async Task<int> HighestHeroLevelAsync(Guid realm, string userId)
    {
        var save = await MarchingArmy.LoadPlayerSaveAsync(_context, _logger, realm, userId);
        var levels = save?.Characters?.Where(c => c != null).Select(c => (int)Math.Max(1, c.Level)).ToList();
        return levels == null || levels.Count == 0 ? 1 : levels.Max();
    }

    // -----------------------------------------------------------------
    // Debug (Development only; Combat Debug → Forge)
    // -----------------------------------------------------------------

    /// <summary>Fills the wallet for trying the Forge: every essence and shard, Minor crystals, and gold.</summary>
    public async Task DebugStockAsync(Guid realm, string userId)
    {
        var grants = _content.Materials
            .Where(m => m.Category == MaterialCategory.Essence || m.Category == MaterialCategory.RarityShard ||
                        (m.Category == MaterialCategory.AffinityCrystal && m.Tier == MaterialTier.Tier1))
            .Select(m => new MaterialGrant
            {
                MaterialName = m.MaterialName,
                Quantity = m.Category == MaterialCategory.Essence ? 20 : m.Category == MaterialCategory.RarityShard ? 5 : 3,
            })
            .ToList();
        await _wallet.GrantAsync(realm, userId, grants, "debug forge stock");
        await _gold.GrantAsync(realm, userId, 5000, "debug forge stock");
    }

    /// <summary>Two Common tier-1 pieces of one kind: one to forge, one to feed it.</summary>
    public async Task<List<ItemSaveData>> DebugPiecesAsync(Guid realm, string userId)
    {
        var template = _content.DroppableItems.FirstOrDefault(t => t.ItemType == ItemTypes.Weapon) ?? _content.DroppableItems.First();
        var pieces = Enumerable.Range(0, 2).Select(_ =>
        {
            var item = new ItemSaveData { Id = Guid.NewGuid().ToString(), ItemName = template.ItemName, ItemType = template.ItemType, ItemCount = 1 };
            Items.Utilities.ItemDropCalculator.ApplyDropProperties(item, 1, template.ImplicitPool, template.ExplicitPool, ItemRarityTypes.Common);
            item.PowerTier = ItemPowerTier.Tier1;
            return item;
        }).ToList();
        await _items.GrantAsync(realm, userId, pieces, "debug forge pieces");
        return pieces;
    }
}
