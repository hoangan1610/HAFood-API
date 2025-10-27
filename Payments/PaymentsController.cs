// HAShop.Api/Controllers/PaymentsController.cs
using System.Text;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.Options;
using HAShop.Api.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

[ApiController]
public class PaymentsController : ControllerBase
{
    private readonly VnPayService _vnPay;
    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<PaymentsController> _log;
    private readonly IOptions<PaymentsFlags> _flags;
    private readonly IOptions<FrontendOptions> _fe;

    public PaymentsController(
        VnPayService vnPay,
        ISqlConnectionFactory db,
        ILogger<PaymentsController> log,
        IOptions<PaymentsFlags> flags,
        IOptions<FrontendOptions> fe)
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

        if (!_vnPay.ValidateSignature(Request.Query))
        {
            _log.LogWarning("VNPAY RETURN invalid signature");
            // ↩ quay về trang checkout và báo lỗi
            return Redirect(ComposeFeUrl("/CartPage/CheckoutConfirm.aspx?payfail=1"));
        }

        var rspCode = (string?)Request.Query["vnp_ResponseCode"]; // "00" = success
        var orderCode = (string?)Request.Query["vnp_TxnRef"];

        if (_flags.Value.ConfirmOnReturnIfOk &&
            rspCode == "00" &&
            !string.IsNullOrWhiteSpace(orderCode))
        {
            await ConfirmPaymentAsync(orderCode, amountVnd: null, provider: "VNPAY", method: 2, forceWhenReturn: true);

        }

        // ✅ SỬA: nếu thành công → ThankYou; nếu HỦY/FAIL → quay về CheckoutConfirm với payfail=1
        string url;
        if (rspCode == "00")
        {
            url = string.IsNullOrWhiteSpace(orderCode)
                ? ComposeFeUrl("/CartPage/ThankYou.aspx")
                : ComposeFeUrl($"/CartPage/ThankYou.aspx?code={Uri.EscapeDataString(orderCode)}");
        }
        else
        {
            // có orderCode thì đính kèm, để FE có thể hiển thị mã đơn (tuỳ bạn)
            url = string.IsNullOrWhiteSpace(orderCode)
                ? ComposeFeUrl("/CartPage/CheckoutConfirm.aspx?payfail=1")
                : ComposeFeUrl($"/CartPage/CheckoutConfirm.aspx?payfail=1&code={Uri.EscapeDataString(orderCode)}");
        }

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
            var ok = await ConfirmPaymentAsync(orderCode, amountVnd, provider: "VNPAY", method: 2, forceWhenReturn: false);

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
    // =========================
    // Helper: xác nhận thanh toán thành công (idempotent)
    // =========================
    private async Task<bool> ConfirmPaymentAsync(string orderCode, long? amountVnd, string provider, int method, bool forceWhenReturn)
    {
        using var con = _db.Create();
        var o = await con.QueryFirstOrDefaultAsync<dynamic>("SELECT id, pay_total, payment_status FROM dbo.tbl_orders WHERE order_code=@c", new { c = orderCode });
        if (o == null) return false;
        if (string.Equals((string?)o.payment_status, "Paid", StringComparison.OrdinalIgnoreCase)) return true;

        if (amountVnd.HasValue)
        {
            var expected = (long)Math.Round((decimal)o.pay_total, MidpointRounding.AwayFromZero);
            if (expected != amountVnd.Value)
            {
                await con.ExecuteAsync("""
                    INSERT dbo.tbl_payment_transaction(order_id, provider, method, status, amount, currency, transaction_id, merchant_ref, 
                        error_code, error_message, created_at, updated_at)
                    VALUES ((SELECT id FROM dbo.tbl_orders WHERE order_code=@code),
                            @prov, @meth, 0, @amount, 'VND', @tx, @code, 'AMOUNT_MISMATCH', 'Amount mismatch (return/ipn)', SYSDATETIME(), SYSDATETIME())
                """, new { code = orderCode, amount = amountVnd.Value, tx = Guid.NewGuid().ToString("N"), prov = provider, meth = method });
                _log.LogWarning("ConfirmPayment: amount mismatch for {code}. expected={expected} paid={paid}", orderCode, expected, amountVnd.Value);
                return false;
            }
        }
        else if (!forceWhenReturn)
        {
            return false;
        }

        await con.ExecuteAsync("""
            INSERT dbo.tbl_payment_transaction(order_id, provider, method, status, amount, currency, transaction_id, merchant_ref, 
                paid_at, created_at, updated_at)
            VALUES ((SELECT id FROM dbo.tbl_orders WHERE order_code=@code),
                    @prov, @meth, 1,
                    COALESCE(@amount, (SELECT CAST(ROUND(pay_total,0) AS BIGINT) FROM dbo.tbl_orders WHERE order_code=@code)),
                    'VND', @tx, @code, SYSDATETIME(), SYSDATETIME(), SYSDATETIME())
        """, new { code = orderCode, amount = amountVnd, tx = Guid.NewGuid().ToString("N"), prov = provider, meth = method });

        await con.ExecuteAsync("""
            UPDATE dbo.tbl_orders
            SET payment_status='Paid', paid_at=SYSDATETIME(), updated_at=SYSDATETIME()
            WHERE order_code=@code
        """, new { code = orderCode });

        return true;
    }

