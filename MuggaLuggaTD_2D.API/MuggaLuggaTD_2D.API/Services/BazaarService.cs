using System.Text.Json;
using Enums;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

public enum BazaarError
{
    None,
    NothingNamed,
    UnknownGoods,
    NotHeld,
    NotEnoughListed,
    PriceChanged,
    CannotAfford,
    NotYours,
    NotActive,
    OwnListing
}

public record BazaarOutcome(BazaarError Error, string? Message = null)
{
    public bool Succeeded => Error == BazaarError.None;
    public static readonly BazaarOutcome Ok = new(BazaarError.None);
}

/// <summary>A material's queue as a buyer sees it: one row however many sellers stand in it.</summary>
public record BazaarMaterialRow(string Name, MaterialCategory Category, MaterialTier Tier, AffinityTypes? Affinity,
    long UnitPrice, int Available, int Listings, int Worlds);

/// <summary>One piece of equipment for sale.</summary>
public record BazaarEquipmentRow(Guid ListingId, ItemSaveData Item, long Price, string FromWorld, DateTime ListedAt);

/// <summary>One of the caller's own listings, with where it stands.</summary>
public record BazaarMyListing(Guid ListingId, ListingKind Kind, string Name, ItemSaveData? Item, int Quantity,
    int Sold, long UnitPrice, long Earned, long Owed, ListingStatus Status, int UnitsAhead, string? SoldIntoWorld,
    DateTime ListedAt);

/// <summary>What a purchase handed over.</summary>
public record BazaarPurchase(BazaarOutcome Outcome, long Spent = 0, int Quantity = 0, ItemSaveData? Item = null);

/// <summary>
/// The Crossroads Bazaar (design 12d): a market open to every world, at prices nobody sets.
///
/// <para><b>Goods leave the seller when listed.</b> Equipment is held in the item ledger's escrow
/// (<see cref="ItemLedgerService.EscrowAsync"/>); materials are taken out of the wallet. So a listed
/// thing cannot also be worn or spent, and pulling a listing back returns exactly what was unsold.</para>
///
/// <para><b>Gold flows between worlds, never within one.</b> The buyer pays from the realm they are in;
/// the seller is paid in the realm the listing came from, less the house's tenth, and collects it
/// there. A sale never needs both players to be in the same world.</para>
///
/// <para><b>A material is one queue</b>: the oldest listing sells first, across every seller and
/// world ("same price, same queue"). A buyer is never sold their own goods.</para>
///
/// <para><b>Trades are serialised.</b> Two buyers reaching for the last of a queue at once must not
/// both get it. The API runs as one process, so one lock is enough and is far simpler than row
/// locking across a multi-listing fill.</para>
/// </summary>
public class BazaarService
{
    private static readonly SemaphoreSlim TradeLock = new(1, 1);

    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ItemLedgerService _items;
    private readonly MaterialWalletService _wallet;
    private readonly GoldService _gold;
    private readonly ISessionLog _sessionLog;

    public BazaarService(ApplicationDbContext context, IGameContentProvider content, ItemLedgerService items,
        MaterialWalletService wallet, GoldService gold, ISessionLog sessionLog)
    {
        _context = context;
        _content = content;
        _items = items;
        _wallet = wallet;
        _gold = gold;
        _sessionLog = sessionLog;
    }

    // -----------------------------------------------------------------
    // Browsing
    // -----------------------------------------------------------------

    /// <summary>Every material queue with something in it that this player could buy.</summary>
    public async Task<List<BazaarMaterialRow>> MaterialsAsync(string buyerId)
    {
        var listings = await _context.MarketplaceListings.AsNoTracking()
            .Where(l => l.Kind == ListingKind.Material && l.Status == ListingStatus.Active && l.SellerId != buyerId)
            .ToListAsync();

        var rows = new List<BazaarMaterialRow>();
        foreach (var queue in listings.GroupBy(l => l.GoodsKey))
        {
            var material = MaterialNamed(queue.Key);
            if (material == null) continue;   // content no longer knows it: nothing to price it by
            int available = queue.Sum(l => l.QuantityLeft);
            if (available <= 0) continue;
            rows.Add(new BazaarMaterialRow(material.MaterialName, material.Category, material.Tier, material.AffinityType,
                BazaarAssay.PriceOf(material), available, queue.Count(), queue.Select(l => l.GameInstanceId).Distinct().Count()));
        }
        return rows.OrderBy(r => r.Category).ThenBy(r => r.Tier).ThenBy(r => r.Name).ToList();
    }

