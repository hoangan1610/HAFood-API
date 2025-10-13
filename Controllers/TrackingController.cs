using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/tracking")]
public class TrackingController(ITrackingService tracking) : ControllerBase
{
    // FE gọi khi view product/category/search... (không bắt buộc JWT)
    [HttpPost]
    public async Task<ActionResult<TrackEventResponse>> Log([FromBody] TrackEventRequest req, CancellationToken ct)
    {
        // Nếu không truyền IP từ FE thì lấy IP từ request
        var ip = string.IsNullOrWhiteSpace(req.Ip) ? GetClientIp(Request) : req.Ip;
        var id = await tracking.LogAsync(req with { Ip = ip }, ct);
        return Created($"/api/tracking/{id}", new TrackEventResponse(id));
    }

    // Top products (tuỳ quyền, để public cũng được nếu cần)
    [HttpGet("top-products")]
    public async Task<ActionResult<IReadOnlyList<TopProductViewDto>>> TopProducts(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int top = 10, CancellationToken ct = default)
    {
        var data = await tracking.TopProductsAsync(from, to, Math.Clamp(top, 1, 100), ct);
        return Ok(data);
    }

    // Top categories
    [HttpGet("top-categories")]
    public async Task<ActionResult<IReadOnlyList<TopCategoryViewDto>>> TopCategories(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int top = 10, CancellationToken ct = default)
    {
        var data = await tracking.TopCategoriesAsync(from, to, Math.Clamp(top, 1, 100), ct);
        return Ok(data);
    }

    // Recent by device (để show “vừa xem”)
    [HttpGet("recent")]
    public async Task<ActionResult<IReadOnlyList<RecentEventDto>>> Recent([FromQuery(Name = "device_id")] long deviceId, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        if (deviceId <= 0) return BadRequest(new { code = "MISSING_DEVICE", message = "Thiếu device_id." });
        var data = await tracking.RecentByDeviceAsync(deviceId, Math.Clamp(limit, 1, 100), ct);
        return Ok(data);
    }

    private static string? GetClientIp(HttpRequest request)
    {
        var xff = request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xff))
        {
            var first = xff.Split(',').Select(s => s.Trim()).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) return first;
        }
        var realIp = request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(realIp)) return realIp.Trim();
        return request.HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}
