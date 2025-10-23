using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using HAShop.Api.Options;           // PaymentsFlags
using HAShop.Api.Payments;          // VnPayService
using HAShop.Api.Services;          // IOrderService
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Microsoft.Extensions.Logging;



namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;
    private readonly IOptions<PaymentsFlags> _flags;
    private readonly VnPayService _vnPay;
    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<OrdersController> _log;

    public OrdersController(
        IOrderService orders,
        IOptions<PaymentsFlags> flags,
        VnPayService vnPay,
        ISqlConnectionFactory db,
        ILogger<OrdersController> log)
    {
        _orders = orders;
        _flags = flags;
        _vnPay = vnPay;
        _db = db;
        _log = log;
    }

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
    public async Task<ActionResult<CheckoutResponseDto>> Checkout([FromBody] PlaceOrderRequest req, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) return Unauthorized(new { code = "UNAUTHENTICATED" });
        if (string.IsNullOrWhiteSpace(req.Ip)) req = req with { Ip = GetClientIp(Request) ?? "" };

        // 1) Tạo đơn
        var res = await _orders.PlaceFromCartAsync(uid.Value, req, ct);

        // 2) Lấy tổng tiền
        var detail = await _orders.GetAsync(res.Order_Id, ct);
        var payTotal = (long)Math.Round(detail?.Header.Pay_Total ?? 0m, MidpointRounding.AwayFromZero);

        string? paymentUrl = null;

        if (req.Payment_Method == 2) // VNPAY
        {
            var orderCode = res.Order_Code;

            // optional: đánh dấu pending
            using (var con = _db.Create())
            {
                await con.ExecuteAsync("""
                    UPDATE dbo.tbl_orders
                    SET payment_status='Pending', payment_provider='VNPAY', payment_ref=@ref, updated_at=SYSDATETIME()
                    WHERE id=@id
                """, new { id = res.Order_Id, @ref = orderCode });
            }

            try
            {
                var ip = GetClientIp(Request) ?? "127.0.0.1";
                if (ip.Contains(":")) ip = "127.0.0.1"; // ép IPv4

                paymentUrl = _vnPay.CreatePaymentUrl(orderCode, payTotal, ip, $"Thanh toan don {orderCode}"
                );
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Build VNPAY URL failed for {orderCode} amount {amount}", orderCode, payTotal);
                // trả về 400 để FE hiện lỗi rõ ràng
                return BadRequest(new
                {
                    code = "VNPAY_BUILD_FAILED",
                    message = "Không khởi tạo được yêu cầu thanh toán.",
                });
            }

            if (!string.IsNullOrEmpty(paymentUrl))
            {
                _log.LogInformation("Redirect to VNPAY: {url}", paymentUrl); // giờ _log đã có
            }

            return Ok(new CheckoutResponseDto(res.Order_Id, res.Order_Code, paymentUrl));
        }
        return Ok(new CheckoutResponseDto(res.Order_Id, res.Order_Code, paymentUrl));

    }

    // GET /api/orders/{id}
    [Authorize]
    [HttpGet("{id:long}")]
    public async Task<ActionResult<OrderDetailDto>> Get(long id, CancellationToken ct)
    {
        var data = await _orders.GetAsync(id, ct);
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
        var res = await _orders.ListByUserAsync(uid.Value, status, page, pageSize, ct);
        return Ok(res);
    }

    // POST /api/orders/{id}/status
    [HttpPost("{id:long}/status")]
    public async Task<ActionResult> UpdateStatus(long id, [FromBody] byte newStatus, CancellationToken ct)
    {
        var ok = await _orders.UpdateStatusAsync(id, newStatus, ct);
        return ok ? Ok() : NotFound(new { code = "ORDER_NOT_FOUND" });
    }

    // POST /api/orders/payments
    [HttpPost("payments")]
    public async Task<ActionResult<PaymentCreateResponse>> CreatePayment([FromBody] PaymentCreateRequest req, CancellationToken ct)
    {
        var res = await _orders.CreatePaymentAsync(req, ct);
        return Created($"/api/orders/payments/{res.Payment_Id}", res);
    }
}
