// Controllers/UsersController.cs
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
    public UsersController(IUserService users) => _users = users;

    [HttpPut("me/profile")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(UserProfileUpdateResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserProfileUpdateResponse>> UpdateMyProfile(
        [FromBody] UserProfileUpdateRequest body, CancellationToken ct)
    {
        if (!(User?.Identity?.IsAuthenticated ?? false))
            throw new AppException("UNAUTHENTICATED");

        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            throw new AppException("TOKEN_INVALID_OR_NO_USERID");

        var res = await _users.UpdateProfileAsync(userId.Value, body, ct);

        if (!res.Success)
            throw new AppException(res.Code ?? "ERROR", res.Message);

        return Ok(res);
    }

    [HttpPost("me/avatar")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    [Produces("application/json")]
    public async Task<ActionResult<UserProfileUpdateResponse>> UploadAvatar(
        IFormFile file, CancellationToken ct)
    {
        if (!(User?.Identity?.IsAuthenticated ?? false))
            throw new AppException("UNAUTHENTICATED");

        var userId = GetUserIdFromClaims(User) ?? throw new AppException("TOKEN_INVALID_OR_NO_USERID");

        // 1) Validate cơ bản
        if (file is null || file.Length == 0)
            throw new AppException("AVATAR_EMPTY");

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
            throw new AppException("AVATAR_EXTENSION_NOT_ALLOWED");

        if (file.Length > 5 * 1024 * 1024)
            throw new AppException("AVATAR_TOO_LARGE");

        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new AppException("AVATAR_INVALID_CONTENTTYPE");

        // 2) Lưu file
        var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var saveDir = Path.Combine(webRoot, "uploads", "avatars", userId.ToString());
        Directory.CreateDirectory(saveDir);

        var safeName = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{ext}";
        var savePath = Path.Combine(saveDir, safeName);

        await using (var stream = System.IO.File.Create(savePath))
        {
            await file.CopyToAsync(stream, ct);
        }

        // 3) URL công khai
        var publicUrl = $"/uploads/avatars/{userId}/{safeName}";

        // 4) Cập nhật DB qua service
        var req = new UserProfileUpdateRequest(null, null, publicUrl);
        var res = await _users.UpdateProfileAsync(userId, req, ct);

        if (!res.Success)
        {
            try { System.IO.File.Delete(savePath); } catch { /* ignore */ }
            throw new AppException(res.Code ?? "ERROR", res.Message);
        }

        return Ok(new UserProfileUpdateResponse(true, null, "Đã cập nhật ảnh đại diện."));
    }

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
