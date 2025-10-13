using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController(IOrderService orders) : ControllerBase
{
    private static long? GetUserId(ClaimsPrincipal user)
    {
        var s = user.FindFirstValue("uid") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(s, out var id) ? id : null;
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

    // POST /api/orders/checkout
    [Authorize]
    [HttpPost("checkout")]
    public async Task<ActionResult<PlaceOrderResponse>> Checkout([FromBody] PlaceOrderRequest req, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized(new { code = "UNAUTHENTICATED" });

        // nếu FE không gửi IP thì backend tự điền
        if (string.IsNullOrWhiteSpace(req.Ip))
        {
            req = req with { Ip = GetClientIp(Request) ?? "" };
        }

        try
        {
            var res = await orders.PlaceFromCartAsync(uid.Value, req, ct);
            return Created($"/api/orders/{res.Order_Id}", res);
        }
        catch (InvalidOperationException ex) when (ex.Message == "CART_NOT_FOUND")
        { return NotFound(new { code = "CART_NOT_FOUND", message = "Không tìm thấy giỏ hàng." }); }
        catch (InvalidOperationException ex) when (ex.Message == "CART_EMPTY")
        { return BadRequest(new { code = "CART_EMPTY", message = "Giỏ hàng trống." }); }
        catch (InvalidOperationException ex) when (ex.Message == "OUT_OF_STOCK")
        { return BadRequest(new { code = "OUT_OF_STOCK", message = "Một số sản phẩm đã hết hàng." }); }
        // Nếu bạn có bắt lỗi promo ở service thì thêm catch tương tự ở đây.
    }

    // GET /api/orders/{id}
    [Authorize]
    [HttpGet("{id:long}")]
    public async Task<ActionResult<OrderDetailDto>> Get(long id, CancellationToken ct)
    {
        var data = await orders.GetAsync(id, ct);
        return data is null ? NotFound() : Ok(data);
    }

    // GET /api/orders?status=&page=&page_size=
    [Authorize]
    [HttpGet]
    public async Task<ActionResult<OrdersPageDto>> MyOrders(
        [FromQuery] byte? status,
        [FromQuery] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized(new { code = "UNAUTHENTICATED" });
        var res = await orders.ListByUserAsync(uid.Value, status, page, pageSize, ct);
        return Ok(res);
    }

    // POST /api/orders/{id}/status  (tuỳ bạn thêm [Authorize(Roles="admin")])
    [HttpPost("{id:long}/status")]
    public async Task<ActionResult> UpdateStatus(long id, [FromBody] byte newStatus, CancellationToken ct)
    {
        var ok = await orders.UpdateStatusAsync(id, newStatus, ct);
        return ok ? Ok() : NotFound(new { code = "ORDER_NOT_FOUND" });
    }

    // POST /api/orders/payments
    [HttpPost("payments")]
    public async Task<ActionResult<PaymentCreateResponse>> CreatePayment([FromBody] PaymentCreateRequest req, CancellationToken ct)
    {
        var res = await orders.CreatePaymentAsync(req, ct);
        return Created($"/api/orders/payments/{res.Payment_Id}", res);
    }
}
