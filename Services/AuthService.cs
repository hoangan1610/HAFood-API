using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;

using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;



namespace HAShop.Api.Services;

public interface IAuthService
{
    Task<RegisterResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default);
    Task<VerifyOtpResponse> VerifyRegistrationOtpAsync(VerifyRegistrationOtpRequest req, CancellationToken ct = default);

    Task<LoginResponse> LoginAttemptAsync(LoginRequest req, CancellationToken ct = default);

    Task<LogoutResponse> LogoutAsync(Guid token, CancellationToken ct = default);
    //Task<DeviceUpsertResponse> UpsertDeviceAsync(DeviceUpsertRequest req, CancellationToken ct = default);

    Task<MeResponse> GetMeAsync(Guid token, CancellationToken ct = default);

}

public class AuthService(ISqlConnectionFactory dbFactory, ILogger<AuthService> logger) : IAuthService
{
    public async Task<RegisterResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default)
{
        // Hash tạm: SHA1 Base64 để lọt varchar(50). (Khuyên: nâng schema -> dùng bcrypt/Argon2)
        //string passwordHash = HashPasswordSha1Base64(req.Password);
        

        using var con = dbFactory.Create();

    var p = new DynamicParameters();
    p.Add("@full_name", req.FullName, DbType.String);
    p.Add("@phone", req.Phone, DbType.String);
    p.Add("@email", req.Email, DbType.String);
        p.Add("@hashed_password", req.Password, DbType.String);
        p.Add("@avatar", req.Avatar, DbType.String);

    // KHÔNG truyền các tham số OTP/Mail -> để SP dùng DEFAULT
    p.Add("@user_info_id", dbType: DbType.Int64, direction: ParameterDirection.Output);

    try
    {
        var cmd = new CommandDefinition(
            "dbo.usp_user_register",
            parameters: p,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct
        );
        await con.ExecuteAsync(cmd);
    }
    catch (SqlException ex) when (ex.Number == 50002) // EMAIL_EXISTS (từ SP)
    {
        throw new InvalidOperationException("EMAIL_EXISTS");
    }
    catch (SqlException ex) when (ex.Number == 50003) // USER_REGISTER_FAILED
    {
        throw new InvalidOperationException(ex.Message);
    }

    long userInfoId = p.Get<long>("@user_info_id");

    // SP default @require_verification = 1 (true)
    return new RegisterResponse(userInfoId, true);
}

