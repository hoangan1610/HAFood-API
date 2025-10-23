using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HAShop.Api.Options;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Services;

public interface IAuthService
{
    Task<RegisterResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default);
    Task<VerifyOtpResponse> VerifyRegistrationOtpAsync(VerifyRegistrationOtpRequest req, CancellationToken ct = default);
    Task<LoginResponse> LoginAttemptAsync(LoginRequest req, CancellationToken ct = default);
    //Task<LogoutResponse> LogoutAsync(Guid token, CancellationToken ct = default);
    //Task<MeResponse> GetMeAsync(Guid token, CancellationToken ct = default);
    Task<PasswordResetRequestResult> PasswordResetRequestAsync(PasswordResetRequestDto req, CancellationToken ct = default);
    Task<PasswordResetVerifyResponse> PasswordResetVerifyAsync(PasswordResetVerifyRequest req, CancellationToken ct = default);
    Task<PasswordResetConfirmResponse> PasswordResetConfirmAsync(PasswordResetConfirmRequest req, CancellationToken ct = default);
    Task<OtpResendResponse> OtpResendAsync(OtpResendRequest req, CancellationToken ct = default);
    //Task<PasswordChangeResponse> PasswordChangeAsync(Guid token, PasswordChangeRequest req, CancellationToken ct = default);

    Task<LogoutResponse> LogoutAsync(string jwtToken, CancellationToken ct = default);
    Task<MeResponse> GetMeAsync(string jwtToken, CancellationToken ct = default);
    Task<PasswordChangeResponse> PasswordChangeAsync(string jwtToken, PasswordChangeRequest req, CancellationToken ct = default);

}

public class AuthService : IAuthService
{
    private readonly ISqlConnectionFactory _dbFactory;
    private readonly ILogger<AuthService> _logger;
    private readonly JwtOptions _jwt;

