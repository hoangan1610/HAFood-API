using System;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using HAShop.Api.Options;
using HAShop.Api.Payments;
using HAShop.Api.Services;
using HAShop.Api.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;
    private readonly IOptions<PaymentsFlags> _flags;
    private readonly Pay2SService _pay2s;
    private readonly MomoService _momo;
    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<OrdersController> _log;

    private const int METHOD_MOMO = 1;
    private const int METHOD_PAY2S = 2;

    private const string PROV_MOMO = "MOMO";
    private const string PROV_PAY2S = "PAY2S";

    public OrdersController(
        IOrderService orders,
        IOptions<PaymentsFlags> flags,
        Pay2SService pay2s,
        MomoService momo,
        ISqlConnectionFactory db,
        ILogger<OrdersController> log
    )
    {
        _orders = orders;
        _flags = flags;
        _pay2s = pay2s;
        _momo = momo;
        _db = db;
        _log = log;
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

    // =========================================================
    // Helper: set Pending + provider/ref + log tx create (status=0)
    // =========================================================
    private async Task<(bool ok, string? errCode)> MarkPendingAndLogCreateAsync(
        string orderCode,
        long amountVnd,
        string provider,
        int method,
        string paymentRef,
        string transactionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) return (false, "ORDER_CODE_EMPTY");
        if (amountVnd <= 0) return (false, "AMOUNT_INVALID");

        using var con = _db.Create();
        if (con is DbConnection dbc) await dbc.OpenAsync(ct);
        else con.Open();

        using var tx = con.BeginTransaction();

        try
        {
            var o = await con.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                """
                SELECT id, pay_total, payment_status
                FROM dbo.tbl_orders WITH (UPDLOCK, HOLDLOCK)
                WHERE order_code=@c
                """,
                new { c = orderCode },
                transaction: tx,
                cancellationToken: ct,
                commandTimeout: 15));

            if (o == null)
            {
                tx.Rollback();
                return (false, "ORDER_NOT_FOUND");
            }

            long orderId = (long)o.id;
            decimal payTotal = (decimal)o.pay_total;
            string? payStatus = (string?)o.payment_status;

            if (string.Equals(payStatus, "Paid", StringComparison.OrdinalIgnoreCase))
            {
                tx.Rollback();
                return (false, "ORDER_ALREADY_PAID");
            }

            var expected = (long)Math.Round(payTotal, MidpointRounding.AwayFromZero);
            if (expected != amountVnd)
            {
                await con.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT dbo.tbl_payment_transaction
                        (order_id, provider, method, status, amount, currency, transaction_id, merchant_ref,
                         error_code, error_message, created_at, updated_at)
                    VALUES
                        (@oid, @prov, @meth, 0, CAST(@amt AS decimal(12,2)), 'VND', @txid, @mref,
                         'AMOUNT_MISMATCH', 'Create request amount mismatch', SYSDATETIME(), SYSDATETIME())
                    """,
                    new
                    {
                        oid = orderId,
                        prov = provider,
                        meth = method,
                        amt = amountVnd,
                        txid = string.IsNullOrWhiteSpace(transactionId) ? Guid.NewGuid().ToString("N") : transactionId,
                        mref = orderCode
                    },
                    transaction: tx,
                    cancellationToken: ct,
                    commandTimeout: 15));

                tx.Commit();
                return (false, "AMOUNT_MISMATCH");
            }

            await con.ExecuteAsync(new CommandDefinition(
                """
                UPDATE dbo.tbl_orders
                SET payment_status='Pending',
                    payment_provider=@prov,
                    payment_ref=@pref,
                    updated_at=SYSDATETIME()
                WHERE id=@id
                """,
                new { id = orderId, prov = provider, pref = paymentRef },
                transaction: tx,
                cancellationToken: ct,
                commandTimeout: 15));

            await con.ExecuteAsync(new CommandDefinition(
                """
                INSERT dbo.tbl_payment_transaction
                    (order_id, provider, method, status, amount, currency, transaction_id, merchant_ref,
                     error_code, error_message, created_at, updated_at)
                VALUES
                    (@oid, @prov, @meth, 0, CAST(@amt AS decimal(12,2)), 'VND', @txid, @mref,
                     'CREATE_REQUEST', 'Created payment request', SYSDATETIME(), SYSDATETIME())
                """,
                new
                {
                    oid = orderId,
                    prov = provider,
                    meth = method,
                    amt = expected,
                    txid = string.IsNullOrWhiteSpace(transactionId) ? Guid.NewGuid().ToString("N") : transactionId,
                    mref = orderCode
                },
                transaction: tx,
                cancellationToken: ct,
                commandTimeout: 15));

            tx.Commit();
            return (true, null);
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            _log.LogError(ex, "MarkPendingAndLogCreateAsync failed code={code} prov={prov}", orderCode, provider);
            return (false, "DB_ERROR");
        }
    }

    // =========================================================
    // POST /api/orders/checkout
    // =========================================================
    [Authorize]
    [HttpPost("checkout")]
    public async Task<ActionResult<CheckoutResponseDto>> Checkout([FromBody] PlaceOrderRequest req, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) throw new AppException("UNAUTHENTICATED");

        if (string.IsNullOrWhiteSpace(req.Ip))
            req = req with { Ip = GetClientIp(Request) ?? "" };

        var res = await _orders.PlaceFromCartAsync(uid.Value, req, ct);

        var detail = await _orders.GetAsync(res.Order_Id, ct);
        var payTotal = (long)Math.Round(detail?.Header.Pay_Total ?? 0m, MidpointRounding.AwayFromZero);

        string? paymentUrl = null;

        if (req.Payment_Method == METHOD_MOMO)
        {
            var orderCode = res.Order_Code;

            try
            {
                var momo = await _momo.CreatePaymentAsync(
                    orderCode: orderCode,
                    amountVnd: payTotal,
                    orderInfo: $"Thanh toan don {orderCode}",
                    extraDataBase64: "",
                    ct: ct
                );

                paymentUrl = momo.payUrl;

                var mk = await MarkPendingAndLogCreateAsync(
                    orderCode: orderCode,
                    amountVnd: payTotal,
                    provider: PROV_MOMO,
                    method: METHOD_MOMO,
                    paymentRef: momo.requestId,
                    transactionId: momo.requestId,
                    ct: ct
                );

                if (!mk.ok)
                    throw new AppException(mk.errCode ?? "MOMO_MARK_PENDING_FAILED");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Build MOMO order failed for {orderCode} amount {amount}", orderCode, payTotal);
                throw new AppException("MOMO_BUILD_FAILED", "Không khởi tạo được yêu cầu thanh toán.", ex);
            }

            return Ok(new CheckoutResponseDto(res.Order_Id, res.Order_Code, paymentUrl));
        }

        if (req.Payment_Method == METHOD_PAY2S)
        {
            var orderCode = res.Order_Code;

            try
            {
                var p2 = await _pay2s.CreatePaymentUrlAsync(orderCode, payTotal, ct);
                paymentUrl = p2.payUrl;

                var mk = await MarkPendingAndLogCreateAsync(
                    orderCode: orderCode,
                    amountVnd: payTotal,
                    provider: PROV_PAY2S,
                    method: METHOD_PAY2S,
                    paymentRef: p2.orderId,        // ✅ providerOrderId (quan trọng)
                    transactionId: p2.requestId,   // ✅ log requestId cho dễ trace
                    ct: ct
                );

                if (!mk.ok)
                    throw new AppException(mk.errCode ?? "PAY2S_MARK_PENDING_FAILED");

            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Build PAY2S URL failed for {orderCode} amount {amount}", orderCode, payTotal);
                throw new AppException("PAY2S_BUILD_FAILED", "Không khởi tạo được yêu cầu thanh toán.", ex);
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

    // GET /api/orders?status=&order_code=&page=&page_size=
    [Authorize]
    [HttpGet]
    public async Task<ActionResult<OrdersPageDto>> MyOrders(
        [FromQuery] byte? status,
        [FromQuery(Name = "order_code")] string? orderCode,
        [FromQuery] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        CancellationToken ct = default)
    {
        var uid = GetUserId(User);
        if (uid is null) throw new AppException("UNAUTHENTICATED");

        var res = await _orders.ListByUserAsync(uid.Value, status, orderCode, page, pageSize, ct);
        return Ok(res);
    }

    // =========================================================
    // ✅ Admin update status (các trạng thái)
    // =========================================================

    [HttpPost("{id:long}/status")]
    public async Task<ActionResult> UpdateStatus(long id, [FromBody] byte newStatus, CancellationToken ct)
    {
        var ok = await _orders.UpdateStatusAsync(id, newStatus, ct);
        return ok ? Ok() : NotFound(new { code = "ORDER_NOT_FOUND" });
    }

    // =========================================================
    // ✅ Admin/Shipper báo đã giao (status=3)
    // =========================================================
    [Authorize(Roles = "Admin")]
    [HttpPost("{id:long}/report-delivered")]
    public async Task<ActionResult> ReportDelivered(long id, CancellationToken ct)
    {
        var ok = await _orders.ReportDeliveredAsync(id, ct);
        return ok ? Ok(new { ok = true }) : NotFound(new { code = "ORDER_NOT_FOUND" });
    }

    // =========================================================
    // ✅ User xác nhận đã nhận hàng (status=7) -> cộng điểm + mission
    // =========================================================
    [Authorize]
    [HttpPost("{id:long}/confirm-received")]
    public async Task<ActionResult> ConfirmReceived(long id, CancellationToken ct)
    {
        var uid = GetUserId(User);
        if (uid is null) throw new AppException("UNAUTHENTICATED");

        var ok = await _orders.ConfirmReceivedAsync(id, uid.Value, ct);
        return ok ? Ok(new { ok = true }) : BadRequest(new { code = "ORDER_CANNOT_CONFIRM" });
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

        decimal payTotalDec;
        using (var con = _db.Create())
        {
            var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                "SELECT pay_total FROM dbo.tbl_orders WHERE order_code=@c",
                new { c = code }, cancellationToken: ct));
            if (row == null) throw new AppException("ORDER_NOT_FOUND");
            payTotalDec = (decimal)row.pay_total;
        }
        var payTotal = (long)Math.Round(payTotalDec, MidpointRounding.AwayFromZero);

        string paymentUrl;

        if (dto.Method == METHOD_MOMO)
        {
            try
            {
                var momo = await _momo.CreatePaymentAsync(
                    orderCode: code,
                    amountVnd: payTotal,
                    orderInfo: $"Thanh toan don {code}",
                    extraDataBase64: "",
                    ct: ct
                );

                paymentUrl = momo.payUrl;

                var mk = await MarkPendingAndLogCreateAsync(
                    orderCode: code,
                    amountVnd: payTotal,
                    provider: PROV_MOMO,
                    method: METHOD_MOMO,
                    paymentRef: momo.requestId,
                    transactionId: momo.requestId,
                    ct: ct
                );

                if (!mk.ok)
                    throw new AppException(mk.errCode ?? "MOMO_MARK_PENDING_FAILED");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Create MOMO link failed for {code}", code);
                throw new AppException("PAYLINK_CREATE_FAILED", "MoMo create link failed.", ex);
            }
        }
        else if (dto.Method == METHOD_PAY2S)
        {
            try
            {
                var p2 = await _pay2s.CreatePaymentUrlAsync(code, payTotal, ct);
                paymentUrl = p2.payUrl;

                var mk = await MarkPendingAndLogCreateAsync(
                    orderCode: code,
                    amountVnd: payTotal,
                    provider: PROV_PAY2S,
                    method: METHOD_PAY2S,
                    paymentRef: p2.orderId,        // ✅ providerOrderId
                    transactionId: p2.requestId,   // ✅ requestId
                    ct: ct
                );

                if (!mk.ok)
                    throw new AppException(mk.errCode ?? "PAY2S_MARK_PENDING_FAILED");

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
        public int Method { get; set; } // 1 = MoMo, 2 = Pay2S
    }
}
