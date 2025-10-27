using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using HAShop.Api.Options;           // PaymentsFlags
using HAShop.Api.Payments;          // VnPayService, IZaloPayGateway
using HAShop.Api.Services;          // IOrderService
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Linq;

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
    private readonly IZaloPayGateway _zaloPay;

    public OrdersController(
        IOrderService orders,
        IOptions<PaymentsFlags> flags,
        VnPayService vnPay,
        ISqlConnectionFactory db,
        ILogger<OrdersController> log,
        IZaloPayGateway zaloPay
    )
    {
        _orders = orders;
        _flags = flags;
        _vnPay = vnPay;
        _db = db;
        _log = log;
        _zaloPay = zaloPay;
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

        // 1 = ZALOPAY
        if (req.Payment_Method == 1)
        {
            var orderCode = res.Order_Code;

            using (var con = _db.Create())
            {
                await con.ExecuteAsync("""
                    UPDATE dbo.tbl_orders
                    SET payment_status='Pending', payment_provider='ZALOPAY', payment_ref=@ref, updated_at=SYSDATETIME()
                    WHERE id=@id
                """, new { id = res.Order_Id, @ref = orderCode });
            }

            try
            {
                var zp = await _zaloPay.CreateOrderAsync(
                    orderCode: orderCode,
                    amountVnd: payTotal,
                    description: $"Thanh toan don {orderCode}",
                    appUser: uid.Value.ToString(),
                    clientReturnUrl: null, // dùng mặc định từ config
                    ct: ct
                );

                paymentUrl = zp.order_url;

                // Lưu app_trans_id để return truy vấn trạng thái
                using (var con = _db.Create())
                {
                    await con.ExecuteAsync("""
                        UPDATE dbo.tbl_orders
                        SET payment_ref = @ref, updated_at = SYSDATETIME()
                        WHERE id = @id
                    """, new { id = res.Order_Id, @ref = zp.app_trans_id });
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Build ZALOPAY order failed for {orderCode} amount {amount}", orderCode, payTotal);
                return BadRequest(new { code = "ZALOPAY_BUILD_FAILED", message = "Không khởi tạo được yêu cầu thanh toán." });
            }

            _log.LogInformation("Redirect to ZALOPAY: {url}", paymentUrl);
            return Ok(new CheckoutResponseDto(res.Order_Id, res.Order_Code, paymentUrl));
        }

        // 2 = VNPAY
        if (req.Payment_Method == 2)
        {
            var orderCode = res.Order_Code;

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
                paymentUrl = _vnPay.CreatePaymentUrl(orderCode, payTotal, ip, $"Thanh toan don {orderCode}");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Build VNPAY URL failed for {orderCode} amount {amount}", orderCode, payTotal);
                return BadRequest(new { code = "VNPAY_BUILD_FAILED", message = "Không khởi tạo được yêu cầu thanh toán." });
            }

            _log.LogInformation("Redirect to VNPAY: {url}", paymentUrl);
            return Ok(new CheckoutResponseDto(res.Order_Id, res.Order_Code, paymentUrl));
        }

        // COD / mặc định
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

    // POST /api/orders/switch-payment/{code}
    [Authorize]
    [HttpPost("switch-payment/{code}")]
    public async Task<ActionResult<SwitchPaymentResponse>> SwitchPayment(string code, [FromBody] SwitchPaymentRequest req, CancellationToken ct)
    {
        try
        {
            var res = await _orders.SwitchPaymentAsync(code, req.New_Method, req.Reason, ct);
            return Ok(res);
        }
        catch (InvalidOperationException ex) when (ex.Message == "ORDER_NOT_FOUND")
        {
            return NotFound(new { code = "ORDER_NOT_FOUND" });
        }
        catch (InvalidOperationException ex) when (ex.Message == "ORDER_ALREADY_PAID")
        {
            return BadRequest(new { code = "ORDER_ALREADY_PAID" });
        }
        catch (Exception ex)
        {
            // để FE show message đẹp
            return StatusCode(500, new { code = "SWITCH_PAYMENT_FAILED", message = ex.Message });
        }
    }

    // POST /api/orders/{code}/payment-link
    // FE gọi khi cần xin link thanh toán mới cho đơn đã tạo (sau khi hủy gateway trước đó)
    [Authorize]
    [HttpPost("{code}/payment-link")]
    public async Task<IActionResult> CreatePaymentLink(string code, [FromBody] CreatePayLinkDto dto, CancellationToken ct)
    {
        // 1) Đổi phương thức nếu cần (idempotent)
        var sw = await _orders.SwitchPaymentAsync(code, (byte)dto.Method, "USER_SWITCH_GATEWAY", ct);
        if (sw == null) return NotFound(new { code = "ORDER_NOT_FOUND" });

        // LƯU Ý: DTO của bạn đặt tên thuộc tính là New_Status (snake/camel khác nhau), không phải NewStatus
        if (string.Equals(sw.New_Status, "Paid", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "ORDER_ALREADY_PAID" });

        // 2) Lấy tổng tiền hiện tại
        long? orderId = null; decimal payTotalDec = 0m;
        using (var con = _db.Create())
        {
            var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                @"SELECT id, pay_total FROM dbo.tbl_orders WHERE order_code=@c",
                new { c = code }, cancellationToken: ct));
            if (row == null) return NotFound(new { code = "ORDER_NOT_FOUND" });
            orderId = (long)row.id;
            payTotalDec = (decimal)row.pay_total;
        }
        var payTotal = (long)Math.Round(payTotalDec, MidpointRounding.AwayFromZero);

        // 3) Tạo link theo method
        string paymentUrl;
        if (dto.Method == 1)
        {
            // ZaloPay
            try
            {
                var zp = await _zaloPay.CreateOrderAsync(
                    orderCode: code,
                    amountVnd: payTotal,
                    description: $"Thanh toan don {code}",
                    appUser: (GetUserId(User) ?? 0).ToString(),
                    clientReturnUrl: null,
                    ct: ct
                );
                paymentUrl = zp.order_url;

                using (var con = _db.Create())
                {
                    await con.ExecuteAsync("""
                        UPDATE dbo.tbl_orders
                        SET payment_status='Pending', payment_provider='ZALOPAY', payment_ref=@ref, updated_at=SYSDATETIME()
                        WHERE order_code=@code
                    """, new { code, @ref = zp.app_trans_id });
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Create ZALOPAY link failed for {code}", code);
                return StatusCode(502, new { code = "PAYLINK_CREATE_FAILED", message = "ZaloPay create order failed." });
            }
        }
        else if (dto.Method == 2)
        {
            // VNPay
            try
            {
                var ip = GetClientIp(Request) ?? "127.0.0.1";
                if (ip.Contains(":")) ip = "127.0.0.1";
                paymentUrl = _vnPay.CreatePaymentUrl(code, payTotal, ip, $"Thanh toan don {code}");

                using (var con = _db.Create())
                {
                    await con.ExecuteAsync("""
                        UPDATE dbo.tbl_orders
                        SET payment_status='Pending', payment_provider='VNPAY', payment_ref=@ref, updated_at=SYSDATETIME()
                        WHERE order_code=@code
                    """, new { code, @ref = code });
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Create VNPAY link failed for {code}", code);
                return StatusCode(502, new { code = "PAYLINK_CREATE_FAILED", message = "VNPay create link failed." });
            }
        }
        else
        {
            return BadRequest(new { code = "UNSUPPORTED_METHOD" });
        }

        return Ok(new { payment_Url = paymentUrl });
    }

    public sealed class CreatePayLinkDto
    {
        public int Method { get; set; } // 1 = ZaloPay, 2 = VNPay
    }
}
