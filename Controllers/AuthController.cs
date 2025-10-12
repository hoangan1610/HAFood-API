using HAShop.Api.DTOs;
using HAShop.Api.Options;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(IAuthService auth) : ControllerBase
{
    [HttpPost("register")]
public async Task<ActionResult<RegisterResponse>> Register([FromBody] RegisterRequest req, CancellationToken ct)
{
    try
    {
        var res = await auth.RegisterAsync(req, ct);
        return Created(string.Empty, res);
    }
    catch (InvalidOperationException ex) when (ex.Message == "EMAIL_EXISTS")
    {
        return Conflict(new { code = "EMAIL_EXISTS", message = "Email đã tồn tại" });
    }
    catch (Exception ex)
    {
        return Problem(title: "USER_REGISTER_FAILED", detail: ex.Message);
    }
}
    [HttpPost("otp/resend")]
    public async Task<ActionResult<OtpResendResponse>> OtpResend([FromBody] OtpResendRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new { code = "MISSING_EMAIL", message = "Email là bắt buộc." });

        var ip = string.IsNullOrWhiteSpace(req.Ip) ? GetClientIp(Request) : req.Ip;
        var res = await auth.OtpResendAsync(req with { Ip = ip }, ct);
        return Ok(res); // blind-mode: luôn 200
    }

    [HttpPost("verify-otp")]
