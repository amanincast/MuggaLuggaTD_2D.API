using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>What reconciling a save's inventory against the ledger did.</summary>
public record ItemReconciliation(int Kept, int Corrected, int Dropped, IReadOnlyList<string> Details)
{
    public bool Changed => Corrected > 0 || Dropped > 0;
}

/// <summary>
/// The equipment ledger: every item the server hands out is recorded (<see cref="ItemGrant"/>), and
/// every save's inventory is held to the record.
///
/// <para><b>The rule, in one line:</b> the save decides <i>who wears</i> an item; the ledger decides
/// <i>what the item is and whether it exists</i>. An item the player holds a grant for is written back
/// to exactly what was granted (a save cannot raise a stat or a rarity); an item with no grant is
/// dropped (a save cannot invent one). Materials are not here - they are the wallet's
/// (<see cref="MaterialWalletService"/>) and are stripped from saves before this runs.</para>
///
/// <para><b>Grants come from</b> a claimed PvE run, a won ambush, and a marketplace purchase (which
/// moves a grant rather than making one). Nothing else in the game mints equipment: item merging is
/// not wired to anything, and the marketplace takes its items from the ledger, not from the listing
/// request.</para>
///
/// <para><b>Adoption.</b> Items held before the ledger existed have no grant. The first time the
/// ledger meets a player it takes in whatever their <i>stored</i> save holds - the one the server
/// last accepted, never the one arriving with the request - and writes an <see cref="ItemLedgerState"/>
/// so it happens once. Adopting from the incoming save would let the first post-ledger save launder
/// anything into a grant.</para>
/// </summary>
public class ItemLedgerService
{
    private const int MaterialItemType = (int)Enums.ItemTypes.Material;
    private const string EquippedField = "EquippedByCharacterId";

    private readonly ApplicationDbContext _context;
    private readonly ISessionLog _sessionLog;
    private readonly ILogger<ItemLedgerService> _logger;

    public ItemLedgerService(ApplicationDbContext context, ISessionLog sessionLog, ILogger<ItemLedgerService> logger)
    {
        _context = context;
        _sessionLog = sessionLog;
        _logger = logger;
    }

    // -----------------------------------------------------------------
    // Granting
    // -----------------------------------------------------------------

    /// <summary>Records items the server just rolled for this player. Call before the response goes out.</summary>
    public async Task GrantAsync(Guid gameInstanceId, string userId, IEnumerable<ItemSaveData>? items, string source)
    {
        var list = items?.Where(i => i != null && !string.IsNullOrEmpty(i.Id) && (int)i.ItemType != MaterialItemType).ToList();
        if (list == null || list.Count == 0) return;

        // Adopt first, so a player's first claim after the ledger arrives does not leave their older
        // items looking unearned at the next save.
        await EnsureAdoptedAsync(gameInstanceId, userId);

        foreach (var item in list)
        {
            _context.ItemGrants.Add(new ItemGrant
            {
                GameInstanceId = gameInstanceId,
                UserId = userId,
                ItemId = item.Id,
                ItemJson = Canonical(item),
                Source = Trim(source),
                GrantedAt = DateTime.UtcNow,
            });
        }
        await _context.SaveChangesAsync();
        _sessionLog.Log("ITEM-GRANT", $"user={userId} count={list.Count} source={source}");
    }

