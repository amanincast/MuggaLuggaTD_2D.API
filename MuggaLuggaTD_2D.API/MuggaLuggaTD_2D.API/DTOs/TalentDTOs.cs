using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>Learn one rank of a Trainer talent.</summary>
public record LearnTalentRequest(
    [Required] string CharacterId,
    [Required] string NodeId,
    [Required] string SharedContractVersion);

/// <summary>Unlearn every talent a character holds, for gold.</summary>
public record RespecTalentsRequest(
    [Required] string CharacterId,
    [Required] string SharedContractVersion);

/// <summary>A character's talents after a learn or a respec.</summary>
public record TalentsResponse(
    string CharacterId,
    Dictionary<string, int> Talents,
    int Points,
    int Unspent,
    long RespecCost,
    /// <summary>The purse after a respec paid for itself; null after a learn, which costs no gold.</summary>
    long? GoldBalance);
