using Enums;

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
    int RosterCap);

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
