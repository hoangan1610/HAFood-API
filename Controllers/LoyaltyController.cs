using System.Security.Claims;
using System.Linq;
using HAShop.Api.DTOs;
using HAShop.Api.Services;
using HAShop.Api.Utils;   // AppException
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers
{
    [ApiController]
    [Route("api/loyalty")]
    public sealed class LoyaltyController : ControllerBase
    {
        private readonly IGamificationService _gam;
        private readonly ILogger<LoyaltyController> _log;

        public LoyaltyController(
            IGamificationService gam,
            ILogger<LoyaltyController> log)
        {
            _gam = gam;
            _log = log;
        }

        private static long? GetUserId(ClaimsPrincipal user)
        {
            var s = user.FindFirstValue("uid")
                 ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
                 ?? user.FindFirstValue("sub");
            return long.TryParse(s, out var id) ? id : (long?)null;
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

        /// <summary>
        /// Đổi điểm lấy reward trong catalog.
        /// POST /api/loyalty/redeem
        /// Body: { "rewardId": 3, "quantity": 1 }
        /// </summary>
        [Authorize]
        [HttpPost("redeem")]
        public async Task<ActionResult<LoyaltyRedeemResponseDto>> Redeem(
            [FromBody] LoyaltyRedeemRequestDto body,
            [FromQuery] byte channel = 1,
            CancellationToken ct = default)
        {
            var uid = GetUserId(User);
            if (uid is null)
                throw new AppException("UNAUTHENTICATED");

            if (body is null || body.RewardId <= 0)
            {
                return BadRequest(new
                {
                    success = false,
                    error_code = "INVALID_REWARD_ID",
                    error_message = "RewardId không hợp lệ."
                });
            }

            var qty = body.Quantity <= 0 ? 1 : body.Quantity;
            var ip = GetClientIp(Request);
            long? deviceId = null; // sau này nếu anh có device id từ header thì bind vào đây

            var res = await _gam.RedeemRewardAsync(
                uid.Value,
                body.RewardId,
                qty,
                channel,
                deviceId,
                ip,
                ct);

            // Giữ đúng pattern các API trước: luôn 200, FE đọc res.Success / Error_Code
            return Ok(res);
        }
    }
}