public async Task<ActionResult<VerifyOtpResponse>> VerifyRegistrationOtp(
    [FromBody] VerifyRegistrationOtpRequest req, CancellationToken ct)
{
    try
    {
        var res = await auth.VerifyRegistrationOtpAsync(req, ct);
        return res.Verified ? Ok(res) : BadRequest(res);
    }
    catch (Exception ex)
    {
        return Problem(title: "OTP_VERIFY_FAILED", detail: ex.Message);
    }
}

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(
    [FromBody] LoginRequest req, CancellationToken ct)
    {
        var res = await auth.LoginAttemptAsync(req, ct);

        if (res.Success) return Ok(res);

        return res.Code switch
        {
            "ACCOUNT_PENDING_VERIFICATION" => StatusCode(423, res), // 423 Locked (gợi ý)
            "LOGIN_NOT_FOUND" or "LOGIN_INVALID_CREDENTIALS" => Unauthorized(res),
            "ACCOUNT_INACTIVE" => Forbid(),
            _ => BadRequest(res)
        };
    }

    //[HttpPost("logout")]
    //public async Task<ActionResult<LogoutResponse>> Logout([FromBody] LogoutRequest? req, CancellationToken ct)
    //{
    //    var token = req?.Token ?? ParseBearerToken(Request.Headers["Authorization"]);

    //    if (token is null)
    //        return BadRequest(new { code = "MISSING_TOKEN", message = "Thiếu token. Truyền trong body hoặc header Authorization: Bearer {GUID}." });

    //    var res = await auth.LogoutAsync(token.Value, ct);

    //    if (res.Success) return Ok(res);

    //    // Map lỗi cho rõ ràng
    //    return res.Code switch
    //    {
    //        "LOGOUT_NO_ACTIVE_SESSION" => NotFound(res), // 404 khi token không còn session active
    //        "AUTH_LOGOUT_FAILED" => Problem(title: res.Code, detail: res.Message), // 50042 từ SP
    //        _ => BadRequest(res)
    //    };
    //}

    [HttpPost("password/forgot")]
    public async Task<ActionResult<PasswordResetRequestResult>> PasswordResetRequest(
    [FromBody] PasswordResetRequestDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new { code = "MISSING_EMAIL", message = "Email là bắt buộc." });

        var ip = string.IsNullOrWhiteSpace(req.Ip) ? GetClientIp(Request) : req.Ip;

        var res = await auth.PasswordResetRequestAsync(
            req with { Ip = ip }, ct);

        // Blind-mode: luôn 200 Accepted
        return Ok(res);
    }

    //[HttpPost("password/reset/confirm")]
    //public async Task<ActionResult<PasswordResetConfirmResponse>> PasswordResetConfirm(
    //    [FromBody] PasswordResetConfirmRequest req, CancellationToken ct)
    //{
    //    if (req.UserInfoId <= 0)
    //        return BadRequest(new { code = "MISSING_USER", message = "Thiếu hoặc sai user_info_id." });

    //    if (string.IsNullOrWhiteSpace(req.Otp))
    //        return BadRequest(new { code = "MISSING_OTP", message = "Thiếu mã OTP." });

    //    if (string.IsNullOrWhiteSpace(req.NewPassword))
    //        return BadRequest(new { code = "MISSING_PASSWORD", message = "Thiếu mật khẩu mới." });

    //    var res = await auth.PasswordResetConfirmAsync(req, ct);

    //    if (res.Success) return Ok(res);

    //    return res.Code switch
    //    {
    //        "OTP_VERIFY_FAILED" => BadRequest(res),
    //        "PASSWORD_RESET_CONFIRM_FAILED" => Problem(title: res.Code, detail: res.Message),
    //        _ => BadRequest(res)
    //    };
    //}

    [HttpPost("password/reset/verify")]
    public async Task<ActionResult<PasswordResetVerifyResponse>> PasswordResetVerify(
      [FromBody] PasswordResetVerifyRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Otp))
            return BadRequest(new { code = "MISSING_FIELDS", message = "Thiếu email hoặc OTP." });

        var res = await auth.PasswordResetVerifyAsync(req, ct);
        return res.Verified ? Ok(res) : BadRequest(res);
    }

    // BƯỚC 2: Confirm đổi mật khẩu bằng otpId
    [HttpPost("password/reset/confirm")]
    public async Task<ActionResult<PasswordResetConfirmResponse>> PasswordResetConfirm(
        [FromBody] PasswordResetConfirmRequest req, CancellationToken ct)
    {
        if (req.OtpId <= 0)
            return BadRequest(new { code = "MISSING_OTP_ID", message = "Thiếu hoặc sai otpId." });

        if (string.IsNullOrWhiteSpace(req.NewPassword))
            return BadRequest(new { code = "MISSING_PASSWORD", message = "Thiếu mật khẩu mới." });

        var res = await auth.PasswordResetConfirmAsync(req, ct);

        if (res.Success) return Ok(res);

        return res.Code switch
        {
            "PASSWORD_RESET_INVALID_STATE" => BadRequest(res),
            "PASSWORD_RESET_CONFIRM_FAILED" => Problem(title: res.Code, detail: res.Message),
            _ => BadRequest(res)
        };
    }
    //[HttpGet("me")]
    //public async Task<ActionResult<MeResponse>> Me(CancellationToken ct)
    //{
    //    var token = ParseBearerToken(Request.Headers["Authorization"]);
    //    if (token is null)
    //        return Unauthorized(new { code = "MISSING_TOKEN", message = "Thiếu Authorization: Bearer {GUID}." });

    //    var res = await auth.GetMeAsync(token.Value, ct);

    //    if (res.Authenticated) return Ok(res);

    //    return res.Code switch
    //    {
    //        "SESSION_NOT_FOUND" or "SESSION_INACTIVE" or "SESSION_EXPIRED"
    //            => Unauthorized(res),               // 401
    //        "AUTH_ME_FAILED"
    //            => Problem(title: res.Code, detail: res.Message),
    //        _
    //            => Unauthorized(res)
    //    };
    //}


    //[HttpPost("password/change")]
    //public async Task<ActionResult<PasswordChangeResponse>> ChangePassword(
    //[FromBody] PasswordChangeRequest req, CancellationToken ct)
    //{
    //    var token = ParseBearerToken(Request.Headers["Authorization"]);
    //    if (token is null)
    //        return Unauthorized(new { code = "MISSING_TOKEN", message = "Thiếu Authorization: Bearer {GUID}." });

    //    if (string.IsNullOrWhiteSpace(req.OldPassword))
    //        return BadRequest(new { code = "MISSING_OLD_PASSWORD", message = "Thiếu mật khẩu hiện tại." });

    //    if (string.IsNullOrWhiteSpace(req.NewPassword))
    //        return BadRequest(new { code = "MISSING_NEW_PASSWORD", message = "Thiếu mật khẩu mới." });

    //    // (tuỳ chọn) chặn new == old ở controller
    //    if (req.OldPassword == req.NewPassword)
    //        return BadRequest(new { code = "PASSWORD_UNCHANGED", message = "Mật khẩu mới không được trùng mật khẩu hiện tại." });

    //    var res = await auth.PasswordChangeAsync(token.Value, req, ct);

    //    if (res.Success) return Ok(res);

    //    // map status code gợi ý
    //    return res.Code switch
    //    {
    //        "SESSION_NOT_FOUND" or "SESSION_INACTIVE" or "SESSION_EXPIRED"
    //            => Unauthorized(res),         // 401
    //        "LOGIN_INACTIVE"
    //            => StatusCode(423, res),      // 423 Locked
    //        "OLD_PASSWORD_MISMATCH"
    //            => BadRequest(res),           // 400
    //        "LOGIN_NOT_FOUND"
    //            => NotFound(res),             // 404
    //        "PASSWORD_CHANGE_FAILED"
    //            => Problem(title: res.Code, detail: res.Message), // 50064 từ SP
    //        _ => BadRequest(res)
    //    };
    //}

    // GET /api/auth/me  -> JWT
    [HttpGet("me")]
    public async Task<ActionResult<MeResponse>> Me(CancellationToken ct)
    {
        var jwt = ParseBearerJwt(Request.Headers["Authorization"]);
        if (jwt is null)
            return Unauthorized(new { code = "MISSING_TOKEN", message = "Thiếu Authorization: Bearer {JWT}." });

        var res = await auth.GetMeAsync(jwt, ct);   // <-- string JWT

        if (res.Authenticated) return Ok(res);

        return res.Code switch
        {
            "SESSION_NOT_FOUND" or "SESSION_INACTIVE" or "SESSION_EXPIRED" => Unauthorized(res),
            "AUTH_ME_FAILED" => Problem(title: res.Code, detail: res.Message),
            _ => Unauthorized(res)
        };
    }

    // POST /api/auth/logout -> JWT
    // POST /api/auth/logout -> chỉ header Bearer
    [HttpPost("logout")]
    public async Task<ActionResult<LogoutResponse>> Logout(CancellationToken ct)
    {
        var jwt = ParseBearerJwt(Request.Headers["Authorization"]);
        if (string.IsNullOrWhiteSpace(jwt))
            return BadRequest(new { code = "MISSING_TOKEN", message = "Thiếu token. Truyền trong header Authorization: Bearer {JWT}." });

        var res = await auth.LogoutAsync(jwt, ct);
        if (res.Success) return Ok(res);

        return res.Code switch
        {
            "LOGOUT_NO_ACTIVE_SESSION" => NotFound(res),
            "AUTH_LOGOUT_FAILED" => Problem(title: res.Code, detail: res.Message),
            _ => BadRequest(res)
        };
    }


    // POST /api/auth/password/change -> JWT
    [HttpPost("password/change")]
    public async Task<ActionResult<PasswordChangeResponse>> ChangePassword(
        [FromBody] PasswordChangeRequest req, CancellationToken ct)
    {
        var jwt = ParseBearerJwt(Request.Headers["Authorization"]);
        if (jwt is null)
            return Unauthorized(new { code = "MISSING_TOKEN", message = "Thiếu Authorization: Bearer {JWT}." });

        if (string.IsNullOrWhiteSpace(req.OldPassword))
            return BadRequest(new { code = "MISSING_OLD_PASSWORD", message = "Thiếu mật khẩu hiện tại." });
        if (string.IsNullOrWhiteSpace(req.NewPassword))
            return BadRequest(new { code = "MISSING_NEW_PASSWORD", message = "Thiếu mật khẩu mới." });
        if (req.OldPassword == req.NewPassword)
            return BadRequest(new { code = "PASSWORD_UNCHANGED", message = "Mật khẩu mới không được trùng mật khẩu hiện tại." });

        var res = await auth.PasswordChangeAsync(jwt, req, ct);

        if (res.Success) return Ok(res);

        return res.Code switch
        {
            "SESSION_NOT_FOUND" or "SESSION_INACTIVE" or "SESSION_EXPIRED" => Unauthorized(res),
            "LOGIN_INACTIVE" => StatusCode(423, res),
            "OLD_PASSWORD_MISMATCH" => BadRequest(res),
            "LOGIN_NOT_FOUND" => NotFound(res),
            "PASSWORD_CHANGE_FAILED" => Problem(title: res.Code, detail: res.Message),
            _ => BadRequest(res)
        };
    }




    [HttpPost("dev-token")]
    public ActionResult<DevTokenResponse> IssueDevToken(
    [FromBody] DevTokenRequest req,
    [FromServices] IOptions<JwtOptions> jwtOpt,
    [FromServices] IWebHostEnvironment env)
    {
        // Chỉ bật ở Development (để tránh lộ công cụ phát token trên Prod)
        if (!env.IsDevelopment())
            return NotFound(new { code = "NOT_FOUND", message = "Endpoint is disabled in non-development environments." });

        if (req.UserInfoId <= 0)
            return BadRequest(new { code = "MISSING_USER", message = "userInfoId phải > 0." });

        var now = DateTimeOffset.UtcNow;
        var exp = now.AddSeconds(Math.Max(60, req.TtlSeconds)); // tối thiểu 60s

        var jwtCfg = jwtOpt.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtCfg.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
    {
        new(JwtRegisteredClaimNames.Sub, req.UserInfoId.ToString()),
        new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
        new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        new("uid", req.UserInfoId.ToString())
    };
        if (req.DeviceUuid is Guid did)
            claims.Add(new Claim("did", did.ToString()));
        if (!string.IsNullOrWhiteSpace(req.DeviceModel))
            claims.Add(new Claim("dmodel", req.DeviceModel!));

        var token = new JwtSecurityToken(
            issuer: jwtCfg.Issuer,
            audience: jwtCfg.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: exp.UtcDateTime,
            signingCredentials: creds
        );

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);
        return Ok(new DevTokenResponse(jwt, exp));
    }

    [HttpGet("dev-claims")]
    public ActionResult<object> ReadDevClaims([FromServices] IWebHostEnvironment env)
    {
        if (!env.IsDevelopment())
            return NotFound(new { code = "NOT_FOUND", message = "Endpoint is disabled in non-development environments." });

        var jwt = ParseBearerJwt(Request.Headers["Authorization"]);
        if (string.IsNullOrWhiteSpace(jwt))
            return Unauthorized(new { code = "MISSING_TOKEN", message = "Thiếu Authorization: Bearer {JWT}." });

        // Nếu token hợp lệ, JwtBearer middleware sẽ set User.Identity.IsAuthenticated = true
        if (!(User?.Identity?.IsAuthenticated ?? false))
            return Unauthorized(new { code = "INVALID_TOKEN", message = "Token không hợp lệ hoặc hết hạn." });

        var claims = User.Claims.ToDictionary(c => c.Type, c => c.Value);
        return Ok(new { authenticated = true, claims });
    }




    private static Guid? ParseBearerToken(string? authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader)) return null;
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;

        var raw = authHeader.Substring("Bearer ".Length).Trim();
        return Guid.TryParse(raw, out var g) ? g : null;
    }


    private static string? ParseBearerJwt(string? authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader)) return null;
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var raw = authHeader.Substring("Bearer ".Length).Trim();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }



    // Helper lấy IP từ các header phổ biến (X-Forwarded-For / X-Real-IP) hoặc RemoteIp
    private static string? GetClientIp(HttpRequest request)
    {
        // Ưu tiên X-Forwarded-For (có thể nhiều IP, lấy IP đầu)
        var xff = request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xff))
        {
            var first = xff.Split(',').Select(s => s.Trim()).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) return first;
        }

        // Tiếp theo X-Real-IP
        var realIp = request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(realIp)) return realIp.Trim();

        // Cuối cùng RemoteIpAddress
        return request.HttpContext.Connection.RemoteIpAddress?.ToString();
    }

}
