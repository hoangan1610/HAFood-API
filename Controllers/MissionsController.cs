using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // yêu cầu đăng nhập
    public sealed class MissionsController : ControllerBase
    {
        private readonly IMissionService _missions;

        public MissionsController(IMissionService missions)
        {
            _missions = missions;
        }

        // GET /api/missions/my
        [HttpGet("my")]
        public async Task<ActionResult<IReadOnlyList<UserMissionDto>>> GetMyMissions(CancellationToken ct)
        {
            // Lấy user_id từ claim (tuỳ anh map khi login)
            var userIdClaim = User.FindFirst("user_info_id")
                             ?? User.FindFirst(ClaimTypes.NameIdentifier);

            if (userIdClaim == null || !long.TryParse(userIdClaim.Value, out var userId))
            {
                return Unauthorized();
            }

            var missions = await _missions.GetUserMissionsAsync(userId, ct);
            return Ok(missions);
        }
    }
}
