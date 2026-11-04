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
    OwnListing,

    /// <summary>The piece is on a character; take it off before selling it.</summary>
    Equipped,

    /// <summary>At the cap of listings or orders from this realm (Marketplace M2).</summary>
    TooMany
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
    DateTime ListedAt, DateTime ExpiresAt, long Deposit);

/// <summary>What other players want of one material: every open order for it, summed (the WANTED tab).</summary>
public record BazaarWantedRow(string Name, MaterialCategory Category, MaterialTier Tier, AffinityTypes? Affinity,
    long UnitPrice, int Units, int Orders);

/// <summary>One of the caller's own buy orders.</summary>
public record BazaarMyOrder(Guid OrderId, string Name, int Quantity, int Filled, long UnitPrice, long HeldGold,
    MarketOrderStatus Status, DateTime CreatedAt, DateTime ExpiresAt);

/// <summary>How much of a material sold in the last <see cref="BazaarAssay.DemandDays"/> days (M1).</summary>
public record BazaarMaterialDemand(string Name, int Sold);

/// <summary>One line of a player's sales log (§2.4): something they sold or bought.</summary>
public record BazaarLogEntry(string Name, ListingKind Kind, int Quantity, long UnitPrice, bool Sold, DateTime At);

/// <summary>How many pieces of one type and rarity sold in the last week (M1).</summary>
public record BazaarGearDemand(ItemTypes Type, ItemRarityTypes Rarity, int Sold);

/// <summary>What a purchase handed over.</summary>
public record BazaarPurchase(BazaarOutcome Outcome, long Spent = 0, int Quantity = 0, ItemSaveData? Item = null);

/// <summary>What listing a material did: the listing, and how many units sold at once into wanted orders.</summary>
public record BazaarListed(BazaarOutcome Outcome, MarketplaceListing? Listing = null, int SoldToOrders = 0)
{
    public void Deconstruct(out BazaarOutcome outcome, out MarketplaceListing? listing)
    {
        outcome = Outcome;
        listing = Listing;
    }
}

/// <summary>What posting an order did: the order, and how many units it bought at once from listings.</summary>
public record BazaarOrdered(BazaarOutcome Outcome, MarketOrder? Order = null, int Bought = 0);

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
/// <para><b>The market's terms</b> (Marketplace spec, Mike 2026-10-10 and 2026-11-04): a listing pays a
/// 2% deposit, back as it sells and kept if it is pulled back or runs out; a listing and an order each
/// stand 72 hours, then go back; a player has at most 100 listings and 20 orders from one realm.</para>
///
/// <para><b>Buy orders</b> hold their gold when posted. A new listing sells into the oldest open orders
/// first; a new order first buys what is already listed. With one price per material, both sides of
/// that are a plain queue, so an open order and a listing of the same material never wait side by side
/// (other than a player's own, which never match).</para>
///
/// <para><b>Trades are serialised.</b> Two buyers reaching for the last of a queue at once must not
/// both get it. The API runs as one process, so one lock is enough and is far simpler than row
/// locking across a multi-listing fill.</para>
/// </summary>
public class BazaarService
{
    /// <summary>One trade at a time; the Forge takes it too, so a piece can't be forged and listed at once.</summary>
    internal static readonly SemaphoreSlim TradeLock = new(1, 1);

    /// <summary>A listing that came back, or an order that closed, stays on MY LISTINGS this long so the player sees why.</summary>
    public static readonly TimeSpan ShownAfterClosing = TimeSpan.FromHours(24);

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

    /// <summary>What everyone else wants: open orders, summed per material (the WANTED tab).</summary>
    public async Task<List<BazaarWantedRow>> WantedAsync(string viewerId)
    {
        var orders = await _context.MarketOrders.AsNoTracking()
            .Where(o => o.Status == MarketOrderStatus.Open && o.BuyerId != viewerId)
            .ToListAsync();

        var rows = new List<BazaarWantedRow>();
        foreach (var group in orders.GroupBy(o => o.MaterialName))
        {
            var material = MaterialNamed(group.Key);
            if (material == null) continue;
            int units = group.Sum(o => o.QuantityLeft);
            if (units <= 0) continue;
            rows.Add(new BazaarWantedRow(material.MaterialName, material.Category, material.Tier, material.AffinityType,
                BazaarAssay.PriceOf(material), units, group.Count()));
        }
        return rows.OrderBy(r => r.Category).ThenBy(r => r.Tier).ThenBy(r => r.Name).ToList();
    }

