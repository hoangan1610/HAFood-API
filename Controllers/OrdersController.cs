// Controllers/OrdersController.cs
using System.Security.Claims;
using System.Linq;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using HAShop.Api.Options;           // PaymentsFlags
using HAShop.Api.Payments;          // Pay2SService, IZaloPayGateway
using HAShop.Api.Services;          // IOrderService
using HAShop.Api.Utils;             // AppException
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;
    private readonly IOptions<PaymentsFlags> _flags;
    private readonly Pay2SService _pay2s;     // ✅ thay VnPayService
    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<OrdersController> _log;
    private readonly IZaloPayGateway _zaloPay;

    public OrdersController(
        IOrderService orders,
        IOptions<PaymentsFlags> flags,
        Pay2SService pay2s,                 // ✅ thay VnPayService
        ISqlConnectionFactory db,
        ILogger<OrdersController> log,
        IZaloPayGateway zaloPay
    )
    {
        _orders = orders;
        _flags = flags;
        _pay2s = pay2s;                     // ✅
        _db = db;
        _log = log;
        _zaloPay = zaloPay;
    }

    private static long? GetUserId(ClaimsPrincipal user)
    {
        var s = user.FindFirstValue("uid")
             ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
             ?? user.FindFirstValue("sub");
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
        if (uid is null) throw new AppException("UNAUTHENTICATED");

        if (string.IsNullOrWhiteSpace(req.Ip))
            req = req with { Ip = GetClientIp(Request) ?? "" };

        // 1) Tạo đơn
        var res = await _orders.PlaceFromCartAsync(uid.Value, req, ct);

        // 2) Lấy tổng tiền để build link
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
                    clientReturnUrl: null,
                    ct: ct
                );

                paymentUrl = zp.order_url;

                using var con = _db.Create();
                await con.ExecuteAsync("""
                    UPDATE dbo.tbl_orders
                    SET payment_status='Pending', payment_provider='ZALOPAY', payment_ref=@ref, updated_at=SYSDATETIME()
                    WHERE id=@id
                """, new { id = res.Order_Id, @ref = zp.app_trans_id });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Build ZALOPAY order failed for {orderCode} amount {amount}", orderCode, payTotal);
                throw new AppException("ZALOPAY_BUILD_FAILED", "Không khởi tạo được yêu cầu thanh toán.", ex);
            }

            return Ok(new CheckoutResponseDto(res.Order_Id, res.Order_Code, paymentUrl));
        }

        // 2 = (slot cũ VNPAY) -> ✅ PAY2S
        if (req.Payment_Method == 2)
        {
            var orderCode = res.Order_Code;

            using (var con = _db.Create())
            {
                await con.ExecuteAsync("""
                    UPDATE dbo.tbl_orders
                    SET payment_status='Pending', payment_provider='PAY2S', payment_ref=@ref, updated_at=SYSDATETIME()
                    WHERE id=@id
                """, new { id = res.Order_Id, @ref = orderCode });
            }

            try
            {
                // Pay2S amount = VND (không *100). orderId = orderCode
                paymentUrl = await _pay2s.CreatePaymentUrlAsync(orderCode, payTotal, ct);

                // (optional) nếu bạn muốn lưu requestId riêng thì cần parse response create,
                // hiện tại cứ lưu orderCode là đủ để đối soát IPN/return.
                using var con = _db.Create();
                await con.ExecuteAsync("""
                    UPDATE dbo.tbl_orders
                    SET payment_status='Pending', payment_provider='PAY2S', payment_ref=@ref, updated_at=SYSDATETIME()
                    WHERE id=@id
                """, new { id = res.Order_Id, @ref = orderCode });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Build PAY2S URL failed for {orderCode} amount {amount}", orderCode, payTotal);
                throw new AppException("PAY2S_BUILD_FAILED", "Không khởi tạo được yêu cầu thanh toán.", ex);
            }

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
        if (uid is null) throw new AppException("UNAUTHENTICATED");

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
        var res = await _orders.SwitchPaymentAsync(code, req.New_Method, req.Reason, ct);
        return Ok(res);
    }

    // POST /api/orders/{code}/payment-link
    [Authorize]
    [HttpPost("{code}/payment-link")]
    public async Task<IActionResult> CreatePaymentLink(string code, [FromBody] CreatePayLinkDto dto, CancellationToken ct)
    {
        var sw = await _orders.SwitchPaymentAsync(code, (byte)dto.Method, "USER_SWITCH_GATEWAY", ct);

        if (string.Equals(sw.New_Status, "Paid", StringComparison.OrdinalIgnoreCase))
            throw new AppException("ORDER_ALREADY_PAID");

        long orderId; decimal payTotalDec;
        using (var con = _db.Create())
        {
            var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                "SELECT id, pay_total FROM dbo.tbl_orders WHERE order_code=@c",
                new { c = code }, cancellationToken: ct));
            if (row == null) throw new AppException("ORDER_NOT_FOUND");
            orderId = (long)row.id;
            payTotalDec = (decimal)row.pay_total;
        }
        var payTotal = (long)Math.Round(payTotalDec, MidpointRounding.AwayFromZero);

        string paymentUrl;

        if (dto.Method == 1)
        {
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

                using var con = _db.Create();
                await con.ExecuteAsync("""
                    UPDATE dbo.tbl_orders
                    SET payment_status='Pending', payment_provider='ZALOPAY', payment_ref=@ref, updated_at=SYSDATETIME()
                    WHERE order_code=@code
                """, new { code, @ref = zp.app_trans_id });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Create ZALOPAY link failed for {code}", code);
                throw new AppException("PAYLINK_CREATE_FAILED", "ZaloPay create order failed.", ex);
            }
        }
        else if (dto.Method == 2)
        {
            // ✅ PAY2S (slot cũ VNPAY)
            try
            {
                paymentUrl = await _pay2s.CreatePaymentUrlAsync(code, payTotal, ct);

                using var con = _db.Create();
                await con.ExecuteAsync("""
                    UPDATE dbo.tbl_orders
                    SET payment_status='Pending', payment_provider='PAY2S', payment_ref=@ref, updated_at=SYSDATETIME()
                    WHERE order_code=@code
                """, new { code, @ref = code });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Create PAY2S link failed for {code}", code);
                throw new AppException("PAYLINK_CREATE_FAILED", "Pay2S create link failed.", ex);
            }
        }
        else
        {
            throw new AppException("UNSUPPORTED_METHOD");
        }

        return Ok(new { payment_url = paymentUrl });
    }

    public sealed class CreatePayLinkDto
    {
        public int Method { get; set; } // 1 = ZaloPay, 2 = Pay2S(slot VNPAY cũ)
    }
}