    /// <summary>Equipment for sale from everyone but this player, newest first.</summary>
    public async Task<List<BazaarEquipmentRow>> EquipmentAsync(string buyerId, int limit = 300)
    {
        var listings = await _context.MarketplaceListings.AsNoTracking()
            .Where(l => l.Kind == ListingKind.Equipment && l.Status == ListingStatus.Active && l.SellerId != buyerId)
            .OrderByDescending(l => l.CreatedAt)
            .Take(limit)
            .ToListAsync();

        var worlds = await WorldNamesAsync(listings.Select(l => l.GameInstanceId));
        var rows = new List<BazaarEquipmentRow>();
        foreach (var listing in listings)
        {
            var item = ReadItem(listing.ItemData);
            if (item == null) continue;
            rows.Add(new BazaarEquipmentRow(listing.Id, item, BazaarAssay.PriceOf(item),
                worlds.GetValueOrDefault(listing.GameInstanceId, "a far world"), listing.CreatedAt));
        }
        return rows;
    }

    /// <summary>
    /// This player's listings from this realm: everything still for sale, and anything sold or
    /// pulled back that still has gold waiting. Newest first.
    /// </summary>
    public async Task<List<BazaarMyListing>> MineAsync(Guid realmId, string sellerId)
    {
        var mine = await _context.MarketplaceListings.AsNoTracking()
            .Where(l => l.GameInstanceId == realmId && l.SellerId == sellerId &&
                (l.Status == ListingStatus.Active || l.EarnedGold > l.CollectedGold))
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

        // How many units of each material stand ahead of each of mine in its queue.
        var keys = mine.Where(l => l.Kind == ListingKind.Material && l.Status == ListingStatus.Active)
            .Select(l => l.GoodsKey).Distinct().ToList();
        var queues = await _context.MarketplaceListings.AsNoTracking()
            .Where(l => l.Kind == ListingKind.Material && l.Status == ListingStatus.Active && keys.Contains(l.GoodsKey))
            .ToListAsync();

        var soldInto = await WorldNamesAsync(mine.Where(l => l.BuyerGameInstanceId.HasValue)
            .Select(l => l.BuyerGameInstanceId!.Value));

        return mine.Select(l =>
        {
            int ahead = l.Kind == ListingKind.Material && l.Status == ListingStatus.Active
                ? queues.Where(q => q.GoodsKey == l.GoodsKey && SellsBefore(q, l)).Sum(q => q.QuantityLeft)
                : 0;
            var item = l.Kind == ListingKind.Equipment ? ReadItem(l.ItemData) : null;
            long price = item != null ? BazaarAssay.PriceOf(item) : BazaarAssay.PriceOf(MaterialNamed(l.GoodsKey));
            string? into = l.BuyerGameInstanceId.HasValue ? soldInto.GetValueOrDefault(l.BuyerGameInstanceId.Value) : null;
            return new BazaarMyListing(l.Id, l.Kind, l.GoodsName, item, l.Quantity, l.QuantitySold, price,
                l.EarnedGold, l.GoldOwed, l.Status, ahead, into, l.CreatedAt);
        }).ToList();
    }

    /// <summary>Gold waiting for this seller in this realm.</summary>
    public async Task<long> OwedAsync(Guid realmId, string sellerId) =>
        await _context.MarketplaceListings
            .Where(l => l.GameInstanceId == realmId && l.SellerId == sellerId && l.EarnedGold > l.CollectedGold)
            .SumAsync(l => l.EarnedGold - l.CollectedGold);

    // -----------------------------------------------------------------
    // Selling
    // -----------------------------------------------------------------

