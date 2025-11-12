// File: Api/Controllers/PromotionsController.cs
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers
{
    [ApiController]
    [Route("api/promotions")]
    public class PromotionsController : ControllerBase
    {
        private readonly IPromotionService _svc;
        public PromotionsController(IPromotionService svc) => _svc = svc;

        [HttpPost("cart/list")]
        [AllowAnonymous]
        [Produces("application/json")]
        public async Task<ActionResult<PromoListResponse>> ListForCart([FromBody] PromoListRequest body, CancellationToken ct)
        {
            var userId = TryGetUserId(HttpContext.User);
            var deviceUuid = Request.Headers["X-Device-Id"].FirstOrDefault();
            var res = await _svc.ListActiveAsync(userId, deviceUuid, body, ct);
            return Ok(res);
        }

        [HttpPost("cart/quote")]
        [AllowAnonymous]
        [Produces("application/json")]
        public async Task<ActionResult<PromoQuoteResponse>> Quote([FromBody] PromoQuoteRequest body, CancellationToken ct)
        {
            var userId = TryGetUserId(HttpContext.User);
            var deviceUuid = Request.Headers["X-Device-Id"].FirstOrDefault();
            var res = await _svc.QuoteAsync(userId, deviceUuid, body, ct);
            return Ok(res);
        }

        [HttpPost("cart/reserve")]
        [Authorize]
        [Produces("application/json")]
        public async Task<ActionResult<PromoReserveResponse>> Reserve([FromBody] PromoReserveRequest body, CancellationToken ct)
        {
            // cho phép reserve cho guest? ở đây yêu cầu auth (tùy chính sách)
            var userId = TryGetUserId(HttpContext.User) ?? 0;
            var deviceUuid = Request.Headers["X-Device-Id"].FirstOrDefault() ?? body.DeviceUuid;
            var res = await _svc.ReserveAsync(userId, deviceUuid, body, ct);
            return Ok(res);
        }

        [HttpPost("cart/release")]
        [Authorize]
        [Produces("application/json")]
        public async Task<ActionResult<PromoReleaseResponse>> Release([FromBody] PromoReleaseRequest body, CancellationToken ct)
        {
            var res = await _svc.ReleaseAsync(body, ct);
            return Ok(res);
        }

        private static long? TryGetUserId(ClaimsPrincipal user)
        {
            var raw = user?.FindFirstValue("sub")
                   ?? user?.FindFirstValue("user_id")
                   ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? user?.FindFirstValue("uid");
            return long.TryParse(raw, out var id) ? id : null;
        }
    }
}
