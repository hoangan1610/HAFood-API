using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api")]
public class PromotionsController(IPromotionService promos) : ControllerBase
{
    // GET /api/promotions/preview?code=...&cart_id=...&subtotal=...
    // - Nếu truyền cả cart_id và subtotal -> ưu tiên cart_id (BE tự tính).
    // - YÊU CẦU JWT để check case new-user-only (is_new_user).
    [Authorize]
    [HttpGet("promotions/preview")]
    public async Task<ActionResult<PromotionPreviewResponse>> Preview(
        [FromQuery] string code,
        [FromQuery(Name = "cart_id")] long? cartId,
        [FromQuery] decimal? subtotal,
        CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null)
            return Unauthorized(new { code = "MISSING_TOKEN", message = "Thiếu JWT." });

        var res = await promos.PreviewAsync(uid.Value, code, cartId, subtotal, ct);
        return Ok(res);
    }

    private static long? GetUserId(ClaimsPrincipal user)
    {
        var s = user.FindFirstValue("uid") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(s, out var id) ? id : null;
    }
}