    /// <summary>
    /// Takes in what the player's stored save already holds, once. A player with no stored save
    /// (new) adopts nothing and is under the ledger from their first item.
    /// </summary>
    public async Task EnsureAdoptedAsync(Guid gameInstanceId, string userId)
    {
        if (await _context.ItemLedgerStates.AnyAsync(s => s.GameInstanceId == gameInstanceId && s.UserId == userId))
            return;

        var stored = await _context.PlayerGameData
            .Where(p => p.GameInstanceId == gameInstanceId && p.UserId == userId)
            .Select(p => p.GameData)
            .FirstOrDefaultAsync();

        var known = await _context.ItemGrants
            .Where(g => g.GameInstanceId == gameInstanceId)
            .Select(g => g.ItemId)
            .ToListAsync();
        var seen = new HashSet<string>(known);

        int adopted = 0;
        foreach (var item in EquipmentIn(ParseOrNull(stored)))
        {
            string? id = ReadString(item["Id"]);
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;   // no id, or someone's already
            _context.ItemGrants.Add(new ItemGrant
            {
                GameInstanceId = gameInstanceId,
                UserId = userId,
                ItemId = id,
                ItemJson = Canonical(item),
                Source = "adopted",
                GrantedAt = DateTime.UtcNow,
            });
            adopted++;
        }

        _context.ItemLedgerStates.Add(new ItemLedgerState
        {
            GameInstanceId = gameInstanceId,
            UserId = userId,
            AdoptedAt = DateTime.UtcNow,
            AdoptedCount = adopted,
        });
        await _context.SaveChangesAsync();

        if (adopted > 0)
        {
            _sessionLog.Log("ITEM-ADOPT", $"user={userId} adopted={adopted}");
            _logger.LogInformation("Item ledger adopted {Count} item(s) for {User} in {Realm}.", adopted, userId, gameInstanceId);
        }
    }

    // -----------------------------------------------------------------
    // Saves
    // -----------------------------------------------------------------

    /// <summary>Holds an arriving save's inventory to the ledger. Edits <paramref name="save"/> in place.</summary>
    public async Task<ItemReconciliation> ReconcileSaveAsync(Guid gameInstanceId, string userId, JsonNode? save)
    {
        await EnsureAdoptedAsync(gameInstanceId, userId);
        var held = await _context.ItemGrants
            .Where(g => g.GameInstanceId == gameInstanceId && g.UserId == userId && g.ListingId == null)
            .ToListAsync();
        return Reconcile(save, held);
    }

    /// <summary>
    /// The reconciliation itself, with no database: every equipment item in the save either matches
    /// a held grant (and is rewritten to it, keeping only who wears it) or is dropped. An id that
    /// appears twice keeps its first copy. Materials are left alone.
    /// </summary>
    public static ItemReconciliation Reconcile(JsonNode? save, IReadOnlyCollection<ItemGrant> held)
    {
        if (SaveItems(save) is not JsonArray items)
            return new ItemReconciliation(0, 0, 0, Array.Empty<string>());

        var grants = held.GroupBy(g => g.ItemId).ToDictionary(g => g.Key, g => g.First());
        var used = new HashSet<string>();
        var details = new List<string>();
        int kept = 0, corrected = 0, dropped = 0;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is not JsonObject item || IsMaterial(item)) continue;

            string? id = ReadString(item["Id"]);
            if (string.IsNullOrEmpty(id) || !grants.TryGetValue(id, out var grant) || !used.Add(id))
            {
                details.Add($"dropped {ReadString(item["ItemName"]) ?? "?"} ({id ?? "no id"})");
                items.RemoveAt(i--);
                dropped++;
                continue;
            }

            var truth = JsonNode.Parse(grant.ItemJson) as JsonObject ?? new JsonObject();
            var wearer = item[EquippedField]?.DeepClone();
            if (wearer != null) truth[EquippedField] = wearer;

            // Compared in one form: the client writes Newtonsoft (5.0), the ledger System.Text.Json (5).
            if (Canonical(item) == grant.ItemJson)
            {
                kept++;
                continue;
            }

