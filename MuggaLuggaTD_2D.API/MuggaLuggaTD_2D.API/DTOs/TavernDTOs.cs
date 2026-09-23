using Enums;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>
/// One card on the Tavern board, as the client draws it.
///
/// <para>The cost travels with the card so the player is shown the price they will be charged,
/// rather than the client computing one from its own copy of the rules and hoping they agree.</para>
/// </summary>
public record TavernRecruitCard(
    int Slot,
    string Name,
    string Sheet,
    string CharacterClass,
    string SignatureId,
    AffinityTypes Affinity,
    CharacterRarity Rarity,
    List<MaterialBalance> Cost,
    bool Hired);

/// <summary>
/// The board, plus what it cannot tell the player itself: how many characters they hold, and how
/// many they may.
/// </summary>
public record TavernBoardResponse(
    List<TavernRecruitCard> Recruits,
    DateTime RolledAt,
    int RosterCount,
    int RosterCap,
    /// <summary>
    /// Every affinity this player has an offer or a debt on. Sent whole rather than as "the standing
    /// one", so the room can show a player what their misses have already bought them on affinities
    /// they are not currently luring.
    /// </summary>
    List<TavernLureState>? Lures = null,
    /// <summary>
    /// What a paid refresh costs right now, and what the player holds. Both travel with the board so
    /// the room shows the price it will actually be charged.
    /// </summary>
    long RefreshCostGold = 0,
    long Gold = 0);

/// <summary>
/// What a player stands to get on one affinity: the crystal currently offered against it, the run of
/// lured boards that have missed it, and the share the next lured board would actually aim for.
///
/// <para><see cref="Target"/> is computed by the server and sent, rather than being recomputed by the
/// client from its own copy of the rules — the player is shown the odds they will actually be given.</para>
/// </summary>
public record TavernLureState(
    AffinityTypes Affinity,
    TavernRules.LureStrength PendingStrength,
    int MissedRestocks,
    double Target);

/// <summary>
/// Offers a crystal against the next restock. Names the affinity and the strength rather than the
/// material, so the server decides which crystal that costs.
/// </summary>
/// <summary>Buys a fresh room. Carries nothing but the contract - the price is the server's.</summary>
public record TavernRefreshRequest(string SharedContractVersion);

public record TavernLureRequest(
    AffinityTypes Affinity,
    TavernRules.LureStrength Strength,
    string SharedContractVersion);

/// <summary>A request to take on the recruit in one slot. It names the slot, never the recruit.</summary>
public record TavernHireRequest(int Slot, string SharedContractVersion);

/// <summary>
/// The character the server just created, with the id it chose. The client adds a level-1 character
/// with exactly these values; anything else it writes is reconciled away on the next save.
/// </summary>
public record HiredCharacterDto(
    string CharacterId,
    string Name,
    string Sheet,
    string CharacterClass,
    string SignatureId,
    AffinityTypes Affinity,
    CharacterRarity Rarity,
    DateTime HiredAt);

/// <summary>A completed hire: the new character, the board it came from, and what is left in the wallet.</summary>
public record TavernHireResponse(
    HiredCharacterDto Character,
    TavernBoardResponse Board,
    List<MaterialBalance> Materials);