    /// <summary>Lists one piece of equipment the seller holds. It leaves their inventory.</summary>
    public Task<(BazaarOutcome Outcome, MarketplaceListing? Listing)> ListEquipmentAsync(
        Guid realmId, string sellerId, string? itemId)
        => Concurrency.TransactionAsync(_context, () => ListEquipmentCoreAsync(realmId, sellerId, itemId));

    private async Task<(BazaarOutcome Outcome, MarketplaceListing? Listing)> ListEquipmentCoreAsync(
        Guid realmId, string sellerId, string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return (new BazaarOutcome(BazaarError.NothingNamed, "No item was named."), null);

        await TradeLock.WaitAsync();
        try
        {
            var listingId = Guid.NewGuid();
            string? json = await _items.EscrowAsync(realmId, sellerId, itemId, listingId);
            var item = ReadItem(json);
            if (json == null || item == null)
                return (new BazaarOutcome(BazaarError.NotHeld, "You do not hold that item, or it is already listed."), null);

            var listing = new MarketplaceListing
            {
                Id = listingId,
                GameInstanceId = realmId,
                SellerId = sellerId,
                Kind = ListingKind.Equipment,
                GoodsKey = item.Id,
                GoodsName = Clip(item.ItemName ?? "Item"),
                ItemData = json,
                Quantity = 1,
            };
            _context.MarketplaceListings.Add(listing);
            await _context.SaveChangesAsync();

            _sessionLog.Log("BAZAAR-LIST", $"user={sellerId} realm={realmId} item={item.Id} price={BazaarAssay.PriceOf(item)}");
            return (BazaarOutcome.Ok, listing);
        }
        finally { TradeLock.Release(); }
    }

    /// <summary>Lists a quantity of a material. It leaves the seller's wallet.</summary>
    public Task<(BazaarOutcome Outcome, MarketplaceListing? Listing)> ListMaterialAsync(
        Guid realmId, string sellerId, string? materialName, int quantity)
        => Concurrency.TransactionAsync(_context, () => ListMaterialCoreAsync(realmId, sellerId, materialName, quantity));

    private async Task<(BazaarOutcome Outcome, MarketplaceListing? Listing)> ListMaterialCoreAsync(
        Guid realmId, string sellerId, string? materialName, int quantity)
    {
        var material = MaterialNamed(materialName);
        if (material == null)
            return (new BazaarOutcome(BazaarError.UnknownGoods, "The Bazaar does not trade in that."), null);
        if (quantity <= 0)
            return (new BazaarOutcome(BazaarError.NothingNamed, "List at least one."), null);

        await TradeLock.WaitAsync();
        try
        {
            var spent = await _wallet.SpendAsync(realmId, sellerId,
                new[] { new MaterialGrant { MaterialName = material.MaterialName, Quantity = quantity } }, "bazaar-list");
            if (!spent.Succeeded)
                return (new BazaarOutcome(BazaarError.NotHeld, spent.Message), null);

            var listing = new MarketplaceListing
            {
                GameInstanceId = realmId,
                SellerId = sellerId,
                Kind = ListingKind.Material,
                GoodsKey = material.MaterialName,
                GoodsName = material.MaterialName,
                Quantity = quantity,
            };
            _context.MarketplaceListings.Add(listing);
            await _context.SaveChangesAsync();

            _sessionLog.Log("BAZAAR-LIST", $"user={sellerId} realm={realmId} material={material.MaterialName} x{quantity}");
            return (BazaarOutcome.Ok, listing);
        }
        finally { TradeLock.Release(); }
    }

    /// <summary>
    /// Takes a listing off the Bazaar: whatever is unsold goes back to the seller, in the realm it
    /// came from. Gold already earned stays to be collected.
    /// </summary>
    public Task<(BazaarOutcome Outcome, ItemSaveData? Returned, int MaterialsReturned)> PullBackAsync(
        Guid realmId, string sellerId, Guid listingId)
        => Concurrency.TransactionAsync(_context, () => PullBackCoreAsync(realmId, sellerId, listingId));

