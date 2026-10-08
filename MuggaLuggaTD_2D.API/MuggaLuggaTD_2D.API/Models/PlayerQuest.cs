using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Models;

/// <summary>
/// A quest a player has taken (<c>docs/design/quests.md</c>). The offer is frozen as it was taken, so the
/// realm changing under it (a region lost, a new hour) does not change what it asks or pays.
///
/// <para>Abandoning one deletes the row. A handed-in one is kept until the season's end, which deletes
/// them all: the next season's world is new, and its site ids would otherwise inherit these.</para>
/// </summary>
public class PlayerQuest
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid GameInstanceId { get; set; }

    [ForeignKey(nameof(GameInstanceId))]
    public GameInstance GameInstance { get; set; } = null!;

    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The offer's id (<see cref="QuestOffer.Id"/>). One row per offer: it cannot be taken twice.</summary>
    [Required]
    [MaxLength(200)]
    public string OfferId { get; set; } = string.Empty;

    /// <summary>The <see cref="QuestOffer"/> as taken.</summary>
    [Required]
    public string OfferJson { get; set; } = "{}";

    public int Progress { get; set; }

    public DateTime AcceptedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When its count was reached; null until then, and for a Gather quest until it is handed in.</summary>
    public DateTime? DoneAt { get; set; }

    public DateTime? HandedInAt { get; set; }

    [NotMapped]
    public QuestOffer Offer
    {
        get
        {
            try { return JsonSerializer.Deserialize<QuestOffer>(OfferJson) ?? new QuestOffer(); }
            catch (JsonException) { return new QuestOffer(); }
        }
        set => OfferJson = JsonSerializer.Serialize(value);
    }
}
