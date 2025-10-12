namespace HAShop.Api.DTOs;


public record UserProfileUpdateRequest(
    string? FullName,
    string? Phone,
    string? Avatar
);

public record UserProfileUpdateResponse(
    bool Success,
    string? Code = null,
    string? Message = null
);