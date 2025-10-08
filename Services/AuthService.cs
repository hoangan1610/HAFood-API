using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace HAShop.Api.Services;

public interface IAuthService
{
    Task<RegisterResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default);
    Task<VerifyOtpResponse> VerifyRegistrationOtpAsync(VerifyRegistrationOtpRequest req, CancellationToken ct = default);
}

public class AuthService(ISqlConnectionFactory dbFactory, ILogger<AuthService> logger) : IAuthService
{
    public async Task<RegisterResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default)
{
    // Hash tạm: SHA1 Base64 để lọt varchar(50). (Khuyên: nâng schema -> dùng bcrypt/Argon2)
    string passwordHash = HashPasswordSha1Base64(req.Password);

    using var con = dbFactory.Create();

    var p = new DynamicParameters();
    p.Add("@full_name", req.FullName, DbType.String);
    p.Add("@phone", req.Phone, DbType.String);
    p.Add("@email", req.Email, DbType.String);
    p.Add("@hashed_password", passwordHash, DbType.String);
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

private static string HashPasswordSha1Base64(string password)
{
    using var sha1 = SHA1.Create();
    var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(password));
    return Convert.ToBase64String(bytes);
}
}
