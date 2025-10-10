using Microsoft.AspNetCore.Mvc;
using HAShop.Api.DTOs;
using HAShop.Api.Services;
using System.Threading;
using System.Threading.Tasks;
using System;

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

    [HttpPost("logout")]
    public async Task<ActionResult<LogoutResponse>> Logout([FromBody] LogoutRequest? req, CancellationToken ct)
    {
        var token = req?.Token ?? ParseBearerToken(Request.Headers["Authorization"]);

        if (token is null)
            return BadRequest(new { code = "MISSING_TOKEN", message = "Thiếu token. Truyền trong body hoặc header Authorization: Bearer {GUID}." });

        var res = await auth.LogoutAsync(token.Value, ct);

        if (res.Success) return Ok(res);

        // Map lỗi cho rõ ràng
        return res.Code switch
        {
            "LOGOUT_NO_ACTIVE_SESSION" => NotFound(res), // 404 khi token không còn session active
            "AUTH_LOGOUT_FAILED" => Problem(title: res.Code, detail: res.Message), // 50042 từ SP
            _ => BadRequest(res)
        };
    }

    [HttpGet("me")]
    public async Task<ActionResult<MeResponse>> Me(CancellationToken ct)
    {
        var token = ParseBearerToken(Request.Headers["Authorization"]);
        if (token is null)
            return Unauthorized(new { code = "MISSING_TOKEN", message = "Thiếu Authorization: Bearer {GUID}." });

        var res = await auth.GetMeAsync(token.Value, ct);

        if (res.Authenticated) return Ok(res);

        return res.Code switch
        {
            "SESSION_NOT_FOUND" or "SESSION_INACTIVE" or "SESSION_EXPIRED"
                => Unauthorized(res),               // 401
            "AUTH_ME_FAILED"
                => Problem(title: res.Code, detail: res.Message),
            _
                => Unauthorized(res)
        };
    }

    private static Guid? ParseBearerToken(string? authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader)) return null;
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;

        var raw = authHeader.Substring("Bearer ".Length).Trim();
        return Guid.TryParse(raw, out var g) ? g : null;
    }


    //[HttpPost("device/upsert")]
    //public async Task<ActionResult<DeviceUpsertResponse>> UpsertDevice([FromBody] DeviceUpsertRequest req, CancellationToken ct)
    //{
    //    // Bổ sung IP nếu client không gửi
    //    var ip = req.Ip;
    //    if (string.IsNullOrWhiteSpace(ip))
    //        ip = GetClientIp(HttpContext.Request);

    //    if (ip is null)
    //        return BadRequest(new { code = "MISSING_IP", message = "Không xác định được IP client. Hãy truyền 'ip' trong body hoặc cấu hình proxy headers." });

    //    // Gọi service
    //    var res = await auth.UpsertDeviceAsync(req with { Ip = ip }, ct);

    //    if (res.Success) return Ok(res);

    //    // Mapping lỗi từ SP
    //    return res.Code switch
    //    {
    //        "DEVICE_UPSERT_FAILED" => Problem(title: res.Code, detail: res.Message),
    //        _ => BadRequest(res)
    //    };
    //}



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
