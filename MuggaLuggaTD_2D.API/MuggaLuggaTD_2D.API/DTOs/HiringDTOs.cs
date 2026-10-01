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

/// <summary>A worker in the player's employ.</summary>
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
    double RatePerHour);

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
