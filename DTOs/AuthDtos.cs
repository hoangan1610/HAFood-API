namespace HAShop.Api.DTOs;

public record RegisterRequest(
    string FullName,
    string Phone,
    string Email,
    string Password,
    string? Avatar = null
);

// DTO trả về sau khi đăng ký
public record RegisterResponse(
    long UserInfoId,
    bool RequireVerification
);
public record VerifyRegistrationOtpRequest(
    string Email,
    string Code
);

public record VerifyOtpResponse(
    bool Verified,
    long? OtpId,
    string Message
);