    private async Task<(BazaarOutcome Outcome, ItemSaveData? Returned, int MaterialsReturned)> PullBackCoreAsync(
        Guid realmId, string sellerId, Guid listingId)
    {
        await TradeLock.WaitAsync();
        try
        {
            var listing = await _context.MarketplaceListings.FirstOrDefaultAsync(l => l.Id == listingId);
            if (listing == null || listing.SellerId != sellerId || listing.GameInstanceId != realmId)
                return (new BazaarOutcome(BazaarError.NotYours, "That is not your listing in this world."), null, 0);
            if (listing.Status != ListingStatus.Active)
                return (new BazaarOutcome(BazaarError.NotActive, "That listing is no longer on the Bazaar."), null, 0);

            ItemSaveData? returned = null;
            int left = listing.QuantityLeft;
            if (listing.Kind == ListingKind.Equipment)
            {
                await _items.ReleaseAsync(listing.GameInstanceId, listing.Id);
                returned = ReadItem(listing.ItemData);
            }
            else if (left > 0)
            {
                await _wallet.GrantAsync(listing.GameInstanceId, sellerId,
                    new[] { new MaterialGrant { MaterialName = listing.GoodsKey, Quantity = left } }, "bazaar-pull-back");
            }

            listing.Status = ListingStatus.Cancelled;
            listing.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _sessionLog.Log("BAZAAR-PULL", $"user={sellerId} listing={listing.Id} {listing.GoodsName} returned={left}");
            return (BazaarOutcome.Ok, returned, listing.Kind == ListingKind.Material ? left : 0);
        }
        finally { TradeLock.Release(); }
    }

    /// <summary>Banks every coin this seller's listings from this realm have earned.</summary>
    public Task<(long Collected, long Balance)> CollectAsync(Guid realmId, string sellerId)
        => Concurrency.TransactionAsync(_context, () => CollectCoreAsync(realmId, sellerId));

    private async Task<(long Collected, long Balance)> CollectCoreAsync(Guid realmId, string sellerId)
    {
        await TradeLock.WaitAsync();
        try
        {
            var owing = await _context.MarketplaceListings
                .Where(l => l.GameInstanceId == realmId && l.SellerId == sellerId && l.EarnedGold > l.CollectedGold)
                .ToListAsync();

            long total = owing.Sum(l => l.GoldOwed);
            foreach (var listing in owing)
            {
                listing.CollectedGold = listing.EarnedGold;
                listing.UpdatedAt = DateTime.UtcNow;
            }
            await _context.SaveChangesAsync();

            if (total > 0) await _gold.CreditAsync(realmId, sellerId, total, "bazaar-collect");
            return (total, await _gold.BalanceAsync(realmId, sellerId));
        }
        finally { TradeLock.Release(); }
    }

    // -----------------------------------------------------------------
    // Buying
    // -----------------------------------------------------------------

    /// <summary>
    /// Buys one piece of equipment, paid from the buyer's realm. The item moves into that realm's
    /// ledger under the buyer, so their next save may carry it.
    /// </summary>
    public Task<BazaarPurchase> BuyEquipmentAsync(Guid buyerRealm, string buyerId, Guid listingId, long? quotedPrice)
        => Concurrency.TransactionAsync(_context, () => BuyEquipmentCoreAsync(buyerRealm, buyerId, listingId, quotedPrice));

