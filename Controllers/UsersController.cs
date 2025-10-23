using System.Security.Claims;
using HAShop.Api.DTOs;
using HAShop.Api.Services;
using HAShop.Api.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class UsersController : ControllerBase
{
    private readonly IUserService _users;

    public UsersController(IUserService users)
    {
        _users = users;
    }


    [HttpPut("me/profile")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(UserProfileUpdateResponse), StatusCodes.Status200OK)]
    //[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    //[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    //[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    //[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    //[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    //[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<UserProfileUpdateResponse>> UpdateMyProfile(
        [FromBody] UserProfileUpdateRequest body,
        CancellationToken ct)
    {
        if (!(User?.Identity?.IsAuthenticated ?? false))
        {
            const string code = "UNAUTHENTICATED";
            var pd = BuildProblemDetails(code, StatusCodes.Status401Unauthorized);
            return Unauthorized(pd);
        }

        var userId = GetUserIdFromClaims(User);
        if (userId is null)
        {
            const string code = "TOKEN_INVALID_OR_NO_USERID";
            var pd = BuildProblemDetails(code, StatusCodes.Status401Unauthorized);
            return Unauthorized(pd);
        }

        var res = await _users.UpdateProfileAsync(userId.Value, body, ct);

        if (!res.Success)
        {
            var code = res.Code ?? "ERROR";
            var pd = BuildProblemDetails(code, MapStatus(code), techMessage: res.Message);
            return code switch
            {
                "UNAUTHENTICATED_OR_NO_SESSION_USER" => Unauthorized(pd),
                "USER_INFO_NOT_FOUND" => NotFound(pd),
                "PHONE_ALREADY_IN_USE" => Conflict(pd),
                "USER_UPDATE_PROFILE_FAILED" => StatusCode(StatusCodes.Status500InternalServerError, pd),
                _ => StatusCode(StatusCodes.Status500InternalServerError, pd)
            };
        }

        
        return Ok(res);
    }


    [HttpPost("me/avatar")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10MB
    [Consumes("multipart/form-data")]
    [Produces("application/json")]
    public async Task<ActionResult<UserProfileUpdateResponse>> UploadAvatar(
    IFormFile file, CancellationToken ct)
    {
        if (!(User?.Identity?.IsAuthenticated ?? false))
            return Unauthorized(BuildProblemDetails("UNAUTHENTICATED", StatusCodes.Status401Unauthorized));

        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            return Unauthorized(BuildProblemDetails("TOKEN_INVALID_OR_NO_USERID", StatusCodes.Status401Unauthorized));

        // 1) Validate file cơ bản
        if (file is null || file.Length == 0)
            return BadRequest(BuildProblemDetails("AVATAR_EMPTY", StatusCodes.Status400BadRequest));

        // Chỉ cho phép jpg/png/webp
        var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
            return BadRequest(BuildProblemDetails("AVATAR_EXTENSION_NOT_ALLOWED", StatusCodes.Status400BadRequest));

        if (file.Length > 5 * 1024 * 1024) // 5MB
            return BadRequest(BuildProblemDetails("AVATAR_TOO_LARGE", StatusCodes.Status400BadRequest));

        // (khuyến nghị) kiểm tra MIME
        if (!file.ContentType.StartsWith("image/"))
            return BadRequest(BuildProblemDetails("AVATAR_INVALID_CONTENTTYPE", StatusCodes.Status400BadRequest));

        // 2) Lưu file xuống local
        // ví dụ: wwwroot/uploads/avatars/{userId}/yyyyMMddHHmmssfff_{rand}.ext
        var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var saveDir = Path.Combine(webRoot, "uploads", "avatars", userId.Value.ToString());
        Directory.CreateDirectory(saveDir);

        var safeName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{ext}";
        var savePath = Path.Combine(saveDir, safeName);

        await using (var stream = System.IO.File.Create(savePath))
        {
            await file.CopyToAsync(stream, ct);
        }

        // 3) Tạo URL công khai (tuỳ cấu hình reverse proxy)
        var publicUrl = $"/uploads/avatars/{userId}/{safeName}";

        // 4) Cập nhật DB: gọi service cũ với chỉ avatar
        var req = new UserProfileUpdateRequest(null, null, publicUrl);
        var res = await _users.UpdateProfileAsync(userId.Value, req, ct);

        if (!res.Success)
        {
            // Nếu DB báo lỗi, xoá file vừa lưu để tránh rác
            try { System.IO.File.Delete(savePath); } catch { /* ignore */ }

            var code = res.Code ?? "ERROR";
            var pd = BuildProblemDetails(code, MapStatus(code), techMessage: res.Message);
            return code switch
            {
                "USER_INFO_NOT_FOUND" => NotFound(pd),
                "PHONE_ALREADY_IN_USE" => Conflict(pd),
                _ => StatusCode(StatusCodes.Status500InternalServerError, pd)
            };
        }

        // 5) Trả kết quả
        return Ok(new UserProfileUpdateResponse(true, null, "Đã cập nhật ảnh đại diện."));
    }

    private ProblemDetails BuildProblemDetails(string code, int status, string? techMessage = null)
    {
        var pd = new ProblemDetails
        {
            Title = code,
            Detail = ErrorCatalog.Friendly(code, techMessage),
            Status = status,
            Type = "about:blank"
        };

        // Các extension để debug (FE không cần hiển thị)
        pd.Extensions["traceId"] = HttpContext?.TraceIdentifier ?? string.Empty;
        pd.Extensions["code"] = code;
        if (!string.IsNullOrWhiteSpace(techMessage))
            pd.Extensions["techMessage"] = techMessage;

        return pd;
    }

    private static int MapStatus(string code) => code switch
    {
        "UNAUTHENTICATED" => StatusCodes.Status401Unauthorized,
        "UNAUTHENTICATED_OR_NO_SESSION_USER" => StatusCodes.Status401Unauthorized,
        "USER_INFO_NOT_FOUND" => StatusCodes.Status404NotFound,
        "PHONE_ALREADY_IN_USE" => StatusCodes.Status409Conflict,
        "USER_UPDATE_PROFILE_FAILED" => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError
    };

    private static long? GetUserIdFromClaims(ClaimsPrincipal user)
    {
        string? raw =
            user.FindFirstValue("sub") ??
            user.FindFirstValue("user_id") ??
            user.FindFirstValue(ClaimTypes.NameIdentifier) ??
            user.FindFirstValue("uid");

        return long.TryParse(raw, out var id) ? id : null;
    }
}