    private string ComposeFeUrl(string pathAndQuery)
    {
        var baseUrl = _fe?.Value?.BaseUrl?.TrimEnd('/') ?? "";
        if (string.IsNullOrEmpty(baseUrl))
            return pathAndQuery.StartsWith("/") ? pathAndQuery : "/" + pathAndQuery;
        if (!pathAndQuery.StartsWith("/")) pathAndQuery = "/" + pathAndQuery;
        return baseUrl + pathAndQuery;
    }

    // ===== ZaloPay Return: validate data/mac nếu có; nếu không có thì Query để chốt =====
    // HAShop.Api/Controllers/PaymentsController.cs
    [HttpGet("payment/zalopay-return")]
    public async Task<IActionResult> ZaloPayReturn([FromServices] IZaloPayGateway zp)
    {
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(Request.QueryString.Value ?? "");
        string? orderCode = q.TryGetValue("code", out var cv) ? cv.ToString() : null;
        string? appTransId = q.TryGetValue("apptransid", out var at) ? at.ToString() : null;

        if (string.IsNullOrWhiteSpace(appTransId) && !string.IsNullOrWhiteSpace(orderCode))
        {
            using var con0 = _db.Create();
            appTransId = await con0.ExecuteScalarAsync<string?>(
                "SELECT payment_ref FROM dbo.tbl_orders WHERE order_code=@c", new { c = orderCode });
        }

        var paid = false;

        // 1) Thử Query nhiều lần (giữ nguyên như bạn đã làm)
        if (!string.IsNullOrWhiteSpace(appTransId))
        {
            var delays = new[] { 1, 2, 4, 8, 8 };
            for (int i = 0; i < delays.Length && !paid; i++)
            {
                try
                {
                    var qr = await zp.QueryAsync(appTransId, HttpContext.RequestAborted);
                    _log.LogInformation("ZP RETURN->QUERY try#{i} rc={rc} msg={msg} amount={amt}",
                        i + 1, qr.return_code, qr.return_message, qr.amount);

                    if (qr.return_code == 1)
                    {
                        orderCode ??= appTransId.Split('_').Skip(1).FirstOrDefault();
                        await ConfirmPaymentAsync(orderCode ?? "", qr.amount, "ZALOPAY", 3, forceWhenReturn: true);
                        paid = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "ZP RETURN Query error try#{i} for {appTransId}", i + 1, appTransId);
                }
                await Task.Delay(TimeSpan.FromSeconds(delays[i]));
            }
        }

        // 2) DEV MODE: ép Paid nếu vẫn chưa paid
        if (!paid && _flags.Value.ForceConfirmOnReturnForDev && !string.IsNullOrWhiteSpace(orderCode))
        {
            _log.LogWarning("DEV ONLY: Force confirm ZaloPay for order {code} on return.", orderCode);
            await ConfirmPaymentAsync(orderCode, amountVnd: null, provider: "ZALOPAY", method: 3, forceWhenReturn: true);
            paid = true;
        }

        var url = string.IsNullOrWhiteSpace(orderCode)
            ? ComposeFeUrl("/CartPage/ThankYou.aspx")
            : ComposeFeUrl($"/CartPage/ThankYou.aspx?code={Uri.EscapeDataString(orderCode)}");

        return Redirect(url);
    }




    public record ZpIpnModel(string data, string mac);

    [HttpPost("payment/zalopay-ipn")]
    public async Task<IActionResult> ZaloPayIpn([FromServices] IZaloPayGateway zp)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        _log.LogInformation("ZP IPN body: {body}", body);

        ZpIpnModel? model;
        try { model = System.Text.Json.JsonSerializer.Deserialize<ZpIpnModel>(body); }
        catch { model = null; }
        if (model is null || string.IsNullOrWhiteSpace(model.data))
            return Ok(new { return_code = -1, return_message = "Invalid payload" });

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data"] = model.data,
            ["mac"] = model.mac ?? ""
        };
        var ok = zp.ValidateIpn(fields, out _, out _);
        if (!ok) return Ok(new { return_code = -1, return_message = "MAC not matched" });

        string? orderCode = null;
        int result = -1;
        long? amount = null;

        try
        {
            var root = System.Text.Json.JsonDocument.Parse(model.data).RootElement;
            var appTransId = root.GetProperty("apptransid").GetString(); // yymmdd_code
            result = root.GetProperty("returncode").GetInt32();          // 1=success
            amount = root.TryGetProperty("amount", out var a) ? a.GetInt64() : null;
            orderCode = appTransId?.Split('_').Skip(1).FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ZP IPN parse data error");
            return Ok(new { return_code = -1, return_message = "Parse error" });
        }

        if (string.IsNullOrWhiteSpace(orderCode))
            return Ok(new { return_code = -1, return_message = "Order not found" });

        if (result == 1)
        {
            var success = await ConfirmPaymentAsync(orderCode, amount, "ZALOPAY", 3, false);
            if (!success) return Ok(new { return_code = 4, return_message = "Invalid amount or order state" });
            _log.LogInformation("ZP IPN: set Paid for {code}", orderCode);
            return Ok(new { return_code = 1, return_message = "Confirm success" });
        }
        else
        {
            using var con = _db.Create();
            await con.ExecuteAsync("""
                UPDATE dbo.tbl_orders
                SET payment_status='Failed', updated_at=SYSDATETIME()
                WHERE order_code=@c
            """, new { c = orderCode });

            _log.LogInformation("ZP IPN: set Failed for {code}, rc={rc}", orderCode, result);
            return Ok(new { return_code = 1, return_message = "Confirm fail recorded" });
        }
    }

    private static DateTime ToVNTime(DateTime utc)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")); }
        catch { return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")); }
    }

}