    private async Task<BazaarPurchase> BuyEquipmentCoreAsync(Guid buyerRealm, string buyerId, Guid listingId, long? quotedPrice)
    {
        await TradeLock.WaitAsync();
        try
        {
            var listing = await _context.MarketplaceListings.FirstOrDefaultAsync(l => l.Id == listingId);
            if (listing == null || listing.Kind != ListingKind.Equipment || listing.Status != ListingStatus.Active)
                return new BazaarPurchase(new BazaarOutcome(BazaarError.NotActive, "That item is no longer for sale."));
            if (listing.SellerId == buyerId)
                return new BazaarPurchase(new BazaarOutcome(BazaarError.OwnListing, "That is your own listing."));

            var item = ReadItem(listing.ItemData);
            if (item == null)
                return new BazaarPurchase(new BazaarOutcome(BazaarError.UnknownGoods, "That item cannot be read."));

            long price = BazaarAssay.PriceOf(item);
            if (quotedPrice.HasValue && quotedPrice.Value != price)
                return new BazaarPurchase(new BazaarOutcome(BazaarError.PriceChanged, $"The Assay now prices it at {price}g."));

            var paid = await _gold.SpendAsync(buyerRealm, buyerId, price, $"bazaar-buy listing={listing.Id}");
            if (!paid.Succeeded)
                return new BazaarPurchase(new BazaarOutcome(BazaarError.CannotAfford, paid.Message));

            string? json = await _items.TransferAsync(listing.GameInstanceId, listing.Id, buyerId, buyerRealm);
            if (json == null)
            {
                // The escrow went missing (its world was wiped mid-trade). Give the gold back.
                await _gold.CreditAsync(buyerRealm, buyerId, price, "bazaar-refund");
                return new BazaarPurchase(new BazaarOutcome(BazaarError.NotActive, "That item is no longer for sale."));
            }

            listing.QuantitySold = 1;
            listing.EarnedGold += BazaarAssay.NetOf(price);
            listing.BuyerId = buyerId;
            listing.BuyerGameInstanceId = buyerRealm;
            listing.Status = ListingStatus.Sold;
            listing.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _sessionLog.Log("BAZAAR-BUY", $"buyer={buyerId} realm={buyerRealm} listing={listing.Id} item={listing.GoodsName} price={price}");
            return new BazaarPurchase(BazaarOutcome.Ok, price, 1, ReadItem(json));
        }
        finally { TradeLock.Release(); }
    }

    /// <summary>
    /// Buys a quantity of a material from its queue, oldest listing first, paid from the buyer's
    /// realm and delivered to that realm's wallet. All of it or none: a buyer is never charged for a
    /// partial fill they did not ask for.
    /// </summary>
    public Task<BazaarPurchase> BuyMaterialAsync(Guid buyerRealm, string buyerId, string? materialName,
        int quantity, long? quotedUnitPrice)
        => Concurrency.TransactionAsync(_context, () => BuyMaterialCoreAsync(buyerRealm, buyerId, materialName, quantity, quotedUnitPrice));