public async Task<VerifyOtpResponse> VerifyRegistrationOtpAsync(VerifyRegistrationOtpRequest req, CancellationToken ct = default)
{
    using var con = dbFactory.Create();

    // 1) Tra user_info_id từ email
    const string sqlFind = @"SELECT user_info_id FROM dbo.tbl_login WITH (NOLOCK) WHERE email = @email;";
    long? userInfoId = await con.ExecuteScalarAsync<long?>(
        new CommandDefinition(sqlFind, new { email = req.Email }, cancellationToken: ct));

    if (userInfoId is null)
        return new VerifyOtpResponse(false, null, "EMAIL_NOT_FOUND");

    // 2) Gọi store verify OTP
    var p = new DynamicParameters();
    p.Add("@user_info_id", userInfoId.Value, DbType.Int64);
    p.Add("@purpose", (byte)1, DbType.Byte);              // 1 = register (fix cứng ở server)
    p.Add("@code_hash", req.Code, DbType.String);         // hiện SP so sánh plain
    p.Add("@invalidate_on_fail", false, DbType.Boolean);  // mặc định server

    p.Add("@verified", dbType: DbType.Boolean, direction: ParameterDirection.Output);
    p.Add("@otp_id", dbType: DbType.Int64, direction: ParameterDirection.Output);
    p.Add("@message", dbType: DbType.String, size: 100, direction: ParameterDirection.Output);

    try
    {
        await con.ExecuteAsync(new CommandDefinition(
            "dbo.usp_otp_verify", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }
    catch (SqlException ex) when (ex.Number == 50031) // OTP_VERIFY_FAILED
    {
        throw new InvalidOperationException(ex.Message);
    }

    bool ok = p.Get<bool>("@verified");
    long? otpId = null;
    try { otpId = p.Get<long>("@otp_id"); } catch { /* giữ null nếu không set */ }
    string msg = p.Get<string>("@message") ?? string.Empty;

    return new VerifyOtpResponse(ok, otpId, msg);
}
    public async Task<LoginResponse> LoginAttemptAsync(LoginRequest req, CancellationToken ct = default)
    {
        using var con = dbFactory.Create();

        var p = new DynamicParameters();
        p.Add("@email", req.Email, DbType.String);
        // theo SP: truyền hash theo chuẩn cũ của bạn, hoặc plain nếu SP so sánh hash đã lưu.
        // Vì usp_user_register đã lưu SHA-256(hex) ở cột hashed_password,
        // bạn cần đồng nhất: ở đây hãy SHA-256(password) -> hex để so sánh.
        p.Add("@hashed_password", Sha256HexUtf16LE(req.Password), DbType.String);



        p.Add("@device_uuid", req.DeviceUuid, DbType.Guid);
        p.Add("@device_model", req.DeviceModel, DbType.String);
        p.Add("@ip", req.Ip, DbType.String);
        p.Add("@session_ttl_seconds", 2_592_000, DbType.Int32); // 30 ngày

        // pending-flow: tự phát OTP và gửi mail
        p.Add("@auto_issue_verification_otp", true, DbType.Boolean);
        p.Add("@otp_code_plaintext", dbType: DbType.String, value: null);
        p.Add("@otp_ttl_seconds", 900, DbType.Int32); // 15 phút
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
            // SP ném:
            // 50010 LOGIN_NOT_FOUND
            // 50011 LOGIN_INVALID_CREDENTIALS
            // 50013 ACCOUNT_PENDING_VERIFICATION (đã auto-issue OTP + enqueue mail)
            // 50014 ACCOUNT_INACTIVE
            var code = ExtractKnownCode(ex.Message); // tách đoạn code ở đầu chuỗi
            return new LoginResponse(false, null, null, null, code, ex.Message);
        }

        // OK
        var uid = p.Get<long>("@user_info_id");
        var token = p.Get<Guid>("@token");
        var expires = p.Get<DateTime?>("@expires_at");

        return new LoginResponse(
            Success: true,
            UserInfoId: uid,
            Token: token,
            ExpiresAt: expires is null ? null : DateTime.SpecifyKind(expires.Value, DateTimeKind.Utc),
            Code: null
        );
    }

    public async Task<LogoutResponse> LogoutAsync(Guid token, CancellationToken ct = default)
    {
        using var con = dbFactory.Create();

        var p = new DynamicParameters();
        p.Add("@token", token, DbType.Guid);
        p.Add("@rows_affected", dbType: DbType.Int32, direction: ParameterDirection.Output);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_auth_logout",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50041) // LOGOUT_NO_ACTIVE_SESSION
        {
            return new LogoutResponse(false, "LOGOUT_NO_ACTIVE_SESSION", ex.Message);
        }
        catch (SqlException ex) when (ex.Number == 50042) // AUTH_LOGOUT_FAILED
        {
            return new LogoutResponse(false, "AUTH_LOGOUT_FAILED", ex.Message);
        }

        var affected = p.Get<int>("@rows_affected");
        if (affected > 0)
            return new LogoutResponse(true, null, "OK");

        // Trường hợp hiếm: SP không ném lỗi nhưng không cập nhật gì
        return new LogoutResponse(false, "LOGOUT_NO_ACTIVE_SESSION", "No active session for this token.");
    }

    public async Task<MeResponse> GetMeAsync(Guid token, CancellationToken ct = default)
    {
        using var con = dbFactory.Create();

        try
        {
            using var multi = await con.QueryMultipleAsync(
                new CommandDefinition("dbo.usp_auth_me",
                    new { token },
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct));

            var user = await multi.ReadFirstOrDefaultAsync<MeUserDto>();
            var device = await multi.ReadFirstOrDefaultAsync<MeDeviceDto>();
            var sess = await multi.ReadFirstOrDefaultAsync<MeSessionDto>();

            if (user is null || sess is null)
            {
                return new MeResponse
                {
                    Authenticated = false,
                    Code = "SESSION_INVALID",
                    Message = "Missing session or user."
                };
            }

            // Đặt Kind=Utc cho DateTime từ DB (datetime2 không có tz)
            sess.IssuedAt = DateTime.SpecifyKind(sess.IssuedAt, DateTimeKind.Utc);
            sess.ExpiresAt = DateTime.SpecifyKind(sess.ExpiresAt, DateTimeKind.Utc);

            return new MeResponse
            {
                Authenticated = true,
                User = user,
                Device = device,
                Session = sess
            };
        }
        catch (SqlException ex) when (ex.Number == 50015) // SESSION_NOT_FOUND
        {
            return new MeResponse { Authenticated = false, Code = "SESSION_NOT_FOUND", Message = ex.Message };
        }
        catch (SqlException ex) when (ex.Number == 50016) // SESSION_INACTIVE
        {
            return new MeResponse { Authenticated = false, Code = "SESSION_INACTIVE", Message = ex.Message };
        }
        catch (SqlException ex) when (ex.Number == 50017) // SESSION_EXPIRED
        {
            return new MeResponse { Authenticated = false, Code = "SESSION_EXPIRED", Message = ex.Message };
        }
        catch (SqlException ex) when (ex.Number == 50018) // AUTH_ME_FAILED
        {
            return new MeResponse { Authenticated = false, Code = "AUTH_ME_FAILED", Message = ex.Message };
        }
    }

    //public async Task<DeviceUpsertResponse> UpsertDeviceAsync(DeviceUpsertRequest req, CancellationToken ct = default)
    //{
    //    using var con = dbFactory.Create();

    //    var p = new DynamicParameters();
    //    p.Add("@user_info_id", req.UserInfoId, DbType.Int64);
    //    p.Add("@device_uuid", req.DeviceUuid, DbType.Guid);
    //    p.Add("@device_model", req.DeviceModel, DbType.String);
    //    p.Add("@ip", req.Ip, DbType.String);
    //    p.Add("@device_pk", dbType: DbType.Int64, direction: ParameterDirection.Output);

    //    try
    //    {
    //        await con.ExecuteAsync(new CommandDefinition(
    //            "dbo.usp_device_upsert",
    //            p,
    //            commandType: CommandType.StoredProcedure,
    //            cancellationToken: ct));
    //    }
    //    catch (SqlException ex) when (ex.Number == 50001) // DEVICE_UPSERT_FAILED
    //    {
    //        return new DeviceUpsertResponse(false, 0, "DEVICE_UPSERT_FAILED", ex.Message);
    //    }

    //    var devicePk = p.Get<long>("@device_pk");
    //    return new DeviceUpsertResponse(true, devicePk);
    //}
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
        // đơn giản: lấy token “LOGIN_…”, “ACCOUNT_…”, “AUTH_…”, nếu có.
        var known = new[] { "LOGIN_NOT_FOUND", "LOGIN_INVALID_CREDENTIALS", "ACCOUNT_PENDING_VERIFICATION", "ACCOUNT_INACTIVE" };
        return known.FirstOrDefault(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));
    }



    private static string Sha256HexUtf16LE(string input)
    {
        using var sha = SHA256.Create();
        // Encoding.Unicode = UTF-16 LE trong .NET
        var hash = sha.ComputeHash(Encoding.Unicode.GetBytes(input));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

}