    /// <summary>What sold over the last week, per material and per gear type and rarity (M1's demand line).</summary>
    public async Task<(List<BazaarMaterialDemand> Materials, List<BazaarGearDemand> Gear)> DemandAsync(DateTime? at = null)
    {
        var since = (at ?? DateTime.UtcNow).AddDays(-BazaarAssay.DemandDays);
        var sales = await _context.MarketSales.AsNoTracking().Where(s => s.SoldAt >= since).ToListAsync();

        var materials = sales.Where(s => s.Kind == ListingKind.Material)
            .GroupBy(s => s.GoodsKey)
            .Select(g => new BazaarMaterialDemand(g.Key, g.Sum(s => s.Quantity)))
            .OrderBy(d => d.Name).ToList();
        var gear = sales.Where(s => s.Kind == ListingKind.Equipment && s.ItemType.HasValue && s.Rarity.HasValue)
            .GroupBy(s => (s.ItemType!.Value, s.Rarity!.Value))
            .Select(g => new BazaarGearDemand((ItemTypes)g.Key.Item1, (ItemRarityTypes)g.Key.Item2, g.Sum(s => s.Quantity)))
            .ToList();
        return (materials, gear);
    }

    /// <summary>
    /// This player's listings from this realm: everything still for sale, anything sold or pulled back
    /// that still has gold waiting, and anything that came back unsold in the last day. Newest first.
    /// </summary>
    public async Task<List<BazaarMyListing>> MineAsync(Guid realmId, string sellerId)
    {
        var shownSince = DateTime.UtcNow - ShownAfterClosing;
        var mine = await _context.MarketplaceListings.AsNoTracking()
            .Where(l => l.GameInstanceId == realmId && l.SellerId == sellerId &&
                (l.Status == ListingStatus.Active || l.EarnedGold > l.CollectedGold ||
                 (l.Status == ListingStatus.Returned && l.ReturnedAt >= shownSince)))
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
                l.EarnedGold, l.GoldOwed, l.Status, ahead, into, l.CreatedAt, l.ExpiresAt, l.Deposit);
        }).ToList();
    }

    /// <summary>This player's buy orders from this realm: the open ones, and any that closed in the last day.</summary>
    public async Task<List<BazaarMyOrder>> MyOrdersAsync(Guid realmId, string buyerId)
    {
        var shownSince = DateTime.UtcNow - ShownAfterClosing;
        var orders = await _context.MarketOrders.AsNoTracking()
            .Where(o => o.GameInstanceId == realmId && o.BuyerId == buyerId &&
                (o.Status == MarketOrderStatus.Open || o.UpdatedAt >= shownSince))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
        return orders.Select(o => new BazaarMyOrder(o.Id, o.MaterialName, o.Quantity, o.QuantityFilled, o.UnitPrice,
            o.HeldGold, o.Status, o.CreatedAt, o.ExpiresAt)).ToList();
    }

    /// <summary>
    /// The last <paramref name="count"/> things this player sold from this realm or bought into it, newest first
    /// (the sales log, §2.4).
    /// </summary>
    public async Task<List<BazaarLogEntry>> SalesLogAsync(Guid realmId, string userId, int count = 20)
    {
        var sales = await _context.MarketSales.AsNoTracking()
            .Where(s => (s.SellerId == userId && s.SellerRealm == realmId) || (s.BuyerId == userId && s.BuyerRealm == realmId))
            .OrderByDescending(s => s.SoldAt)
            .Take(count)
            .ToListAsync();
        return sales.Select(s => new BazaarLogEntry(s.GoodsName, s.Kind, s.Quantity, s.UnitPrice,
            s.SellerId == userId && s.SellerRealm == realmId, s.SoldAt)).ToList();
    }

    /// <summary>How many of this player's listings from this realm are still for sale.</summary>
    public Task<int> ListedCountAsync(Guid realmId, string sellerId) =>
        _context.MarketplaceListings.CountAsync(l =>
            l.GameInstanceId == realmId && l.SellerId == sellerId && l.Status == ListingStatus.Active);

    /// <summary>How many of this player's orders from this realm are still open.</summary>
    public Task<int> OpenOrderCountAsync(Guid realmId, string buyerId) =>
        _context.MarketOrders.CountAsync(o =>
            o.GameInstanceId == realmId && o.BuyerId == buyerId && o.Status == MarketOrderStatus.Open);

    /// <summary>Gold waiting for this seller in this realm.</summary>
    public async Task<long> OwedAsync(Guid realmId, string sellerId) =>
        await _context.MarketplaceListings
            .Where(l => l.GameInstanceId == realmId && l.SellerId == sellerId && l.EarnedGold > l.CollectedGold)
            .SumAsync(l => l.EarnedGold - l.CollectedGold);

    // -----------------------------------------------------------------
    // Selling
    // -----------------------------------------------------------------

    /// <summary>Lists one piece of equipment the seller holds. It leaves their inventory; the deposit leaves their purse.</summary>
    public Task<(BazaarOutcome Outcome, MarketplaceListing? Listing)> ListEquipmentAsync(
        Guid realmId, string sellerId, string? itemId)
        => Concurrency.TransactionAsync(_context, () => ListEquipmentCoreAsync(realmId, sellerId, itemId));

    private async Task<(BazaarOutcome Outcome, MarketplaceListing? Listing)> ListEquipmentCoreAsync(
        Guid realmId, string sellerId, string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return (new BazaarOutcome(BazaarError.NothingNamed, "No item was named."), null);

        // The server's own check (Hardening 5): only the client used to refuse a worn piece.
        if (await _items.IsEquippedAsync(realmId, sellerId, itemId))
            return (new BazaarOutcome(BazaarError.Equipped, "Take that off first: a hero is wearing it."), null);

        await TradeLock.WaitAsync();
        try
        {
            if (await ListedCountAsync(realmId, sellerId) >= BazaarAssay.MaxListings)
                return (TooManyListings, null);

            var listingId = Guid.NewGuid();
            string? json = await _items.EscrowAsync(realmId, sellerId, itemId, listingId);
            var item = ReadItem(json);
            if (json == null || item == null)
                return (new BazaarOutcome(BazaarError.NotHeld, "You do not hold that item, or it is already listed."), null);

            long price = BazaarAssay.PriceOf(item);
            long deposit = BazaarAssay.DepositOn(price);
            var paid = await _gold.SpendAsync(realmId, sellerId, deposit, $"bazaar-deposit item={item.Id}");
            if (!paid.Succeeded)
            {
                await _items.ReleaseAsync(realmId, listingId);
                return (new BazaarOutcome(BazaarError.CannotAfford, $"Listing it takes a {deposit:N0}g deposit, back when it sells."), null);
            }

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
                Deposit = deposit,
                ExpiresAt = DateTime.UtcNow.AddHours(BazaarAssay.ListingHours),
            };
            _context.MarketplaceListings.Add(listing);
            await _context.SaveChangesAsync();

            _sessionLog.Log("BAZAAR-LIST", $"user={sellerId} realm={realmId} item={item.Id} price={price} deposit={deposit}");
            return (BazaarOutcome.Ok, listing);
        }
        finally { TradeLock.Release(); }
    }

    /// <summary>
    /// Lists a quantity of a material. It leaves the seller's wallet, and the deposit their purse. Units
    /// sell at once into any open orders for it, oldest first; the rest wait in the queue.
    /// </summary>
    public Task<BazaarListed> ListMaterialAsync(Guid realmId, string sellerId, string? materialName, int quantity)
        => Concurrency.TransactionAsync(_context, () => ListMaterialCoreAsync(realmId, sellerId, materialName, quantity));

    private async Task<BazaarListed> ListMaterialCoreAsync(Guid realmId, string sellerId, string? materialName, int quantity)
    {
        var material = MaterialNamed(materialName);
        if (material == null)
            return new BazaarListed(new BazaarOutcome(BazaarError.UnknownGoods, "The Bazaar does not trade in that."));
        if (quantity <= 0)
            return new BazaarListed(new BazaarOutcome(BazaarError.NothingNamed, "List at least one."));

        await TradeLock.WaitAsync();
        try
        {
            if (await ListedCountAsync(realmId, sellerId) >= BazaarAssay.MaxListings)
                return new BazaarListed(TooManyListings);

            var grant = new[] { new MaterialGrant { MaterialName = material.MaterialName, Quantity = quantity } };
            var spent = await _wallet.SpendAsync(realmId, sellerId, grant, "bazaar-list");
            if (!spent.Succeeded)
                return new BazaarListed(new BazaarOutcome(BazaarError.NotHeld, spent.Message));

            long unit = BazaarAssay.PriceOf(material);
            long deposit = BazaarAssay.DepositOn(unit * quantity);
            var paid = await _gold.SpendAsync(realmId, sellerId, deposit, $"bazaar-deposit {material.MaterialName}x{quantity}");
            if (!paid.Succeeded)
            {
                await _wallet.GrantAsync(realmId, sellerId, grant, "bazaar-list-undone");
                return new BazaarListed(new BazaarOutcome(BazaarError.CannotAfford,
                    $"Listing those takes a {deposit:N0}g deposit, back as they sell."));
            }

            var now = DateTime.UtcNow;
            var listing = new MarketplaceListing
            {
                GameInstanceId = realmId,
                SellerId = sellerId,
                Kind = ListingKind.Material,
                GoodsKey = material.MaterialName,
                GoodsName = material.MaterialName,
                Quantity = quantity,
                Deposit = deposit,
                CreatedAt = now,
                ExpiresAt = now.AddHours(BazaarAssay.ListingHours),
            };
            _context.MarketplaceListings.Add(listing);

            // Sell into the orders that are waiting, oldest first, at each order's own price.
            var orders = await _context.MarketOrders
                .Where(o => o.Status == MarketOrderStatus.Open && o.MaterialName == material.MaterialName && o.BuyerId != sellerId)
                .ToListAsync();
            int soldToOrders = 0;
            foreach (var order in orders.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id))
            {
                int take = Math.Min(listing.QuantityLeft, order.QuantityLeft);
                if (take <= 0) continue;

                SellUnits(listing, take, order.UnitPrice, order.GameInstanceId, order.BuyerId, now);
                FillOrder(order, take, now);
                await _wallet.GrantAsync(order.GameInstanceId, order.BuyerId,
                    new[] { new MaterialGrant { MaterialName = material.MaterialName, Quantity = take } }, "bazaar-order-fill");
                soldToOrders += take;
                if (listing.QuantityLeft == 0) break;
            }
            await _context.SaveChangesAsync();

            _sessionLog.Log("BAZAAR-LIST",
                $"user={sellerId} realm={realmId} material={material.MaterialName} x{quantity} deposit={deposit} toOrders={soldToOrders}");
            return new BazaarListed(BazaarOutcome.Ok, listing, soldToOrders);
        }
        finally { TradeLock.Release(); }
    }

    /// <summary>
    /// Takes a listing off the Bazaar: whatever is unsold goes back to the seller, in the realm it
    /// came from. Gold already earned stays to be collected; the deposit is kept (row 8).
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

            var (returned, left) = await ReturnUnsoldAsync(listing);
            listing.Status = ListingStatus.Cancelled;
            listing.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _sessionLog.Log("BAZAAR-PULL", $"user={sellerId} listing={listing.Id} {listing.GoodsName} returned={left} depositKept={listing.Deposit - listing.DepositRefunded}");
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

            var now = DateTime.UtcNow;
            SellUnits(listing, 1, price, buyerRealm, buyerId, now, item);
            listing.BuyerId = buyerId;
            await _context.SaveChangesAsync();

            _sessionLog.Log("BAZAAR-BUY", $"buyer={buyerId} realm={buyerRealm} listing={listing.Id} item={listing.GoodsName} price={price}");
            return new BazaarPurchase(BazaarOutcome.Ok, price, 1, ReadItem(json));
        }
        finally { TradeLock.Release(); }
    }

    /// <summary>
    /// Buys a quantity of a material from its queue, oldest listing first, paid from the buyer's
    /// realm and delivered to that realm's wallet. All of it or none: a buyer is never charged for a
    /// partial fill they did not ask for. (A buy order is the way to wait for the rest.)
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
            var queue = await QueueAsync(material.MaterialName, buyerId);

            int available = queue.Sum(l => l.QuantityLeft);
            if (available < quantity)
                return new BazaarPurchase(new BazaarOutcome(BazaarError.NotEnoughListed,
                    $"Only {available} {material.MaterialName} are listed."));

            long total = unit * quantity;
            var paid = await _gold.SpendAsync(buyerRealm, buyerId, total, $"bazaar-buy {material.MaterialName}x{quantity}");
            if (!paid.Succeeded)
                return new BazaarPurchase(new BazaarOutcome(BazaarError.CannotAfford, paid.Message));

            var now = DateTime.UtcNow;
            int remaining = quantity;
            foreach (var listing in queue)
            {
                if (remaining == 0) break;
                int take = Math.Min(remaining, listing.QuantityLeft);
                if (take <= 0) continue;
                SellUnits(listing, take, unit, buyerRealm, buyerId, now);
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
    // Buy orders (Marketplace row 2)
    // -----------------------------------------------------------------

    /// <summary>
    /// Posts a buy order for a material, paid from the buyer's realm: the gold for every unit is held
    /// now, at the assay price. Whatever is already listed is bought at once (oldest first); the rest
    /// waits for sellers, for 72 hours.
    /// </summary>
    public Task<BazaarOrdered> PostOrderAsync(Guid buyerRealm, string buyerId, string? materialName, int quantity)
        => Concurrency.TransactionAsync(_context, () => PostOrderCoreAsync(buyerRealm, buyerId, materialName, quantity));

    private async Task<BazaarOrdered> PostOrderCoreAsync(Guid buyerRealm, string buyerId, string? materialName, int quantity)
    {
        var material = MaterialNamed(materialName);
        if (material == null)
            return new BazaarOrdered(new BazaarOutcome(BazaarError.UnknownGoods, "The Bazaar does not trade in that."));
        if (quantity <= 0 || quantity > BazaarAssay.MaxOrderQuantity)
            return new BazaarOrdered(new BazaarOutcome(BazaarError.NothingNamed,
                $"An order asks for 1 to {BazaarAssay.MaxOrderQuantity}."));

        await TradeLock.WaitAsync();
        try
        {
            if (await OpenOrderCountAsync(buyerRealm, buyerId) >= BazaarAssay.MaxOrders)
                return new BazaarOrdered(new BazaarOutcome(BazaarError.TooMany,
                    $"You already have {BazaarAssay.MaxOrders} orders open from this world. Cancel one first."));

            long unit = BazaarAssay.PriceOf(material);
            long held = unit * quantity;
            var paid = await _gold.SpendAsync(buyerRealm, buyerId, held, $"bazaar-order {material.MaterialName}x{quantity}");
            if (!paid.Succeeded)
                return new BazaarOrdered(new BazaarOutcome(BazaarError.CannotAfford, paid.Message));

            var now = DateTime.UtcNow;
            var order = new MarketOrder
            {
                GameInstanceId = buyerRealm,
                BuyerId = buyerId,
                MaterialName = material.MaterialName,
                Quantity = quantity,
                UnitPrice = unit,
                HeldGold = held,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = now.AddHours(BazaarAssay.ListingHours),
            };
            _context.MarketOrders.Add(order);

            // Buy what is already on the stalls, oldest first, at the listing's price (the same assay).
            int bought = 0;
            foreach (var listing in await QueueAsync(material.MaterialName, buyerId))
            {
                int take = Math.Min(order.QuantityLeft, listing.QuantityLeft);
                if (take <= 0) continue;
                SellUnits(listing, take, unit, buyerRealm, buyerId, now);
                FillOrder(order, take, now);
                bought += take;
                if (order.QuantityLeft == 0) break;
            }
            await _context.SaveChangesAsync();

            if (bought > 0)
                await _wallet.GrantAsync(buyerRealm, buyerId,
                    new[] { new MaterialGrant { MaterialName = material.MaterialName, Quantity = bought } }, "bazaar-order-fill");

            _sessionLog.Log("BAZAAR-ORDER",
                $"buyer={buyerId} realm={buyerRealm} material={material.MaterialName} x{quantity} unit={unit} boughtAtOnce={bought}");
            return new BazaarOrdered(BazaarOutcome.Ok, order, bought);
        }
        finally { TradeLock.Release(); }
    }

    /// <summary>Calls off an open order: the gold still held goes back to the buyer's purse.</summary>
    public Task<(BazaarOutcome Outcome, long Refunded)> CancelOrderAsync(Guid buyerRealm, string buyerId, Guid orderId)
        => Concurrency.TransactionAsync(_context, () => CancelOrderCoreAsync(buyerRealm, buyerId, orderId));

    private async Task<(BazaarOutcome Outcome, long Refunded)> CancelOrderCoreAsync(Guid buyerRealm, string buyerId, Guid orderId)
    {
        await TradeLock.WaitAsync();
        try
        {
            var order = await _context.MarketOrders.FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null || order.BuyerId != buyerId || order.GameInstanceId != buyerRealm)
                return (new BazaarOutcome(BazaarError.NotYours, "That is not your order in this world."), 0);
            if (order.Status != MarketOrderStatus.Open)
                return (new BazaarOutcome(BazaarError.NotActive, "That order is no longer open."), 0);

            long refund = await CloseOrderAsync(order, MarketOrderStatus.Cancelled, "bazaar-order-cancel");
            await _context.SaveChangesAsync();

            _sessionLog.Log("BAZAAR-ORDER-CANCEL", $"buyer={buyerId} order={order.Id} {order.MaterialName} refund={refund}");
            return (BazaarOutcome.Ok, refund);
        }
        finally { TradeLock.Release(); }
    }

    // -----------------------------------------------------------------
    // Time running out (row 5)
    // -----------------------------------------------------------------

    /// <summary>
    /// Sends back everything whose 72 hours are up: an unsold listing's goods to its seller (the deposit
    /// is kept), an order's held gold to its buyer. Called lazily before the board is read, and by the
    /// scheduler's sweep, so nothing waits on a player opening the Bazaar. Returns how many it closed.
    /// </summary>
    public Task<int> SweepAsync(DateTime? at = null)
        => Concurrency.TransactionAsync(_context, () => SweepCoreAsync(at ?? DateTime.UtcNow));

    private async Task<int> SweepCoreAsync(DateTime now)
    {
        // Cheap when nothing is due, which is nearly always: one indexed read each, no lock taken.
        bool listingsDue = await _context.MarketplaceListings.AnyAsync(l => l.Status == ListingStatus.Active && l.ExpiresAt <= now);
        bool ordersDue = await _context.MarketOrders.AnyAsync(o => o.Status == MarketOrderStatus.Open && o.ExpiresAt <= now);
        if (!listingsDue && !ordersDue) return 0;

        await TradeLock.WaitAsync();
        try
        {
            var listings = await _context.MarketplaceListings
                .Where(l => l.Status == ListingStatus.Active && l.ExpiresAt <= now).ToListAsync();
            foreach (var listing in listings)
            {
                var (_, left) = await ReturnUnsoldAsync(listing);
                listing.Status = ListingStatus.Returned;
                listing.ReturnedAt = now;
                listing.UpdatedAt = now;
                _sessionLog.Log("BAZAAR-RETURN", $"user={listing.SellerId} listing={listing.Id} {listing.GoodsName} returned={left}");
            }

            var orders = await _context.MarketOrders
                .Where(o => o.Status == MarketOrderStatus.Open && o.ExpiresAt <= now).ToListAsync();
            foreach (var order in orders)
            {
                long refund = await CloseOrderAsync(order, MarketOrderStatus.Expired, "bazaar-order-expired");
                _sessionLog.Log("BAZAAR-ORDER-EXPIRED", $"buyer={order.BuyerId} order={order.Id} {order.MaterialName} refund={refund}");
            }

            await _context.SaveChangesAsync();
            return listings.Count + orders.Count;
        }
        finally { TradeLock.Release(); }
    }

    // -----------------------------------------------------------------
    // A world resets
    // -----------------------------------------------------------------

    /// <summary>
    /// A world reset destroys the goods it had on the Bazaar (Mike, 2026-10-01: "destroyed for now";
    /// Marketplace row 6: gear is wiped). Unsold equipment leaves the ledger and unsold materials are
    /// simply gone. <b>Gold already earned is paid</b>, and the world's open buy orders give back the
    /// gold they held: the purse survives a reset, and a sale that happened, or gold set aside, is not
    /// the same thing as goods left on a stall.
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
            var orders = await context.MarketOrders
                .Where(o => o.GameInstanceId == realmId && o.Status == MarketOrderStatus.Open)
                .ToListAsync();
            if (listings.Count == 0 && orders.Count == 0) return;

            var escrowed = listings.Where(l => l.Kind == ListingKind.Equipment && l.Status == ListingStatus.Active)
                .Select(l => (Guid?)l.Id).ToList();
            if (escrowed.Count > 0)
                context.ItemGrants.RemoveRange(context.ItemGrants.Where(g => escrowed.Contains(g.ListingId)));

            var owed = listings.Where(l => l.GoldOwed > 0).GroupBy(l => l.SellerId)
                .ToDictionary(g => g.Key, g => g.Sum(l => l.GoldOwed));
            foreach (var order in orders.Where(o => o.HeldGold > 0))
                owed[order.BuyerId] = owed.GetValueOrDefault(order.BuyerId) + order.HeldGold;

            var now = DateTime.UtcNow;
            int destroyed = 0;
            foreach (var listing in listings)
            {
                if (listing.Status == ListingStatus.Active)
                {
                    destroyed += listing.QuantityLeft;
                    listing.Status = ListingStatus.Expired;
                }
                listing.CollectedGold = listing.EarnedGold;
                listing.UpdatedAt = now;
            }
            foreach (var order in orders)
            {
                order.Status = MarketOrderStatus.Expired;
                order.HeldGold = 0;
                order.UpdatedAt = now;
            }
            await context.SaveChangesAsync();

            foreach (var (player, amount) in owed)
                await gold.CreditAsync(realmId, player, amount, "bazaar-reset-payout");

            log.Log("BAZAAR-RESET", $"realm={realmId} listings={listings.Count} orders={orders.Count} unitsDestroyed={destroyed} paidOut={owed.Values.Sum()}");
        }
        finally { TradeLock.Release(); }
    }

    // -----------------------------------------------------------------
    // Debug hooks (Marketplace §8; Development only, Combat Debug → Bazaar)
    // -----------------------------------------------------------------

    /// <summary>The made-up trader whose stall and orders a seeded market holds. A real user row, so listings can name it.</summary>
    public const string DebugTraderId = "debug-bazaar-trader";

    /// <summary>
    /// Fills the market for one tester: a trader lists 25 of three materials and two pieces of gear, and asks for 30 of
    /// three others and of a good, so there is something to buy and something to sell into. Listed and wanted
    /// materials never overlap, since only a new listing or order matches.
    /// </summary>
    public async Task<string> DebugSeedAsync(Guid realmId)
    {
        await TradeLock.WaitAsync();
        try
        {
            if (!await _context.Users.AnyAsync(u => u.Id == DebugTraderId))
            {
                _context.Users.Add(new ApplicationUser
                {
                    Id = DebugTraderId, UserName = "debug-bazaar-trader", NormalizedUserName = "DEBUG-BAZAAR-TRADER",
                    Email = "trader@debug.local", NormalizedEmail = "TRADER@DEBUG.LOCAL", DisplayName = "A travelling trader",
                    SecurityStamp = Guid.NewGuid().ToString(),
                });
                await _context.SaveChangesAsync();
            }

            var now = DateTime.UtcNow;
            var materials = _content.Materials.Where(m => m.Category != MaterialCategory.Goods)
                .OrderBy(m => m.Tier).ThenBy(m => m.Category).ThenBy(m => m.MaterialName).ToList();
            var listed = materials.Take(3).ToList();
            var wanted = materials.Skip(3).Take(3)
                .Concat(_content.Materials.Where(m => m.Category == MaterialCategory.Goods).Take(1)).ToList();

            foreach (var m in listed)
                _context.MarketplaceListings.Add(new MarketplaceListing
                {
                    GameInstanceId = realmId, SellerId = DebugTraderId, Kind = ListingKind.Material, GoodsKey = m.MaterialName,
                    GoodsName = m.MaterialName, Quantity = 25, CreatedAt = now, ExpiresAt = now.AddHours(BazaarAssay.ListingHours),
                });
            foreach (var m in wanted)
            {
                long unit = BazaarAssay.PriceOf(m);
                _context.MarketOrders.Add(new MarketOrder
                {
                    GameInstanceId = realmId, BuyerId = DebugTraderId, MaterialName = m.MaterialName, Quantity = 30, UnitPrice = unit,
                    HeldGold = unit * 30, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddHours(BazaarAssay.ListingHours),
                });
            }
            await _context.SaveChangesAsync();

            int gear = 0;
            var template = _content.DroppableItems.FirstOrDefault(t => t.ItemType == ItemTypes.Weapon) ?? _content.DroppableItems.FirstOrDefault();
            if (template != null)
            {
                foreach (var rarity in new[] { ItemRarityTypes.Common, ItemRarityTypes.Rare })
                {
                    var piece = new ItemSaveData { Id = Guid.NewGuid().ToString(), ItemName = template.ItemName, ItemType = template.ItemType, ItemCount = 1 };
                    Items.Utilities.ItemDropCalculator.ApplyDropProperties(piece, 1, template.ImplicitPool, template.ExplicitPool, rarity);
                    piece.PowerTier = ItemPowerTier.Tier2;
                    await _items.GrantAsync(realmId, DebugTraderId, new[] { piece }, "debug bazaar seed");

                    var listingId = Guid.NewGuid();
                    string? json = await _items.EscrowAsync(realmId, DebugTraderId, piece.Id, listingId);
                    if (json == null) continue;
                    _context.MarketplaceListings.Add(new MarketplaceListing
                    {
                        Id = listingId, GameInstanceId = realmId, SellerId = DebugTraderId, Kind = ListingKind.Equipment,
                        GoodsKey = piece.Id, GoodsName = Clip(piece.ItemName ?? "Item"), ItemData = json, Quantity = 1,
                        CreatedAt = now, ExpiresAt = now.AddHours(BazaarAssay.ListingHours),
                    });
                    gear++;
                }
                await _context.SaveChangesAsync();
            }

            return $"A trader set up: 25 each of {string.Join(", ", listed.Select(m => m.MaterialName))} and {gear} piece(s) of gear " +
                   $"to buy; wants 30 each of {string.Join(", ", wanted.Select(m => m.MaterialName))}.";
        }
        finally { TradeLock.Release(); }
    }

    /// <summary>Runs out the clock on this player's listings and orders from this realm, then sweeps, as 72 hours would.</summary>
    public async Task<string> DebugExpireMineAsync(Guid realmId, string userId)
    {
        var past = DateTime.UtcNow.AddSeconds(-1);
        var listings = await _context.MarketplaceListings
            .Where(l => l.GameInstanceId == realmId && l.SellerId == userId && l.Status == ListingStatus.Active).ToListAsync();
        var orders = await _context.MarketOrders
            .Where(o => o.GameInstanceId == realmId && o.BuyerId == userId && o.Status == MarketOrderStatus.Open).ToListAsync();
        foreach (var l in listings) l.ExpiresAt = past;
        foreach (var o in orders) o.ExpiresAt = past;
        await _context.SaveChangesAsync();
        await SweepAsync();
        return $"{listings.Count} listing(s) and {orders.Count} order(s) ran out and went back.";
    }

    /// <summary>What the server holds for this player from this realm: listed goods, deposits, orders' gold, and what is owed.</summary>
    public async Task<string> DebugEscrowAsync(Guid realmId, string userId)
    {
        var listings = await _context.MarketplaceListings.AsNoTracking()
            .Where(l => l.GameInstanceId == realmId && l.SellerId == userId && l.Status == ListingStatus.Active).ToListAsync();
        var orders = await _context.MarketOrders.AsNoTracking()
            .Where(o => o.GameInstanceId == realmId && o.BuyerId == userId && o.Status == MarketOrderStatus.Open).ToListAsync();
        long owed = await OwedAsync(realmId, userId);
        int gear = listings.Count(l => l.Kind == ListingKind.Equipment);
        int units = listings.Where(l => l.Kind == ListingKind.Material).Sum(l => l.QuantityLeft);
        long deposits = listings.Sum(l => l.Deposit - l.DepositRefunded);
        return $"Listed: {listings.Count} ({units} units, {gear} gear), deposits held {deposits}g. " +
               $"Orders: {orders.Count}, holding {orders.Sum(o => o.HeldGold)}g for {orders.Sum(o => o.QuantityLeft)} units. " +
               $"To collect: {owed}g.";
    }

    // -----------------------------------------------------------------

    private static BazaarOutcome TooManyListings => new(BazaarError.TooMany,
        $"You already have {BazaarAssay.MaxListings} listings on the Bazaar from this world.");

    /// <summary>A material's active listings that <paramref name="buyerId"/> may buy, oldest first.</summary>
    private async Task<List<MarketplaceListing>> QueueAsync(string materialName, string buyerId)
    {
        var queue = await _context.MarketplaceListings
            .Where(l => l.Kind == ListingKind.Material && l.Status == ListingStatus.Active &&
                l.GoodsKey == materialName && l.SellerId != buyerId)
            .ToListAsync();
        return queue.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).ToList();
    }

    /// <summary>
    /// Records <paramref name="take"/> units of a listing sold at <paramref name="unit"/> each: the seller
    /// earns the sale less the house's tenth, plus that share of their deposit back, and the sale is
    /// written down for the demand line.
    /// </summary>
    private void SellUnits(MarketplaceListing listing, int take, long unit, Guid buyerRealm, string buyerId, DateTime now,
        ItemSaveData? item = null)
    {
        int before = listing.QuantitySold;
        listing.QuantitySold += take;
        long back = BazaarAssay.DepositBack(listing.Deposit, listing.Quantity, before, listing.QuantitySold);
        listing.DepositRefunded += back;
        listing.EarnedGold += BazaarAssay.NetOf(unit * take) + back;
        if (listing.QuantityLeft == 0) listing.Status = ListingStatus.Sold;
        listing.BuyerGameInstanceId = buyerRealm;
        listing.UpdatedAt = now;

        _context.MarketSales.Add(new MarketSale
        {
            Kind = listing.Kind,
            GoodsKey = listing.Kind == ListingKind.Material ? listing.GoodsKey : string.Empty,
            ItemType = item != null ? (int)item.ItemType : null,
            Rarity = item != null ? (int)item.Rarity : null,
            Quantity = take,
            UnitPrice = unit,
            SoldAt = now,
            GoodsName = listing.GoodsName,
            SellerId = listing.SellerId,
            SellerRealm = listing.GameInstanceId,
            BuyerId = buyerId,
            BuyerRealm = buyerRealm,
        });
    }

    private static void FillOrder(MarketOrder order, int take, DateTime now)
    {
        order.QuantityFilled += take;
        order.HeldGold = Math.Max(0, order.HeldGold - order.UnitPrice * take);
        if (order.QuantityLeft == 0)
        {
            order.Status = MarketOrderStatus.Filled;
            order.HeldGold = 0;
        }
        order.UpdatedAt = now;
    }

    /// <summary>Closes an order and gives back the gold it still held. Returns the refund.</summary>
    private async Task<long> CloseOrderAsync(MarketOrder order, MarketOrderStatus status, string reason)
    {
        long refund = order.HeldGold;
        order.HeldGold = 0;
        order.Status = status;
        order.UpdatedAt = DateTime.UtcNow;
        if (refund > 0) await _gold.CreditAsync(order.GameInstanceId, order.BuyerId, refund, reason);
        return refund;
    }

    /// <summary>Gives a listing's unsold goods back to its seller in its realm. The caller sets the status.</summary>
    private async Task<(ItemSaveData? Item, int Left)> ReturnUnsoldAsync(MarketplaceListing listing)
    {
        int left = listing.QuantityLeft;
        if (listing.Kind == ListingKind.Equipment)
        {
            await _items.ReleaseAsync(listing.GameInstanceId, listing.Id);
            return (ReadItem(listing.ItemData), left);
        }
        if (left > 0)
            await _wallet.GrantAsync(listing.GameInstanceId, listing.SellerId,
                new[] { new MaterialGrant { MaterialName = listing.GoodsKey, Quantity = left } }, "bazaar-return");
        return (null, left);
    }

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