    private async Task<BazaarPurchase> BuyMaterialCoreAsync(Guid buyerRealm, string buyerId, string? materialName,
        int quantity, long? quotedUnitPrice)
    {
        var material = MaterialNamed(materialName);
        if (material == null)
            return new BazaarPurchase(new BazaarOutcome(BazaarError.UnknownGoods, "The Bazaar does not trade in that."));
        if (quantity <= 0)
            return new BazaarPurchase(new BazaarOutcome(BazaarError.NothingNamed, "Buy at least one."));

        long unit = BazaarAssay.PriceOf(material);
        if (quotedUnitPrice.HasValue && quotedUnitPrice.Value != unit)
            return new BazaarPurchase(new BazaarOutcome(BazaarError.PriceChanged, $"The Assay now prices it at {unit}g."));

        await TradeLock.WaitAsync();
        try
        {
            var queue = await _context.MarketplaceListings
                .Where(l => l.Kind == ListingKind.Material && l.Status == ListingStatus.Active &&
                    l.GoodsKey == material.MaterialName && l.SellerId != buyerId)
                .ToListAsync();
            queue = queue.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).ToList();

            int available = queue.Sum(l => l.QuantityLeft);
            if (available < quantity)
                return new BazaarPurchase(new BazaarOutcome(BazaarError.NotEnoughListed,
                    $"Only {available} {material.MaterialName} are listed."));

            long total = unit * quantity;
            var paid = await _gold.SpendAsync(buyerRealm, buyerId, total, $"bazaar-buy {material.MaterialName}x{quantity}");
            if (!paid.Succeeded)
                return new BazaarPurchase(new BazaarOutcome(BazaarError.CannotAfford, paid.Message));

            int remaining = quantity;
            foreach (var listing in queue)
            {
                if (remaining == 0) break;
                int take = Math.Min(remaining, listing.QuantityLeft);
                if (take <= 0) continue;

                listing.QuantitySold += take;
                listing.EarnedGold += BazaarAssay.NetOf(unit * take);
                if (listing.QuantityLeft == 0) listing.Status = ListingStatus.Sold;
                listing.BuyerGameInstanceId = buyerRealm;
                listing.UpdatedAt = DateTime.UtcNow;
                remaining -= take;
            }
            await _context.SaveChangesAsync();

            await _wallet.GrantAsync(buyerRealm, buyerId,
                new[] { new MaterialGrant { MaterialName = material.MaterialName, Quantity = quantity } }, "bazaar-buy");

            _sessionLog.Log("BAZAAR-BUY", $"buyer={buyerId} realm={buyerRealm} material={material.MaterialName} x{quantity} price={total}");
            return new BazaarPurchase(BazaarOutcome.Ok, total, quantity);
        }
        finally { TradeLock.Release(); }
    }

    // -----------------------------------------------------------------
    // A world resets
    // -----------------------------------------------------------------

    /// <summary>
    /// A world reset destroys the goods it had on the Bazaar (Mike, 2026-10-01: "destroyed for now";
    /// resets are to be revisited after feedback). Unsold equipment leaves the ledger and unsold
    /// materials are simply gone. <b>Gold already earned is paid</b>: the purse survives a reset, and
    /// a sale that happened is not the same thing as goods left on a stall.
    /// </summary>
    public static async Task ExpireRealmAsync(ApplicationDbContext context, GoldService gold, ISessionLog log, Guid realmId)
    {
        await TradeLock.WaitAsync();
        try
        {
            var listings = await context.MarketplaceListings
                .Where(l => l.GameInstanceId == realmId &&
                    (l.Status == ListingStatus.Active || l.EarnedGold > l.CollectedGold))
                .ToListAsync();
            if (listings.Count == 0) return;

            var escrowed = listings.Where(l => l.Kind == ListingKind.Equipment && l.Status == ListingStatus.Active)
                .Select(l => (Guid?)l.Id).ToList();
            if (escrowed.Count > 0)
                context.ItemGrants.RemoveRange(context.ItemGrants.Where(g => escrowed.Contains(g.ListingId)));

            var owed = listings.Where(l => l.GoldOwed > 0).GroupBy(l => l.SellerId)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.GoldOwed));

            int destroyed = 0;
            foreach (var listing in listings)
            {
                if (listing.Status == ListingStatus.Active)
                {
                    destroyed += listing.QuantityLeft;
                    listing.Status = ListingStatus.Expired;
                }
                listing.CollectedGold = listing.EarnedGold;
                listing.UpdatedAt = DateTime.UtcNow;
            }
            await context.SaveChangesAsync();

            foreach (var (seller, amount) in owed)
                await gold.CreditAsync(realmId, seller, amount, "bazaar-reset-payout");

            log.Log("BAZAAR-RESET", $"realm={realmId} listings={listings.Count} unitsDestroyed={destroyed} paidOut={owed.Values.Sum()}");
        }
        finally { TradeLock.Release(); }
    }

    // -----------------------------------------------------------------

    /// <summary>A listing that stands ahead of <paramref name="mine"/> in its queue.</summary>
    private static bool SellsBefore(MarketplaceListing other, MarketplaceListing mine) =>
        other.Id != mine.Id && (other.CreatedAt < mine.CreatedAt ||
            (other.CreatedAt == mine.CreatedAt && other.Id.CompareTo(mine.Id) < 0));

    private MaterialTemplate? MaterialNamed(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _content.Materials.FirstOrDefault(m => m.MaterialName == name);

    private async Task<Dictionary<Guid, string>> WorldNamesAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        return await _context.GameInstances.AsNoTracking()
            .Where(g => list.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name);
    }

    public static ItemSaveData? ReadItem(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ItemSaveData>(json); }
        catch (JsonException) { return null; }
    }

    private static string Clip(string s) => s.Length <= 200 ? s : s.Substring(0, 200);
}
