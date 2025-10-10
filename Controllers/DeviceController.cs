using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeviceController(IDeviceService deviceService) : ControllerBase
{
    [HttpPost("upsert")]
    public async Task<ActionResult<DeviceUpsertResponse>> UpsertDevice(
        [FromBody] DeviceUpsertRequest req, CancellationToken ct)
    {
        // Lấy IP nếu client không truyền
        var ip = req.Ip;
        if (string.IsNullOrWhiteSpace(ip))
            ip = GetClientIp(HttpContext.Request);

        if (ip is null)
            return BadRequest(new { code = "MISSING_IP", message = "Không xác định được IP client." });

        var res = await deviceService.UpsertDeviceAsync(req with { Ip = ip }, ct);

        if (res.Success) return Ok(res);

        return res.Code switch
        {
            "DEVICE_UPSERT_FAILED" => Problem(title: res.Code, detail: res.Message),
            _ => BadRequest(res)
        };
    }

    private static string? GetClientIp(HttpRequest request)
    {
        var xff = request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xff))
            return xff.Split(',').FirstOrDefault()?.Trim();

        var realIp = request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(realIp)) return realIp.Trim();

        return request.HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}
