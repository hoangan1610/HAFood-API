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

public record LoginRequest(
    string Email,
    string Password,
    Guid DeviceUuid,
    string? DeviceModel,
    string Ip
);

public record LoginResponse(
    bool Success,
    long? UserInfoId,
    string? JwtToken,
    DateTimeOffset? ExpiresAt,
    string? Code,
    string? Message = null
);






public record LogoutResponse(bool Success, 
    string? Code = null, 
    string? Message = null);



public class MeDeviceDto
{
    public long? DevicePk { get; set; }
    public Guid? DeviceUuid { get; set; }
    public string? DeviceModel { get; set; }
    public string? Ip { get; set; }
    public DateTime? FirstSeen { get; set; }   // map trực tiếp từ datetime2
    public DateTime? LastSeen { get; set; }
}

public class MeUserDto
{
    public long UserInfoId { get; set; }
    public string FullName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Phone { get; set; }
    public string? Avatar { get; set; }
    public byte? Status { get; set; }          // để nullable cho an toàn
}

public class MeSessionDto
{
    public Guid Token { get; set; }
    public DateTime IssuedAt { get; set; }     // datetime2
    public DateTime ExpiresAt { get; set; }
}

public class MeResponse
{
    public bool Authenticated { get; set; }
    public MeUserDto? User { get; set; }
    public MeDeviceDto? Device { get; set; }
    public MeSessionDto? Session { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }

    public static MeResponse Fail(string code, string? message = null) =>
        new() { Authenticated = false, Code = code, Message = message };
}

public record PasswordResetRequestDto(
    string Email,
    long? UserInfoId = null,
    Guid? DeviceUuid = null,
    string? DeviceModel = null,
    string? Ip = null
);


public record PasswordResetRequestResult(
    bool Accepted,
    long? OtpId,
    string? Code = null,
    string? Message = null
);


//public record PasswordResetConfirmRequest(
//    long UserInfoId,
//    string Otp,
//    string NewPassword
//);

//public record PasswordResetConfirmResponse(
//    bool Success,
//    string? Code = null,
//    string? Message = null
//);


public record PasswordResetVerifyRequest(string Email, string Otp);
public record PasswordResetVerifyResponse(bool Verified, long? OtpId, string? Message);

// Bước 2: confirm đổi mật khẩu theo otpId (THAY đổi so với bản cũ)
public record PasswordResetConfirmRequest(long OtpId, string NewPassword);

public record PasswordResetConfirmResponse(
    bool Success,
    string? Code = null,
    string? Message = null
);

// Resend OTP dùng chung
public record OtpResendRequest(
    string Email,
    byte Purpose,                   // 1 = register, 2 = forgot
    Guid? DeviceUuid = null,
    string? DeviceModel = null,
    string? Ip = null
          
);

public record OtpResendResponse(
    bool Accepted,
    long? OtpId,
    string? Code = null,
    string? Message = null
);

public record PasswordChangeRequest(
    string OldPassword,
    string NewPassword
);

public record PasswordChangeResponse(
    bool Success,
    string? Code = null,
    string? Message = null
);
public record DevTokenRequest(
    long UserInfoId,
    Guid? DeviceUuid = null,
    string? DeviceModel = null,
    int TtlSeconds = 3600   // mặc định 1 giờ
);

public record DevTokenResponse(
    string Jwt,
    DateTimeOffset ExpiresAt
);