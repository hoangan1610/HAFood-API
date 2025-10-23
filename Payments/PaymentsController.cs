// HAShop.Api/Controllers/PaymentsController.cs
using System;
using System.Threading.Tasks;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.Options;     // PaymentsFlags, FrontendOptions
using HAShop.Api.Payments;    // VnPayService
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

[ApiController]
public class PaymentsController : ControllerBase
{
    private readonly VnPayService _vnPay;
    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<PaymentsController> _log;
    private readonly IOptions<PaymentsFlags> _flags;
    private readonly IOptions<FrontendOptions> _fe;  // ⬅ FE base url

    public PaymentsController(
        VnPayService vnPay,
        ISqlConnectionFactory db,
        ILogger<PaymentsController> log,
        IOptions<PaymentsFlags> flags,
        IOptions<FrontendOptions> fe           // ⬅ inject FE options
    )
    {
        _vnPay = vnPay;
        _db = db;
        _log = log;
        _flags = flags;
        _fe = fe;
    }

    // =========================
    // USER RETURN (UI)
    // =========================
    [HttpGet("payment/vnpay-return")]
    public async Task<IActionResult> VnPayReturn()
    {
        _log.LogInformation("VNPAY RETURN query: {q}", Request.QueryString.Value);

        // Validate chữ ký
        if (!_vnPay.ValidateSignature(Request.Query))
        {
            _log.LogWarning("VNPAY RETURN invalid signature");
            return Redirect(ComposeFeUrl("/CartPage/ThankYou.aspx?fail=1"));
        }

        var rspCode = (string?)Request.Query["vnp_ResponseCode"]; // "00" success
        var orderCode = (string?)Request.Query["vnp_TxnRef"];       // order_code

        // (TÙY CHỌN) chốt luôn ở RETURN khi bật cờ để test nhanh (không đối chiếu amount)
        if (_flags.Value.ConfirmOnReturnIfOk &&
            rspCode == "00" &&
            !string.IsNullOrWhiteSpace(orderCode))
        {
            await ConfirmPaymentAsync(orderCode, amountVnd: null, forceWhenReturn: true);
        }

        var url = string.IsNullOrWhiteSpace(orderCode)
            ? ComposeFeUrl("/CartPage/ThankYou.aspx")
            : ComposeFeUrl($"/CartPage/ThankYou.aspx?code={Uri.EscapeDataString(orderCode)}" + (rspCode == "00" ? "" : "&fail=1"));

        return Redirect(url);
    }

    // =========================
    // IPN (SERVER-TO-SERVER)
    // =========================
    [HttpGet("payment/vnpay-ipn")]
    public async Task<IActionResult> VnPayIpn()
    {
        _log.LogInformation("VNPAY IPN query: {q}", Request.QueryString.Value);

        if (!_vnPay.ValidateSignature(Request.Query))
        {
            _log.LogWarning("VNPAY IPN invalid signature");
            return Ok(new { RspCode = "97", Message = "Invalid signature" });
        }

        var rspCode = (string?)Request.Query["vnp_ResponseCode"];
        var orderCode = (string?)Request.Query["vnp_TxnRef"];
        var amountRaw = (string?)Request.Query["vnp_Amount"]; // *100

        if (string.IsNullOrWhiteSpace(orderCode))
            return Ok(new { RspCode = "01", Message = "Order not found" });

        // vnp_Amount là VND * 100 -> về VND
        long? amountVnd = null;
        if (long.TryParse(amountRaw, out var paidRaw))
            amountVnd = paidRaw / 100;

        if (rspCode == "00")
        {
            var ok = await ConfirmPaymentAsync(orderCode, amountVnd, forceWhenReturn: false);
            if (!ok)
                return Ok(new { RspCode = "04", Message = "Invalid amount or order state" });

            _log.LogInformation("VNPAY IPN: set Paid for {code}", orderCode);
            return Ok(new { RspCode = "00", Message = "Confirm success" });
        }
        else
        {
            using var con = _db.Create();
            await con.ExecuteAsync("""
                UPDATE dbo.tbl_orders
                SET payment_status='Failed', updated_at=SYSDATETIME()
                WHERE order_code=@c
            """, new { c = orderCode });

            _log.LogInformation("VNPAY IPN: set Failed for {code}, rspCode={rsp}", orderCode, rspCode);
            return Ok(new { RspCode = "00", Message = "Confirm fail recorded" });
        }
    }

