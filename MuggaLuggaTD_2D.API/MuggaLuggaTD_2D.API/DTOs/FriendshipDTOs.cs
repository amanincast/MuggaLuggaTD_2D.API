using System.ComponentModel.DataAnnotations;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.DTOs;

public record SendFriendRequestRequest(
    [Required] string AddresseeId
);

public record FriendRequestResponse(
    Guid Id,
    string RequesterId,
    string? RequesterDisplayName,
    string AddresseeId,
    string? AddresseeDisplayName,
    FriendshipStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record FriendRequestListResponse(
    IEnumerable<FriendRequestResponse> FriendRequests
);

public record FriendResponse(
    Guid FriendshipId,
    string UserId,
    string? DisplayName,
    DateTime FriendsSince
);

public record FriendListResponse(
    IEnumerable<FriendResponse> Friends
);

public record BlockUserRequest(
    [Required] string BlockedUserId
);

public record UserBlockResponse(
    Guid Id,
    string BlockedUserId,
    string? BlockedUserDisplayName,
    DateTime CreatedAt
);

public record UserBlockListResponse(
    IEnumerable<UserBlockResponse> BlockedUsers
);
