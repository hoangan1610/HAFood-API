using System.Security.Claims;
using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _svc;

    public NotificationsController(INotificationService svc) => _svc = svc;

    // GET /api/notifications/latest?take=10
    [HttpGet("latest")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiOkResponse<NotificationLatestResultDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiOkResponse<NotificationLatestResultDto>>> GetLatest(
        [FromQuery] int take = 10,
        CancellationToken ct = default)
    {
        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            return Unauthorized(BuildProblem("UNAUTHENTICATED", StatusCodes.Status401Unauthorized));

        if (take <= 0 || take > 50) take = 10;

        const byte channel = 1; // in-app

        var result = await _svc.GetLatestAsync(userId.Value, channel, take, ct);
        return Ok(new ApiOkResponse<NotificationLatestResultDto>(true, result));
    }

    // GET /api/notifications?page=1&pageSize=20&onlyUnread=false&type=1
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiOkResponse<NotificationPagedResultDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiOkResponse<NotificationPagedResultDto>>> GetPaged(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool onlyUnread = false,
        [FromQuery] byte? type = null,
        CancellationToken ct = default)
    {
        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            return Unauthorized(BuildProblem("UNAUTHENTICATED", StatusCodes.Status401Unauthorized));

        if (page <= 0) page = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 20;

        const byte channel = 1; // in-app

        var result = await _svc.GetPagedAsync(
            userId.Value, page, pageSize, channel, onlyUnread, type, ct);

        return Ok(new ApiOkResponse<NotificationPagedResultDto>(true, result));
    }

    // POST /api/notifications/{id}/read
    [HttpPost("{id:long}/read")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiOkResponse<bool>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiOkResponse<bool>>> MarkRead(
        long id,
        CancellationToken ct = default)
    {
        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            return Unauthorized(BuildProblem("UNAUTHENTICATED", StatusCodes.Status401Unauthorized));

        var ok = await _svc.MarkReadAsync(userId.Value, id, ct);
        if (!ok)
        {
            return NotFound(BuildProblem("NOTIFICATION_NOT_FOUND",
                StatusCodes.Status404NotFound,
                $"Notification {id} not found or already read."));
        }

        return Ok(new ApiOkResponse<bool>(true, true, "Đã đánh dấu đã đọc."));
    }

    // POST /api/notifications/read-all
    [HttpPost("read-all")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiOkResponse<int>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiOkResponse<int>>> MarkAllRead(
        CancellationToken ct = default)
    {
        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            return Unauthorized(BuildProblem("UNAUTHENTICATED", StatusCodes.Status401Unauthorized));

        const byte channel = 1; // in-app

        var affected = await _svc.MarkAllReadAsync(userId.Value, channel, ct);

        return Ok(new ApiOkResponse<int>(true, affected, "Đã đánh dấu tất cả thông báo là đã đọc."));
    }

    // ===== helpers (copy style từ AddressesController) =====

    private static long? GetUserIdFromClaims(ClaimsPrincipal user)
    {
        string? raw =
            user.FindFirstValue("sub") ??
            user.FindFirstValue("user_id") ??
            user.FindFirstValue(ClaimTypes.NameIdentifier) ??
            user.FindFirstValue("uid");

        return long.TryParse(raw, out var id) ? id : null;
    }

    private ProblemDetails BuildProblem(string code, int status, string? tech = null)
    {
        var pd = new ProblemDetails
        {
            Title = code,
            Detail = tech ?? code,
            Status = status,
            Type = "about:blank"
        };
        pd.Extensions["traceId"] = HttpContext?.TraceIdentifier ?? string.Empty;
        pd.Extensions["code"] = code;
        if (!string.IsNullOrWhiteSpace(tech))
            pd.Extensions["techMessage"] = tech;
        return pd;
    }
}