    // =========================
    // Helper: xác nhận thanh toán thành công (idempotent)
    // =========================
    private async Task<bool> ConfirmPaymentAsync(string orderCode, long? amountVnd, bool forceWhenReturn)
    {
        using var con = _db.Create();

        var o = await con.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT id, pay_total, payment_status FROM dbo.tbl_orders WHERE order_code=@c",
            new { c = orderCode });

        if (o == null) return false;

        // Idempotent
        if (string.Equals((string?)o.payment_status, "Paid", StringComparison.OrdinalIgnoreCase))
            return true;

        // So khớp số tiền nếu có (IPN)
        if (amountVnd.HasValue)
        {
            var expected = (long)Math.Round((decimal)o.pay_total, MidpointRounding.AwayFromZero);
            if (expected != amountVnd.Value)
            {
                await con.ExecuteAsync("""
                    INSERT dbo.tbl_payment_transaction(order_id, provider, method, status, amount, currency, transaction_id, merchant_ref, error_code, error_message, created_at, updated_at)
                    VALUES ((SELECT id FROM dbo.tbl_orders WHERE order_code=@code),
                            'VNPAY', 2, 0, @amount, 'VND', @tx, @code, 'AMOUNT_MISMATCH', 'Amount mismatch (return/ipn)', SYSDATETIME(), SYSDATETIME())
                """, new { code = orderCode, amount = amountVnd.Value, tx = Guid.NewGuid().ToString("N") });

                _log.LogWarning("ConfirmPayment: amount mismatch for {code}. expected={expected} paid={paid}",
                    orderCode, expected, amountVnd.Value);
                return false;
            }
        }
        else if (!forceWhenReturn)
        {
            // RETURN không có amount & không ép chốt -> bỏ qua
            return false;
        }

        // Ghi transaction success
        await con.ExecuteAsync("""
            INSERT dbo.tbl_payment_transaction(order_id, provider, method, status, amount, currency, transaction_id, merchant_ref, paid_at, created_at, updated_at)
            VALUES ((SELECT id FROM dbo.tbl_orders WHERE order_code=@code),
                    'VNPAY', 2, 1, COALESCE(@amount, (SELECT CAST(ROUND(pay_total,0) AS BIGINT) FROM dbo.tbl_orders WHERE order_code=@code)),
                    'VND', @tx, @code, SYSDATETIME(), SYSDATETIME(), SYSDATETIME())
        """, new { code = orderCode, amount = amountVnd, tx = Guid.NewGuid().ToString("N") });

        // Update đơn -> Paid
        await con.ExecuteAsync("""
            UPDATE dbo.tbl_orders
            SET payment_status = 'Paid',
                paid_at = SYSDATETIME(),
                updated_at = SYSDATETIME()
            WHERE order_code = @code
        """, new { code = orderCode });

        return true;
    }

    // =========================
    // Helper: ghép URL redirect về FE theo cấu hình
    // =========================
    private string ComposeFeUrl(string pathAndQuery)
    {
        var baseUrl = _fe?.Value?.BaseUrl?.TrimEnd('/') ?? "";
        if (string.IsNullOrEmpty(baseUrl))
        {
            // fallback: dùng relative path (áp dụng nếu API và FE cùng host trong IIS)
            return pathAndQuery.StartsWith("/") ? pathAndQuery : "/" + pathAndQuery;
        }
        if (!pathAndQuery.StartsWith("/")) pathAndQuery = "/" + pathAndQuery;
        return baseUrl + pathAndQuery;
    }
}
