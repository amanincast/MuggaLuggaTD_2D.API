namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>
/// One player's line in the running table.
///
/// <para>The breakdown travels with the total on purpose: a player who is behind should be able to
/// see <i>where</i> they are behind, because the three sources are played differently. Holding is
/// earned by taking and keeping ground, clearing by fighting, raiding by contesting - and the last
/// two are the ways back for someone with little territory left.</para>
/// </summary>
public record SeasonStandingEntry(
    int Rank,
    string UserId,
    string DisplayName,
    double TotalPoints,
    double HoldingPoints,
    double ClearingPoints,
    double RaidingPoints,
    /// <summary>What their current holdings are earning per hour, right now.</summary>
    double PointsPerHour,
    int RegionsHeld,
    /// <summary>Seasons this player has won, anywhere: the crown beside their name. Factions have none.</summary>
    int Crowns = 0
);

/// <summary>The table, plus the clock it is being played against.</summary>
public record SeasonStandingsResponse(
    Guid GameInstanceId,
    int SeasonNumber,
    DateTime SeasonStartedAt,
    DateTime SeasonEndsAt,
    int SeasonLengthDays,
    IReadOnlyList<SeasonStandingEntry> Standings
);

/// <summary>
/// Where a player finished a season that has already closed. The record the carry-over will be
/// built on - see <c>docs/design/seasons-and-scoring.md</c> §5.
/// </summary>
public record SeasonResultEntry(
    int SeasonNumber,
    int Rank,
    string UserId,
    string DisplayName,
    double TotalPoints,
    double HoldingPoints,
    double ClearingPoints,
    double RaidingPoints,
    int RegionsHeld,
    DateTime SeasonStartedAt,
    DateTime SeasonEndedAt,
    /// <summary>The realm's name, for a history spanning several of them.</summary>
    string? RealmName,
    /// <summary>Seasons this player has won, anywhere. Factions have none.</summary>
    int Crowns = 0
);

/// <summary>
/// A season that has closed, as its end page shows it (<c>docs/design/season-end.md</c> §5): the final
/// table, players and factions together, and the asking player's own finish and chest.
/// </summary>
public record SeasonEndedResponse(
    Guid GameInstanceId,
    int SeasonNumber,
    DateTime SeasonStartedAt,
    DateTime SeasonEndedAt,
    IReadOnlyList<SeasonResultEntry> FinalTable,
    /// <summary>The asking player's row; null if they had none in that season.</summary>
    SeasonResultEntry? Mine,
    /// <summary>The chest their finish earned ("Rare"), or null for none.</summary>
    string? ChestRarity,
    bool ChestOpened,
    /// <summary>What the chest held, once opened.</summary>
    string? ChestItemName,
    /// <summary>Whether they had already seen this page before this reply.</summary>
    bool Seen
);

/// <summary>A season chest, opened: the piece it held, already granted.</summary>
public record SeasonChestResponse(int SeasonNumber, string Rarity, StateManagement.Models.ItemSaveData Item);

/// <summary>
/// Broadcast when a season closes. Carries the final table, because the moment a realm resets is
/// exactly the moment a player wants to know how it went - and the world they were looking at has
/// just been replaced underneath them.
/// </summary>
public record SeasonEndedNotification(
    Guid GameInstanceId,
    int ClosedSeasonNumber,
    int NewSeasonNumber,
    DateTime ClosedAt,
    IReadOnlyList<SeasonResultEntry> FinalStandings
);