    public AuthService(
        ISqlConnectionFactory dbFactory,
        ILogger<AuthService> logger,
        IOptions<JwtOptions> jwtOptions)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _jwt = jwtOptions.Value;
    }

    public async Task<RegisterResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default)
    {
        using var con = _dbFactory.Create();

        var p = new DynamicParameters();
        p.Add("@full_name", req.FullName, DbType.String);
        p.Add("@phone", req.Phone, DbType.String);
        p.Add("@email", req.Email, DbType.String);
        p.Add("@hashed_password", req.Password, DbType.String);
        p.Add("@avatar", req.Avatar, DbType.String);
        p.Add("@user_info_id", dbType: DbType.Int64, direction: ParameterDirection.Output);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_user_register",
                p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50002)
        {
            throw new InvalidOperationException("EMAIL_EXISTS: Email đã được đăng ký.");
        }
        catch (SqlException ex) when (ex.Number == 50003)
        {
            throw new InvalidOperationException($"USER_REGISTER_FAILED: Không thể đăng ký. {ex.Message}");
        }

        long userInfoId = p.Get<long>("@user_info_id");
        return new RegisterResponse(userInfoId, true);
    }

    public async Task<VerifyOtpResponse> VerifyRegistrationOtpAsync(VerifyRegistrationOtpRequest req, CancellationToken ct = default)
    {
        using var con = _dbFactory.Create();

        const string sqlFind = @"SELECT user_info_id FROM dbo.tbl_login WITH (NOLOCK) WHERE email = @email;";
        long? userInfoId = await con.ExecuteScalarAsync<long?>(
            new CommandDefinition(sqlFind, new { email = req.Email }, cancellationToken: ct));

        if (userInfoId is null)
            return new VerifyOtpResponse(false, null, ToUserMessage("EMAIL_NOT_FOUND", "Email không tồn tại."));

        var p = new DynamicParameters();
        p.Add("@user_info_id", userInfoId.Value, DbType.Int64);
        p.Add("@purpose", (byte)1, DbType.Byte);
        p.Add("@code_hash", req.Code, DbType.String);
        p.Add("@invalidate_on_fail", false, DbType.Boolean);
        p.Add("@verified", dbType: DbType.Boolean, direction: ParameterDirection.Output);
        p.Add("@otp_id", dbType: DbType.Int64, direction: ParameterDirection.Output);
        p.Add("@message", dbType: DbType.String, size: 100, direction: ParameterDirection.Output);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_otp_verify", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException) // 50031 OTP_VERIFY_FAILED
        {
            return new VerifyOtpResponse(false, null, ToUserMessage("OTP_VERIFY_FAILED", "Mã OTP không đúng hoặc đã hết hạn."));
        }

        bool ok = p.Get<bool>("@verified");
        long? otpId = null;
        try { otpId = p.Get<long>("@otp_id"); } catch { }
        string msg = p.Get<string>("@message") ?? string.Empty;

        return new VerifyOtpResponse(ok, otpId, ok ? "Xác minh thành công." : ToUserMessage(null, msg));
    }

    public async Task<LoginResponse> LoginAttemptAsync(LoginRequest req, CancellationToken ct = default)
    {
        using var con = _dbFactory.Create();

        var p = new DynamicParameters();
        p.Add("@email", req.Email, DbType.String);
        p.Add("@hashed_password", Sha256HexUtf16LE(req.Password), DbType.String);
        p.Add("@device_uuid", req.DeviceUuid, DbType.Guid);
        p.Add("@device_model", req.DeviceModel, DbType.String);
        p.Add("@ip", req.Ip, DbType.String);
        p.Add("@session_ttl_seconds", 2_592_000, DbType.Int32);
        p.Add("@auto_issue_verification_otp", true, DbType.Boolean);
        p.Add("@otp_code_plaintext", dbType: DbType.String, value: null);
        p.Add("@otp_ttl_seconds", 900, DbType.Int32);
        p.Add("@send_email", true, DbType.Boolean);
        p.Add("@user_info_id", dbType: DbType.Int64, direction: ParameterDirection.Output);
        p.Add("@token", dbType: DbType.Guid, direction: ParameterDirection.Output);
        p.Add("@expires_at", dbType: DbType.DateTime2, direction: ParameterDirection.Output);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_auth_login_attempt",
                p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex)
        {
            var code = ExtractKnownCode(ex.Message);
            var userMsg = ToUserMessage(code, "Không thể đăng nhập. Vui lòng thử lại.");
            return new LoginResponse(false, null, null, null, code, userMsg);
        }

        var uid = p.Get<long>("@user_info_id");
        var legacyGuidToken = p.Get<Guid>("@token");                 // GUID phiên do SP trả về
        var expiresAtDt = p.Get<DateTime?>("@expires_at");
        var expiresAt = (expiresAtDt is null)
            ? DateTimeOffset.UtcNow.AddDays(30)
            : new DateTimeOffset(DateTime.SpecifyKind(expiresAtDt.Value, DateTimeKind.Utc));

        // 1) Phát JWT (đặt JTI = legacyGuidToken để có "phao" theo JTI)
        var now = DateTimeOffset.UtcNow;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Sub, uid.ToString()),
        new(JwtRegisteredClaimNames.Jti, legacyGuidToken.ToString("D")),    // << đổi: JTI = GUID cũ
        new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        new("uid", uid.ToString()),
        new("did", req.DeviceUuid.ToString())
    };
        if (!string.IsNullOrWhiteSpace(req.DeviceModel))
            claims.Add(new Claim("dmodel", req.DeviceModel!));

        var jwt = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: creds
        );
        var jwtToken = new JwtSecurityTokenHandler().WriteToken(jwt);

        // 2) Ghi đè token trong login_history = JWT
        //    Match bằng token_guid = legacy GUID (an toàn hơn so sánh hash chuỗi)
        const string updSql = @"
