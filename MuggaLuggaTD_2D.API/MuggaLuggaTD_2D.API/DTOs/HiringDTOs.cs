using MuggaLuggaTD.Shared.Gameplay;
using MuggaLuggaTD.Shared.World;

namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>A local on the board: who they are, what they cost, what they would gather.</summary>
public record HiringCandidateCard(
    int Slot,
    string Name,
    ResourceTrade Trade,
    WorkerTier Tier,
    List<WorkerTrait> Traits,
    ResourceTrade? SecondTrade,
    BiomeType HomeBiome,
    int Look,
    long Cost,
    /// <summary>Goods an hour before site and trait effects that depend on where they work.</summary>
    double BaseRate);

/// <summary>A worker in the player's employ, with their veterancy (Workers spec).</summary>
public record HiredWorkerDto(
    Guid Id,
    string Name,
    ResourceTrade Trade,
    WorkerTier Tier,
    List<WorkerTrait> Traits,
    ResourceTrade? SecondTrade,
    BiomeType HomeBiome,
    int Look,
    string? SiteId,
    double RatePerHour,
    int Level,
    double HoursWorked,
    /// <summary>Hours of experience the next level needs; null at level 10.</summary>
    double? NextLevelAt,
    List<WorkerPerk> Perks,
    bool Keep,
    /// <summary>"the Steady Axe", from level 5; else null.</summary>
    string? ByName,
    int LifetimeOutput,
    int SeasonsServed,
    /// <summary>Rolls not yet revealed, in the order they were made.</summary>
    List<WorkerRevealDto> Reveals);

/// <summary>One roll to reveal: a perk, a promotion (or none), a Master's bonus perk (or none).</summary>
public record WorkerRevealDto(int Level, WorkerRollKind Kind, WorkerPerk? Perk, PerkGrade? Grade,
    WorkerTier? NewTier, WorkerTrait? NewTrait, ResourceTrade? NewSecondTrade);

/// <summary>A veteran who would go with the player into the next season, and the level they would return at.</summary>
public record WorkerCarryDto(Guid Id, string Name, int Level, int CarriedLevel, bool Keep);

/// <summary>A resource site in the player's land, with room left on it.</summary>
public record HiringSiteDto(string SiteId, string RegionId, ResourceTrade Trade, int Tier, BiomeType Biome, int Slots, int Used);

/// <summary>Everything the Hiring Hall room shows.</summary>
public record HiringHallResponse(
    List<HiringCandidateCard> Candidates,
    List<HiredWorkerDto> Workers,
    List<HiringSiteDto> Sites,
    int Beds,
    int BedsUsed,
    long RefreshCost,
    DateTime NextArrivalAt,
    long Gold);

public record HiringHireRequest(int Slot, string SharedContractVersion);

public record HiringRefreshRequest(string SharedContractVersion);

/// <summary>Sends a worker to a site, or home to the Hall when <see cref="SiteId"/> is null.</summary>
public record HiringAssignRequest(Guid WorkerId, string? SiteId, string SharedContractVersion);

public record HiringKeepRequest(Guid WorkerId, bool Keep, string SharedContractVersion);

public record HiringWorkerRequest(Guid WorkerId, string SharedContractVersion);

public record HiringSeenRequest(string SharedContractVersion);

/// <summary>Debug, Development only.</summary>
public record HiringDebugRequest(double? Hours, Guid? WorkerId, int? Level, bool? ForcePromotion);
