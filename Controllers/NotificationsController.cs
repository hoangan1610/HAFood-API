using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/notifications")]
public class NotificationsController(INotificationService svc) : ControllerBase
{
    private static long? GetUid(ClaimsPrincipal user)
    {
        var s = user.FindFirstValue("uid") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(s, out var id) ? id : null;
    }

    // GET /api/notifications?status=&page=&page_size=
    [Authorize]
    [HttpGet]
    public async Task<ActionResult<NotificationsPageDto>> List([FromQuery] byte? status, [FromQuery] int page = 1, [FromQuery(Name = "page_size")] int pageSize = 20, CancellationToken ct = default)
    {
        var uid = GetUid(User);
        if (uid is null) return Unauthorized(new { code = "UNAUTHENTICATED" });
        var res = await svc.ListByUserAsync(uid.Value, status, page, pageSize, ct);
        return Ok(res);
    }

    // GET /api/notifications/unread-count
    [Authorize]
    [HttpGet("unread-count")]
    public async Task<ActionResult<UnreadCountDto>> UnreadCount(CancellationToken ct)
    {
        var uid = GetUid(User);
        if (uid is null) return Unauthorized(new { code = "UNAUTHENTICATED" });
        var cnt = await svc.GetUnreadCountAsync(uid.Value, ct);
        return Ok(new UnreadCountDto(cnt));
    }

    // POST /api/notifications/{id}/read
    [Authorize]
    [HttpPost("{id:long}/read")]
    public async Task<ActionResult> MarkRead(long id, CancellationToken ct)
    {
        await svc.MarkReadAsync(id, ct);
        return Ok();
    }

    // POST /api/notifications/read-all
    [Authorize]
    [HttpPost("read-all")]
    public async Task<ActionResult> MarkAllRead(CancellationToken ct)
    {
        var uid = GetUid(User);
        if (uid is null) return Unauthorized(new { code = "UNAUTHENTICATED" });
        await svc.MarkAllReadAsync(uid.Value, ct);
        return Ok();
    }

    // POST /api/notifications/{id}/delivered
    [Authorize]
    [HttpPost("{id:long}/delivered")]
    public async Task<ActionResult> MarkDelivered(long id, CancellationToken ct)
    {
        await svc.MarkDeliveredAsync(id, ct);
        return Ok();
    }
}