WITH last_row AS (
    SELECT TOP (1) id
    FROM dbo.tbl_login_history WITH (UPDLOCK, ROWLOCK)
    WHERE user_info_id = @uid
      AND token_guid   = @oldGuid
    ORDER BY created_at DESC, id DESC
)
UPDATE dbo.tbl_login_history
SET token = @jwt
WHERE id IN (SELECT id FROM last_row);";

        try
        {
            var affected = await con.ExecuteAsync(new CommandDefinition(
                updSql,
                new { uid, oldGuid = legacyGuidToken, jwt = jwtToken },   // truyền GUID trực tiếp
                commandType: CommandType.Text,
                cancellationToken: ct));

            if (affected == 0)
            {
                _logger.LogWarning("Không tìm thấy hàng login_history để cập nhật JWT cho user {UserId} (oldGuid={OldGuid})", uid, legacyGuidToken);
            }
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Lỗi khi cập nhật tbl_login_history.token sang JWT cho user {UserId}", uid);
        }

        // 3) Trả JWT cho client
        return new LoginResponse(
            Success: true,
            UserInfoId: uid,
            JwtToken: jwtToken,
            ExpiresAt: expiresAt,
            Code: null,
            Message: "Đăng nhập thành công."
        );
    }


    //public async Task<LogoutResponse> LogoutAsync(Guid token, CancellationToken ct = default)
    //{
    //    using var con = _dbFactory.Create();

    //    var p = new DynamicParameters();
    //    p.Add("@token", token, DbType.Guid);
    //    p.Add("@rows_affected", dbType: DbType.Int32, direction: ParameterDirection.Output);

    //    try
    //    {
    //        await con.ExecuteAsync(new CommandDefinition(
    //            "dbo.usp_auth_logout",
    //            p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    //    }
    //    catch (SqlException ex) when (ex.Number == 50041)
    //    {
    //        return new LogoutResponse(false, "LOGOUT_NO_ACTIVE_SESSION",
    //            ToUserMessage("LOGOUT_NO_ACTIVE_SESSION", "Phiên đăng nhập không còn hiệu lực."));
    //    }
    //    catch (SqlException ex) when (ex.Number == 50042)
    //    {
    //        return new LogoutResponse(false, "AUTH_LOGOUT_FAILED",
    //            ToUserMessage("AUTH_LOGOUT_FAILED", "Không thể đăng xuất. Vui lòng thử lại."));
    //    }

    //    var affected = p.Get<int>("@rows_affected");
    //    if (affected > 0)
    //        return new LogoutResponse(true, null, "Đã đăng xuất.");

    //    return new LogoutResponse(false, "LOGOUT_NO_ACTIVE_SESSION",
    //        ToUserMessage("LOGOUT_NO_ACTIVE_SESSION", "Phiên đăng nhập không còn hiệu lực."));
    //}

    //public async Task<MeResponse> GetMeAsync(Guid token, CancellationToken ct = default)
    //{
    //    using var con = _dbFactory.Create();

    //    try
    //    {
    //        using var multi = await con.QueryMultipleAsync(
    //            new CommandDefinition("dbo.usp_auth_me",
    //                new { token },
    //                commandType: CommandType.StoredProcedure,
    //                cancellationToken: ct));

    //        var user = await multi.ReadFirstOrDefaultAsync<MeUserDto>();
    //        var device = await multi.ReadFirstOrDefaultAsync<MeDeviceDto>();
    //        var sess = await multi.ReadFirstOrDefaultAsync<MeSessionDto>();

    //        if (user is null || sess is null)
    //        {
    //            return new MeResponse
    //            {
    //                Authenticated = false,
    //                Code = "SESSION_INVALID",
    //                Message = ToUserMessage("SESSION_INVALID", "Phiên đăng nhập không hợp lệ.")
    //            };
    //        }

    //        sess.IssuedAt = DateTime.SpecifyKind(sess.IssuedAt, DateTimeKind.Utc);
    //        sess.ExpiresAt = DateTime.SpecifyKind(sess.ExpiresAt, DateTimeKind.Utc);

    //        return new MeResponse
    //        {
    //            Authenticated = true,
    //            User = user,
    //            Device = device,
    //            Session = sess,
    //            Message = "OK"
    //        };
    //    }
    //    catch (SqlException ex) when (ex.Number == 50015)
    //    {
    //        return new MeResponse { Authenticated = false, Code = "SESSION_NOT_FOUND", Message = ToUserMessage("SESSION_NOT_FOUND", "Không tìm thấy phiên đăng nhập.") };
    //    }
    //    catch (SqlException ex) when (ex.Number == 50016)
    //    {
    //        return new MeResponse { Authenticated = false, Code = "SESSION_INACTIVE", Message = ToUserMessage("SESSION_INACTIVE", "Phiên đăng nhập đã bị vô hiệu.") };
    //    }
    //    catch (SqlException ex) when (ex.Number == 50017)
    //    {
    //        return new MeResponse { Authenticated = false, Code = "SESSION_EXPIRED", Message = ToUserMessage("SESSION_EXPIRED", "Phiên đăng nhập đã hết hạn.") };
    //    }
    //    catch (SqlException ex) when (ex.Number == 50018)
    //    {
    //        return new MeResponse { Authenticated = false, Code = "AUTH_ME_FAILED", Message = ToUserMessage("AUTH_ME_FAILED", "Không thể lấy thông tin phiên.") };
    //    }
    //}

    public async Task<PasswordResetRequestResult> PasswordResetRequestAsync(PasswordResetRequestDto req, CancellationToken ct = default)
    {
        using var con = _dbFactory.Create();

        string otp = GenerateNumericOtp6();

        var p = new DynamicParameters();
        p.Add("@email", req.Email, DbType.String);
        p.Add("@user_info_id", req.UserInfoId, DbType.Int64);
        p.Add("@code_plaintext", otp, DbType.String);
        p.Add("@code_hash", dbType: DbType.String, value: null);
        p.Add("@ttl_seconds", 600, DbType.Int32);
        p.Add("@device_uuid", req.DeviceUuid, DbType.Guid);
        p.Add("@device_model", req.DeviceModel, DbType.String);
        p.Add("@ip", req.Ip, DbType.String);
        p.Add("@otp_id", dbType: DbType.Int64, direction: ParameterDirection.Output);
        p.Add("@blind_mode", false, DbType.Boolean);
        p.Add("@message", dbType: DbType.String, size: 100, direction: ParameterDirection.Output);
        p.Add("@send_email", true, DbType.Boolean);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_password_reset_request",
                p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50071)
        {
            return new PasswordResetRequestResult(false, null, "EMAIL_NOT_FOUND",
                ToUserMessage("EMAIL_NOT_FOUND", "Email không tồn tại trong hệ thống."));
        }
        catch (SqlException ex) when (ex.Number == 50073)
        {
            return new PasswordResetRequestResult(false, null, "PASSWORD_RESET_REQUEST_FAILED",
                ToUserMessage("PASSWORD_RESET_REQUEST_FAILED", "Không thể tạo yêu cầu đặt lại mật khẩu. Vui lòng thử lại sau."));
        }

        long? otpId = null;
        try { otpId = p.Get<long?>("@otp_id"); } catch { }
        return new PasswordResetRequestResult(true, otpId, null, "Đã gửi mã xác thực đến email.");
    }

    public async Task<PasswordResetVerifyResponse> PasswordResetVerifyAsync(PasswordResetVerifyRequest req, CancellationToken ct = default)
    {
        using var con = _dbFactory.Create();

        const string sqlFind = @"SELECT user_info_id FROM dbo.tbl_login WITH (NOLOCK) WHERE email = @email;";
        var uid = await con.ExecuteScalarAsync<long?>(
            new CommandDefinition(sqlFind, new { email = req.Email }, cancellationToken: ct));

        if (uid is null)
            return new PasswordResetVerifyResponse(false, null, "Email không tồn tại.");

        var p = new DynamicParameters();
        p.Add("@user_info_id", uid.Value, DbType.Int64);
        p.Add("@purpose", (byte)2, DbType.Byte);
        p.Add("@code_hash", req.Otp, DbType.String);
        p.Add("@invalidate_on_fail", false, DbType.Boolean);
        p.Add("@verified", dbType: DbType.Boolean, direction: ParameterDirection.Output);
        p.Add("@otp_id", dbType: DbType.Int64, direction: ParameterDirection.Output);
        p.Add("@message", dbType: DbType.String, size: 100, direction: ParameterDirection.Output);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_otp_verify", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException)
        {
            return new PasswordResetVerifyResponse(false, null, "Không xác thực được OTP. Vui lòng thử lại.");
        }

        var ok = p.Get<bool>("@verified");
        long? otpId = null; try { otpId = p.Get<long?>("@otp_id"); } catch { }
        var msg = p.Get<string>("@message") ?? string.Empty;

        return new PasswordResetVerifyResponse(ok, ok ? otpId : null,
            ok ? "OTP hợp lệ." :
            msg is "OTP_EXPIRED" ? "Mã OTP đã hết hạn." :
            msg is "OTP_INVALID" ? "Mã OTP không đúng." :
            msg is "OTP_NOT_FOUND" ? "Không còn mã OTP hợp lệ." :
                                      "Xác thực OTP không thành công.");
    }

    public async Task<PasswordResetConfirmResponse> PasswordResetConfirmAsync(PasswordResetConfirmRequest req, CancellationToken ct = default)
    {
        using var con = _dbFactory.Create();

        var p = new DynamicParameters();
        p.Add("@otp_id", req.OtpId, DbType.Int64);
        p.Add("@new_hashed_password", Sha256HexUtf16LE(req.NewPassword), DbType.String);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_password_reset_confirm",
                p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50081)
        {
            var userMsg =
                ex.Message.Contains("OTP_EXPIRED", StringComparison.OrdinalIgnoreCase) ? "Mã OTP đã hết hạn." :
                ex.Message.Contains("OTP_NOT_VERIFIED", StringComparison.OrdinalIgnoreCase) ? "OTP chưa được xác thực." :
                ex.Message.Contains("INVALID_OTP_ID", StringComparison.OrdinalIgnoreCase) ? "Mã xác thực không hợp lệ." :
                "Không thể xác nhận đổi mật khẩu.";
            return new PasswordResetConfirmResponse(false, "PASSWORD_RESET_INVALID_STATE", userMsg);
        }
        catch (SqlException ex) when (ex.Number == 50082)
        {
            return new PasswordResetConfirmResponse(false, "PASSWORD_RESET_CONFIRM_FAILED",
                "Không thể đặt lại mật khẩu. Vui lòng thử lại.");
        }

        return new PasswordResetConfirmResponse(true, null, "Đổi mật khẩu thành công. Vui lòng đăng nhập lại.");
    }

    public async Task<OtpResendResponse> OtpResendAsync(OtpResendRequest req, CancellationToken ct = default)
    {
        if (req.Purpose is not (1 or 2))
            return new OtpResendResponse(false, null, "INVALID_PURPOSE", "Purpose phải là 1 (đăng ký) hoặc 2 (quên mật khẩu).");

        using var con = _dbFactory.Create();

        string otp = GenerateNumericOtp6();

        var p = new DynamicParameters();
        p.Add("@purpose", req.Purpose, DbType.Byte);
        p.Add("@email", req.Email, DbType.String);
        p.Add("@user_info_id", dbType: DbType.Int64, value: null);
        p.Add("@code_plaintext", otp, DbType.String);
        p.Add("@code_hash", dbType: DbType.String, value: null);
        p.Add("@ttl_seconds", 600, DbType.Int32);
        p.Add("@device_uuid", req.DeviceUuid, DbType.Guid);
        p.Add("@device_model", req.DeviceModel, DbType.String);
        p.Add("@ip", string.IsNullOrWhiteSpace(req.Ip) ? null : req.Ip, DbType.String);
        p.Add("@otp_id", dbType: DbType.Int64, direction: ParameterDirection.Output);
        p.Add("@blind_mode", true, DbType.Boolean);
        p.Add("@message", dbType: DbType.String, size: 100, direction: ParameterDirection.Output);
        p.Add("@send_email", true, DbType.Boolean);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_otp_resend",
                p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50071)
        {
            return new OtpResendResponse(false, null, "EMAIL_NOT_FOUND", "Email không tồn tại.");
        }
        catch (SqlException ex) when (ex.Number == 50074)
        {
            return new OtpResendResponse(false, null, "OTP_RESEND_FAILED", "Không thể gửi lại mã. Vui lòng thử lại.");
        }

        long? otpId = null; try { otpId = p.Get<long?>("@otp_id"); } catch { }
        var msg = p.Get<string>("@message") ?? "REQUEST_ACCEPTED";
        return new OtpResendResponse(true, otpId, null, msg);
    }

    //public async Task<PasswordChangeResponse> PasswordChangeAsync(Guid token, PasswordChangeRequest req, CancellationToken ct = default)
    //{
    //    using var con = _dbFactory.Create();

    //    var oldHash = Sha256HexUtf16LE(req.OldPassword);
    //    var newHash = Sha256HexUtf16LE(req.NewPassword);

    //    var p = new DynamicParameters();
    //    p.Add("@token", token, DbType.Guid);
    //    p.Add("@old_hashed_password", oldHash, DbType.String);
    //    p.Add("@new_hashed_password", newHash, DbType.String);

    //    try
    //    {
    //        await con.ExecuteAsync(new CommandDefinition(
    //            "dbo.usp_password_change_by_token",
    //            p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    //    }
    //    catch (SqlException ex) when (ex.Number == 50015)
    //    {
    //        return new PasswordChangeResponse(false, "SESSION_NOT_FOUND", "Phiên đăng nhập không tồn tại.");
    //    }
    //    catch (SqlException ex) when (ex.Number == 50016)
    //    {
    //        return new PasswordChangeResponse(false, "SESSION_INACTIVE", "Phiên đăng nhập đã bị vô hiệu.");
    //    }
    //    catch (SqlException ex) when (ex.Number == 50017)
    //    {
    //        return new PasswordChangeResponse(false, "SESSION_EXPIRED", "Phiên đăng nhập đã hết hạn.");
    //    }
    //    catch (SqlException ex) when (ex.Number == 50061)
    //    {
    //        return new PasswordChangeResponse(false, "LOGIN_NOT_FOUND", "Không tìm thấy tài khoản.");
    //    }
    //    catch (SqlException ex) when (ex.Number == 50062)
    //    {
    //        return new PasswordChangeResponse(false, "LOGIN_INACTIVE", "Tài khoản đang không hoạt động.");
    //    }
    //    catch (SqlException ex) when (ex.Number == 50063)
    //    {
    //        return new PasswordChangeResponse(false, "OLD_PASSWORD_MISMATCH", "Mật khẩu hiện tại không đúng.");
    //    }
    //    catch (SqlException ex) when (ex.Number == 50064)
    //    {
    //        return new PasswordChangeResponse(false, "PASSWORD_CHANGE_FAILED", "Không thể đổi mật khẩu. Vui lòng thử lại.");
    //    }

    //    return new PasswordChangeResponse(true, null, "Đổi mật khẩu thành công.");
    //}

    public async Task<LogoutResponse> LogoutAsync(string jwtToken, CancellationToken ct = default)
    {
        using var con = _dbFactory.Create();
        var p = new DynamicParameters();
        p.Add("@jwtToken", jwtToken, DbType.String);
        p.Add("@rows_affected", dbType: DbType.Int32, direction: ParameterDirection.Output);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_auth_logout", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50041)
        {
            return new LogoutResponse(false, "LOGOUT_NO_ACTIVE_SESSION",
                ToUserMessage("LOGOUT_NO_ACTIVE_SESSION", "Phiên đăng nhập không còn hiệu lực."));
        }
        catch (SqlException ex) when (ex.Number == 50042)
        {
            return new LogoutResponse(false, "AUTH_LOGOUT_FAILED",
                ToUserMessage("AUTH_LOGOUT_FAILED", "Không thể đăng xuất. Vui lòng thử lại."));
        }

        var affected = p.Get<int>("@rows_affected");
        return affected > 0
            ? new LogoutResponse(true, null, "Đã đăng xuất.")
            : new LogoutResponse(false, "LOGOUT_NO_ACTIVE_SESSION",
                ToUserMessage("LOGOUT_NO_ACTIVE_SESSION", "Phiên đăng nhập không còn hiệu lực."));
    }

    public async Task<MeResponse> GetMeAsync(string jwtToken, CancellationToken ct = default)
    {
        using var con = _dbFactory.Create();
        try
        {
            using var multi = await con.QueryMultipleAsync(new CommandDefinition(
                "dbo.usp_auth_me", new { jwtToken }, commandType: CommandType.StoredProcedure, cancellationToken: ct));

            var user = await multi.ReadFirstOrDefaultAsync<MeUserDto>();
            var device = await multi.ReadFirstOrDefaultAsync<MeDeviceDto>();
            var sess = await multi.ReadFirstOrDefaultAsync<MeSessionDto>();

            if (user is null || sess is null)
            {
                return new MeResponse
                {
                    Authenticated = false,
                    Code = "SESSION_INVALID",
                    Message = ToUserMessage("SESSION_INVALID", "Phiên đăng nhập không hợp lệ.")
                };
            }

            sess.IssuedAt = DateTime.SpecifyKind(sess.IssuedAt, DateTimeKind.Utc);
            sess.ExpiresAt = DateTime.SpecifyKind(sess.ExpiresAt, DateTimeKind.Utc);

            return new MeResponse { Authenticated = true, User = user, Device = device, Session = sess, Message = "OK" };
        }
        catch (SqlException ex) when (ex.Number == 50015)
        { return MeResponse.Fail("SESSION_NOT_FOUND", ToUserMessage("SESSION_NOT_FOUND", "Không tìm thấy phiên đăng nhập.")); }
        catch (SqlException ex) when (ex.Number == 50016)
        { return MeResponse.Fail("SESSION_INACTIVE", ToUserMessage("SESSION_INACTIVE", "Phiên đăng nhập đã bị vô hiệu.")); }
        catch (SqlException ex) when (ex.Number == 50017)
        { return MeResponse.Fail("SESSION_EXPIRED", ToUserMessage("SESSION_EXPIRED", "Phiên đăng nhập đã hết hạn.")); }
        catch (SqlException)
        { return MeResponse.Fail("AUTH_ME_FAILED", ToUserMessage("AUTH_ME_FAILED", "Không thể lấy thông tin phiên.")); }
    }

    public async Task<PasswordChangeResponse> PasswordChangeAsync(string jwtToken, PasswordChangeRequest req, CancellationToken ct = default)
    {
        using var con = _dbFactory.Create();
        var p = new DynamicParameters();
        p.Add("@jwtToken", jwtToken, DbType.String);
        p.Add("@old_hashed_password", Sha256HexUtf16LE(req.OldPassword), DbType.String);
        p.Add("@new_hashed_password", Sha256HexUtf16LE(req.NewPassword), DbType.String);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_password_change_by_token",
                p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50015)
        { return new PasswordChangeResponse(false, "SESSION_NOT_FOUND", "Phiên đăng nhập không tồn tại."); }
        catch (SqlException ex) when (ex.Number == 50016)
        { return new PasswordChangeResponse(false, "SESSION_INACTIVE", "Phiên đăng nhập đã bị vô hiệu."); }
        catch (SqlException ex) when (ex.Number == 50017)
        { return new PasswordChangeResponse(false, "SESSION_EXPIRED", "Phiên đăng nhập đã hết hạn."); }
        catch (SqlException ex) when (ex.Number == 50061)
        { return new PasswordChangeResponse(false, "LOGIN_NOT_FOUND", "Không tìm thấy tài khoản."); }
        catch (SqlException ex) when (ex.Number == 50062)
        { return new PasswordChangeResponse(false, "LOGIN_INACTIVE", "Tài khoản đang không hoạt động."); }
        catch (SqlException ex) when (ex.Number == 50063)
        { return new PasswordChangeResponse(false, "OLD_PASSWORD_MISMATCH", "Mật khẩu hiện tại không đúng."); }
        catch (SqlException ex) when (ex.Number == 50064)
        { return new PasswordChangeResponse(false, "PASSWORD_CHANGE_FAILED", "Không thể đổi mật khẩu. Vui lòng thử lại."); }

        return new PasswordChangeResponse(true, null, "Đổi mật khẩu thành công.");
    }


    private static string GenerateNumericOtp6()
    {
        Span<byte> buf = stackalloc byte[4];
        RandomNumberGenerator.Fill(buf);
        var n = BitConverter.ToUInt32(buf) % 1_000_000u;
        return n.ToString("D6");
    }

    private static string Sha256Hex(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string? ExtractKnownCode(string message)
    {
        var known = new[] {
            "LOGIN_NOT_FOUND",
            "LOGIN_INVALID_CREDENTIALS",
            "ACCOUNT_PENDING_VERIFICATION",
            "ACCOUNT_INACTIVE",
            "EMAIL_NOT_FOUND",
            "OTP_VERIFY_FAILED",
            "SESSION_NOT_FOUND",
            "SESSION_INACTIVE",
            "SESSION_EXPIRED",
            "SESSION_INVALID",
            "AUTH_ME_FAILED",
            "PASSWORD_RESET_REQUEST_FAILED",
            "LOGOUT_NO_ACTIVE_SESSION",
            "AUTH_LOGOUT_FAILED",
            "EMAIL_EXISTS",
            "USER_REGISTER_FAILED"
        };
        foreach (var k in known)
        {
            if (!string.IsNullOrEmpty(message) &&
                message.Contains(k, StringComparison.OrdinalIgnoreCase))
                return k;
        }
        return null;
    }

    private static string ToUserMessage(string? code, string defaultMsg)
    {
        return code switch
        {
            "EMAIL_EXISTS" => "Email đã được đăng ký.",
            "USER_REGISTER_FAILED" => "Không thể đăng ký. Vui lòng thử lại sau.",
            "EMAIL_NOT_FOUND" => "Email không tồn tại trong hệ thống.",
            "LOGIN_NOT_FOUND" => "Email hoặc mật khẩu không đúng.",
            "LOGIN_INVALID_CREDENTIALS" => "Email hoặc mật khẩu không đúng.",
            "ACCOUNT_PENDING_VERIFICATION" => "Tài khoản chưa xác minh. Vui lòng kiểm tra email để nhập mã xác minh.",
            "ACCOUNT_INACTIVE" => "Tài khoản đang bị khóa hoặc chưa hoạt động.",
            "OTP_VERIFY_FAILED" => "Mã OTP không đúng hoặc đã hết hạn.",
            "PASSWORD_RESET_REQUEST_FAILED" => "Không thể tạo yêu cầu đặt lại mật khẩu. Vui lòng thử lại sau.",
            "SESSION_NOT_FOUND" => "Không tìm thấy phiên đăng nhập.",
            "SESSION_INACTIVE" => "Phiên đăng nhập đã bị vô hiệu.",
            "SESSION_EXPIRED" => "Phiên đăng nhập đã hết hạn.",
            "SESSION_INVALID" => "Phiên đăng nhập không hợp lệ.",
            "AUTH_ME_FAILED" => "Không thể lấy thông tin phiên.",
            "LOGOUT_NO_ACTIVE_SESSION" => "Phiên đăng nhập không còn hiệu lực.",
            "AUTH_LOGOUT_FAILED" => "Không thể đăng xuất. Vui lòng thử lại.",
            _ => string.IsNullOrWhiteSpace(defaultMsg) ? "Đã xảy ra lỗi. Vui lòng thử lại." : defaultMsg
        };
    }

    private static string Sha256HexUtf16LE(string input)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.Unicode.GetBytes(input)); // UTF-16LE
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
