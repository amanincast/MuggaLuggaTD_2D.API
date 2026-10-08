using MuggaLuggaTD.Shared.Gameplay;
using StateManagement.Models;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>A quest the player has taken: the offer as taken, how far along it is, and whether it can be handed in.</summary>
public record QuestEntry(Guid Id, QuestOffer Offer, int Progress, bool Done, DateTime AcceptedAt);

/// <summary>
/// A player's quests on one world (<c>docs/design/quests.md</c>): the offers standing (the ones not yet
/// taken), the quests taken, and the givers already looked at, which carry no gold "?".
/// </summary>
public record QuestBoardResponse(
    long Hour,
    DateTime TurnsOverAt,
    int Set,
    List<QuestOffer> Offers,
    List<QuestEntry> Active,
    List<string> Seen,
    int MaxActive);

public record QuestAcceptRequest(string OfferId);

/// <summary>The givers whose offers the player has now seen: their villages, or the Hall's board.</summary>
public record QuestSeenRequest(List<string> GiverIds);

/// <summary>What a hand-in paid: the chest's pieces, the gold and the materials, and the board after it.</summary>
public record QuestHandInResponse(
    List<ItemSaveData> Items,
    long Gold,
    long GoldBalance,
    List<MaterialGrant> Materials,
    QuestBoardResponse Board);
