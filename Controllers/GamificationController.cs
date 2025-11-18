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
    [Route("api/gam")]
    public sealed class GamificationController : ControllerBase
    {
        private readonly IGamificationService _gam;
        private readonly ILogger<GamificationController> _log;

        public GamificationController(
            IGamificationService gam,
            ILogger<GamificationController> log)
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
        /// Xem điểm thành viên + streak hiện tại.
        /// </summary>
        [Authorize]
        [HttpGet("loyalty")]
        public async Task<ActionResult<LoyaltySummaryDto>> GetLoyaltySummary(CancellationToken ct)
        {
            var uid = GetUserId(User);
            if (uid is null) throw new AppException("UNAUTHENTICATED");

            var dto = await _gam.GetLoyaltySummaryAsync(uid.Value, ct);
            return Ok(dto);
        }

        /// <summary>
        /// Điểm danh (mỗi ngày 1 lần), nhận điểm + lượt quay.
        /// </summary>
        [Authorize]
        [HttpPost("checkin")]
        public async Task<ActionResult<GamCheckinResponseDto>> Checkin(
            [FromQuery] byte channel = 1,
            CancellationToken ct = default)
        {
            var uid = GetUserId(User);
            if (uid is null) throw new AppException("UNAUTHENTICATED");

            var ip = GetClientIp(Request);
            long? deviceId = null; // sau này bạn có thể bind từ header / token

            var res = await _gam.CheckinAsync(uid.Value, channel, deviceId, ip, ct);
            return Ok(res); // Success=false thì FE tự đọc Error_Code / Error_Message
        }

        /// <summary>
        /// Danh sách lượt quay của user (available + used).
        /// FE có thể filter status=0 để chỉ show lượt chưa dùng.
        /// </summary>
        [Authorize]
        [HttpGet("spins")]
        public async Task<ActionResult<IReadOnlyList<GamSpinTurnDto>>> GetSpins(CancellationToken ct)
        {
            var uid = GetUserId(User);
            if (uid is null) throw new AppException("UNAUTHENTICATED");

            var list = await _gam.GetSpinsAsync(uid.Value, ct);
            return Ok(list);
        }

        /// <summary>
        /// Thực hiện quay 1 lượt cụ thể.
        /// </summary>
        [Authorize]
        [HttpPost("spins/{id:long}/roll")]
        public async Task<ActionResult<GamSpinRollResponseDto>> Roll(
            long id,
            [FromQuery] byte? channel,
            CancellationToken ct)
        {
            var uid = GetUserId(User);
            if (uid is null) throw new AppException("UNAUTHENTICATED");

            var ip = GetClientIp(Request);
            long? deviceId = null;

            var res = await _gam.SpinAsync(uid.Value, id, channel, deviceId, ip, ct);
            return Ok(res);
        }

        // GET /api/gam/spin-config/active?channel=1
        /// <summary>
        /// Lấy config vòng quay đang active + danh sách ô (để FE vẽ vòng quay).
        /// Không yêu cầu login.
        /// </summary>
        [HttpGet("spin-config/active")]
        [AllowAnonymous]
        public async Task<ActionResult<GamSpinConfigDto>> GetActiveSpinConfig(
            [FromQuery] byte? channel,
            CancellationToken ct)
        {
            var cfg = await _gam.GetActiveSpinConfigAsync(channel, ct);
            return cfg is null ? NotFound(new { code = "NO_SPIN_CONFIG" }) : Ok(cfg);
        }

    }


}
