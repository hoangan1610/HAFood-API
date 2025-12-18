using System.Threading;
using System.Threading.Tasks;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("api/articles")]
    public sealed class ArticlesPublicController : ControllerBase
    {
        private readonly IArticlePublicService _svc;
        private readonly IArticleMetricsService _metrics;

        public ArticlesPublicController(IArticlePublicService svc, IArticleMetricsService metrics)
        {
            _svc = svc;
            _metrics = metrics;
        }

        [HttpGet("{slug}")]
        public async Task<IActionResult> GetBySlug(string slug, CancellationToken ct)
        {
            var dto = await _svc.GetBySlugAsync(slug, ct);
            if (dto == null) return NotFound(new { code = "ARTICLE_NOT_FOUND" });

            // tracking không làm hỏng response nếu lỗi
            try { await _metrics.TrackArticleViewAsync(dto.Id, HttpContext, ct); } catch { /* log nếu muốn */ }

            return Ok(dto);
        }

        [HttpGet]
        public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default)
        {
            var (items, total) = await _svc.ListAsync(q, page, pageSize, ct);
            return Ok(new { page, pageSize, total, items });
        }

        // POST /api/articles/{articleId}/products/{productId}/click?variantId=...
        [HttpPost("{articleId:long}/products/{productId:long}/click")]
        public async Task<IActionResult> TrackClick(long articleId, long productId, [FromQuery] long? variantId = null, CancellationToken ct = default)
        {
            await _metrics.TrackArticleProductClickAsync(articleId, productId, variantId, ct);
            return Ok(new { ok = true });
        }
    }
}
