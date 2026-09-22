namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>One line of the war log. The client words it, so "you" can be said to the right person.</summary>
public record WarLogEntryResponse(
    Guid Id,
    DateTime OccurredAt,
    string Kind,
    string? ActorUserId,
    string? ActorName,
    string? SubjectUserId,
    string? SubjectName,
    string? RegionId,
    string? Detail
);

public record WarLogResponse(List<WarLogEntryResponse> Entries);
