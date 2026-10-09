namespace MuggaLuggaTD_2D.API.DTOs;

/// <summary>One letter. The client words it from the kind and the data (<c>LetterWording</c>).</summary>
public record LetterResponse(
    Guid Id,
    Guid GameInstanceId,
    string Kind,
    DateTime OccurredAt,
    string? RegionId,
    string? SubjectId,
    string? ActorName,
    string? Detail,
    int Count,
    bool Read,
    bool Flagged
);

public record LettersResponse(List<LetterResponse> Letters, int Unread, bool Flagged);

public record LetterSummaryResponse(int Unread, bool Flagged);

/// <summary>Mark these letters read, or every letter in the realm with <c>All</c>.</summary>
public record MarkLettersReadRequest(List<Guid>? Ids, bool All = false);
