using System.ComponentModel.DataAnnotations;

namespace MuggaLuggaTD_2D.API.DTOs;

public record RegisterRequest(
    [Required][EmailAddress] string Email,
    [Required][MinLength(6)] string Password,
    [Required][MinLength(3)] string Username,
    string? DisplayName = null
);

public record LoginRequest(
    [Required] string Email,
    [Required] string Password
);

public record AuthResponse(
    bool Success,
    string? Token,
    DateTime? Expiration,
    string? UserId,
    string? Username,
    string? DisplayName,
    IEnumerable<string>? Errors
);

public record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required][MinLength(6)] string NewPassword
);

public record UpdateProfileRequest(
    string? DisplayName
);
