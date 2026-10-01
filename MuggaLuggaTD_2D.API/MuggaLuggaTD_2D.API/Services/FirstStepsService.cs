using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// First Steps (design 12c): the six steps a player takes on a world, and the Rare chest for taking
/// all of them. See <see cref="FirstStepsProgress"/> and <see cref="FirstStepsRules"/>.
///
/// <para>Steps are recorded by the service that did the thing (<see cref="TavernService"/>,
/// <see cref="PartyService"/>, <see cref="WorldPveService"/>, <see cref="WorldGarrisonService"/>),
/// after it has saved. <b>Recording never fails the action it records</b>, like the war log: a hire
/// that went through is not undone because its tick could not be written.</para>
/// </summary>
public class FirstStepsService
{
    private static readonly Random Dice = new();

    private readonly ApplicationDbContext _context;
    private readonly IGameContentProvider _content;
    private readonly ItemLedgerService _items;
    private readonly ISessionLog _sessionLog;

    public FirstStepsService(ApplicationDbContext context, IGameContentProvider content, ItemLedgerService items,
        ISessionLog sessionLog)
    {
        _context = context;
        _content = content;
        _items = items;
        _sessionLog = sessionLog;
    }

    public async Task<FirstStepsProgress?> ReadAsync(Guid gameInstanceId, string userId)
        => await _context.FirstStepsProgress.AsNoTracking()
            .FirstOrDefaultAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId);

    /// <summary>Ticks a step. Idempotent, and never throws.</summary>
    public async Task RecordAsync(Guid gameInstanceId, string userId, string step)
    {
        if (!FirstStepsRules.IsStep(step) || string.IsNullOrEmpty(userId)) return;
        try
        {
            var progress = await _context.FirstStepsProgress
                .FirstOrDefaultAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId);
            if (progress == null)
            {
                progress = new FirstStepsProgress { GameInstanceId = gameInstanceId, UserId = userId };
                _context.FirstStepsProgress.Add(progress);
            }
            else if (progress.DoneSteps.Contains(step))
            {
                return;
            }

            progress.Done = string.Join(',', progress.DoneSteps.Append(step).Distinct());
            progress.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _sessionLog.Log("FIRST-STEP", $"user={userId} realm={gameInstanceId} step={step}");
        }
        catch (Exception ex)
        {
            // A step lost to a race is a step the player takes again; the action itself stands.
            foreach (var entry in _context.ChangeTracker.Entries<FirstStepsProgress>().ToList())
                entry.State = EntityState.Detached;
            _sessionLog.Log("FIRST-STEP", $"user={userId} realm={gameInstanceId} step={step} not recorded: {ex.Message}");
        }
    }

    /// <summary>Opens the chest: all six done, not yet opened. Grants one Rare piece of gear.</summary>
    public async Task<(FirstStepsError Error, ItemSaveData? Item)> OpenChestAsync(Guid gameInstanceId, string userId)
    {
        var progress = await _context.FirstStepsProgress
            .FirstOrDefaultAsync(p => p.GameInstanceId == gameInstanceId && p.UserId == userId);
        if (progress == null || !FirstStepsRules.AllDone(progress.DoneSteps))
            return (FirstStepsError.NotFinished, null);
        if (progress.ChestOpenedAt != null)
            return (FirstStepsError.AlreadyOpened, null);

        var item = FirstStepsRules.RollChest(_content.DroppableItems.ToList(), Dice);
        if (item == null) return (FirstStepsError.NothingToGive, null);

        // Marked opened before the grant, so a second request cannot open it again meanwhile.
        progress.ChestOpenedAt = DateTime.UtcNow;
        progress.UpdatedAt = progress.ChestOpenedAt.Value;
        await _context.SaveChangesAsync();
        await _items.GrantAsync(gameInstanceId, userId, new[] { item }, "first-steps chest");

        _sessionLog.Log("FIRST-STEPS-CHEST", $"user={userId} realm={gameInstanceId} item={item.ItemName} rarity={item.Rarity}");
        return (FirstStepsError.None, item);
    }

    /// <summary>A season reset starts the world over, so everyone's First Steps start over with it.</summary>
    public static async Task ResetRealmAsync(ApplicationDbContext context, Guid realmId)
    {
        var rows = await context.FirstStepsProgress.Where(p => p.GameInstanceId == realmId).ToListAsync();
        context.FirstStepsProgress.RemoveRange(rows);
    }
}

public enum FirstStepsError
{
    None,
    NotFinished,
    AlreadyOpened,
    NothingToGive
}
