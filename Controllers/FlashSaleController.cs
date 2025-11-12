using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api")]
public class FlashSaleController(IFlashSaleService svc) : ControllerBase
{
    /// <summary>
    /// Giá + countdown cho 1 biến thể (trang chi tiết SP).
    /// </summary>
    /// <remarks>channel: 1=web, 2=app... (tùy cấu hình vpo)</remarks>
    [HttpGet("variants/{id:long}/price")]
    public async Task<ActionResult<FlashSalePriceDto>> GetVariantPrice(
        long id, [FromQuery] byte? channel, CancellationToken ct)
    {
        var row = await svc.GetVariantPriceAsync(id, channel, ct);
        return row is null ? NotFound() : Ok(row);
    }

    /// <summary>
    /// Danh sách Flash Sale đang hiệu lực (landing).
    /// </summary>
    [HttpGet("flashsale/active")]
    public async Task<ActionResult<IReadOnlyList<FlashSaleActiveItemDto>>> GetActive(
        [FromQuery] byte? channel, CancellationToken ct)
    {
        var list = await svc.GetActiveAsync(channel, ct);
        return Ok(list);
    }

    /// <summary>
    /// Reserve suất (gọi lúc xác nhận mua/giữ). Tăng sold_count nếu còn suất & trong thời gian.
    /// </summary>
    [HttpPost("flashsale/reserve")]
    public async Task<ActionResult<FlashSaleReserveResponse>> Reserve(
        [FromBody] FlashSaleReserveRequest req, CancellationToken ct)
    {
        if (req.Qty <= 0)
            return BadRequest(new { code = "INVALID_QTY", message = "Số lượng phải > 0." });

        try
        {
            var res = await svc.ReserveAsync(req.Vpo_Id, req.Qty, ct);
            return Ok(res);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { code = "RESERVE_FAILED", message = ex.Message });
        }
    }

    /// <summary>
    /// Release suất (khi huỷ/ thanh toán thất bại).
    /// </summary>
    [HttpPost("flashsale/release")]
    public async Task<ActionResult<FlashSaleReleaseResponse>> Release(
        [FromBody] FlashSaleReleaseRequest req, CancellationToken ct)
    {
        if (req.Qty <= 0)
            return BadRequest(new { code = "INVALID_QTY", message = "Số lượng phải > 0." });

        try
        {
            var res = await svc.ReleaseAsync(req.Vpo_Id, req.Qty, ct);
            return Ok(res);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { code = "RELEASE_FAILED", message = ex.Message });
        }
    }
}