            details.Add($"corrected {ReadString(truth["ItemName"]) ?? "?"} ({id})");
            items[i] = truth;
            corrected++;
        }

        return new ItemReconciliation(kept, corrected, dropped, details);
    }

    // -----------------------------------------------------------------
    // Marketplace
    // -----------------------------------------------------------------

    /// <summary>
    /// Puts a held item up for sale: it leaves the seller's inventory (the next save drops it) and the
    /// listing carries the ledger's copy, not whatever the request said the item was. Null when the
    /// seller holds no such item, or it is already listed.
    /// </summary>
    public async Task<string?> EscrowAsync(Guid gameInstanceId, string sellerId, string? itemId, Guid listingId)
    {
        if (string.IsNullOrEmpty(itemId)) return null;
        await EnsureAdoptedAsync(gameInstanceId, sellerId);
        var grant = await _context.ItemGrants.FirstOrDefaultAsync(g =>
            g.GameInstanceId == gameInstanceId && g.UserId == sellerId && g.ItemId == itemId && g.ListingId == null);
        if (grant == null) return null;

        grant.ListingId = listingId;
        await _context.SaveChangesAsync();
        return grant.ItemJson;
    }

    /// <summary>A cancelled listing gives the item back to its seller.</summary>
    public async Task ReleaseAsync(Guid gameInstanceId, Guid listingId)
    {
        var grant = await _context.ItemGrants.FirstOrDefaultAsync(g => g.GameInstanceId == gameInstanceId && g.ListingId == listingId);
        if (grant == null) return;
        grant.ListingId = null;
        await _context.SaveChangesAsync();
    }

    /// <summary>A sale moves the grant to the buyer, whose next save may then carry the item.</summary>
    public async Task<bool> TransferAsync(Guid gameInstanceId, Guid listingId, string buyerId)
    {
        var grant = await _context.ItemGrants.FirstOrDefaultAsync(g => g.GameInstanceId == gameInstanceId && g.ListingId == listingId);
        if (grant == null) return false;
        await EnsureAdoptedAsync(gameInstanceId, buyerId);

        _sessionLog.Log("ITEM-SOLD", $"item={grant.ItemId} from={grant.UserId} to={buyerId} listing={listingId}");
        grant.UserId = buyerId;
        grant.ListingId = null;
        grant.Source = Trim($"marketplace listing={listingId}");
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>What a player holds, for a client that wants to rebuild its inventory from the truth.</summary>
    public async Task<List<ItemGrant>> HeldAsync(Guid gameInstanceId, string userId) =>
        await _context.ItemGrants
            .Where(g => g.GameInstanceId == gameInstanceId && g.UserId == userId && g.ListingId == null)
            .ToListAsync();

    // -----------------------------------------------------------------
    // JSON
    // -----------------------------------------------------------------

    /// <summary>
    /// The save's inventory. The client writes it as a top-level <c>InventoryItems</c> array
    /// (<c>UserSaveData</c>); <c>ItemInventory.Items</c> is read too, as an older shape.
    /// </summary>
    public static JsonArray? SaveItems(JsonNode? save)
    {
        if (save is not JsonObject root) return null;
        if (root["InventoryItems"] is JsonArray items) return items;
        return root["ItemInventory"] is JsonObject inventory ? inventory["Items"] as JsonArray : null;
    }

    private static IEnumerable<JsonObject> EquipmentIn(JsonNode? save)
    {
        if (SaveItems(save) is not JsonArray items)
            yield break;
        foreach (var node in items)
            if (node is JsonObject item && !IsMaterial(item))
                yield return item;
    }

    /// <summary>The item as the ledger keeps it: everything but who wears it.</summary>
    public static string Canonical(ItemSaveData item) =>
        Without(JsonSerializer.SerializeToNode(item) as JsonObject ?? new JsonObject(), EquippedField).ToJsonString();

    /// <summary>
    /// A saved item in the ledger's one form: read as <c>ItemSaveData</c> and written back, so the
    /// same item compares equal whichever serializer wrote it. Falls back to the raw JSON for an item
    /// that does not read as one.
    /// </summary>
    private static string Canonical(JsonObject item)
    {
        try
        {
            var data = item.Deserialize<ItemSaveData>();
            if (data != null) return Canonical(data);
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return Without(item, EquippedField).ToJsonString();
    }

    private static JsonObject Without(JsonObject item, string field)
    {
        var copy = (JsonObject)item.DeepClone();
        copy.Remove(field);
        return copy;
    }

    private static bool IsMaterial(JsonObject item)
    {
        var type = item["ItemType"];
        return type is JsonValue value && value.TryGetValue<int>(out var t) && t == MaterialItemType;
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static JsonNode? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static string Trim(string? s) => string.IsNullOrEmpty(s) ? "" : s.Length <= 200 ? s : s.Substring(0, 200);
}
