// HAShop.Api/Controllers/PaymentsController.cs
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.Options;
using HAShop.Api.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Data.Common;
using System.Text;
using System.Text.Json;

[ApiController]
public class PaymentsController : ControllerBase
{
    private readonly Pay2SService _pay2s;
    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<PaymentsController> _log;
    private readonly IOptions<PaymentsFlags> _flags;
    private readonly IOptions<FrontendOptions> _fe;

    public PaymentsController(
        Pay2SService pay2s,
        ISqlConnectionFactory db,
        ILogger<PaymentsController> log,
        IOptions<PaymentsFlags> flags,
        IOptions<FrontendOptions> fe)
    {
        _pay2s = pay2s;
        _db = db;
        _log = log;
        _flags = flags;
        _fe = fe;
    }

    // =========================
    // USER RETURN (UI)  (giữ route cũ vnpay-return nhưng xử lý Pay2S)
    // =========================
    //[HttpGet("payment/vnpay-return")]
    //public async Task<IActionResult> VnPayReturn()
    //{
    //    _log.LogInformation("PAY2S REDIRECT query: {q}", Request.QueryString.Value);

    //    // ✅ FIX: Pay2SService của bạn chỉ nhận 1 tham số
    //    if (!_pay2s.ValidateRedirectSignature(Request.Query))
    //    {
    //        _log.LogWarning("PAY2S REDIRECT invalid signature");
    //        return Redirect(ComposeFeUrl("/CartPage/CheckoutConfirm.aspx?payfail=1&prov=pay2s"));
    //    }

    //    var resultCode = (string?)Request.Query["resultCode"]; // success = "0"
    //    var orderCode = (string?)Request.Query["orderId"];    // orderId = orderCode lúc create
    //    var amountRaw = (string?)Request.Query["amount"];     // VND

    //    long? amountVnd = null;
    //    if (long.TryParse(amountRaw, out var a)) amountVnd = a;

    //    if (_flags.Value.ConfirmOnReturnIfOk &&
    //        resultCode == "0" &&
    //        !string.IsNullOrWhiteSpace(orderCode))
    //    {
    //        await ConfirmPaymentAsync(orderCode!, amountVnd, provider: "PAY2S", method: 2, forceWhenReturn: true);
    //    }

    //    string url;
    //    if (resultCode == "0")
    //    {
    //        url = string.IsNullOrWhiteSpace(orderCode)
    //            ? ComposeFeUrl("/CartPage/ThankYou.aspx")
    //            : ComposeFeUrl($"/CartPage/ThankYou.aspx?code={Uri.EscapeDataString(orderCode)}");
    //    }
    //    else
    //    {
    //        url = string.IsNullOrWhiteSpace(orderCode)
    //            ? ComposeFeUrl("/CartPage/CheckoutConfirm.aspx?payfail=1&prov=pay2s")
    //            : ComposeFeUrl($"/CartPage/CheckoutConfirm.aspx?payfail=1&prov=pay2s&code={Uri.EscapeDataString(orderCode)}");
    //    }

    //    return Redirect(url);
    //}

    // =========================
    // IPN (SERVER-TO-SERVER) (giữ route cũ vnpay-ipn nhưng xử lý Pay2S)
    // =========================
    //[HttpPost("payment/vnpay-ipn")]
    //public async Task<IActionResult> VnPayIpn()
    //{
    //    var model = await ReadPay2SIpnAsync();
    //    _log.LogInformation("PAY2S IPN payload: {m}", JsonSerializer.Serialize(model));

    //    if (model is null || string.IsNullOrWhiteSpace(model.orderId))
    //        return Ok(new { success = false });

    //    if (!_pay2s.ValidateIpnSignature(model))
    //    {
    //        _log.LogWarning("PAY2S IPN invalid signature");
    //        return Ok(new { success = false });
    //    }

    //    var orderCode = model.orderId;
    //    var amountVnd = (long?)model.amount;

    //    if (model.resultCode == 0)
    //    {
    //        var ok = await ConfirmPaymentAsync(orderCode, amountVnd, provider: "PAY2S", method: 2, forceWhenReturn: false);
    //        if (!ok) return Ok(new { success = false });

    //        _log.LogInformation("PAY2S IPN: set Paid for {code}", orderCode);
    //        return Ok(new { success = true });
    //    }
    //    else
    //    {
    //        using var con = _db.Create();
    //        await con.ExecuteAsync("""
    //            UPDATE dbo.tbl_orders
    //            SET payment_status='Failed', updated_at=SYSDATETIME()
    //            WHERE order_code=@c
    //        """, new { c = orderCode });

    //        _log.LogInformation("PAY2S IPN: set Failed for {code}, resultCode={rc}", orderCode, model.resultCode);
    //        return Ok(new { success = true });
    //    }
    //}

    // =========================
    // Helper: đọc IPN cả JSON lẫn form-urlencoded
    // =========================
    private async Task<Pay2SIpnModel?> ReadPay2SIpnAsync(CancellationToken ct)
    {
        try
        {
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(ct);

                static long L(string s) => long.TryParse(s, out var v) ? v : 0;
                static int I(string s) => int.TryParse(s, out var v) ? v : -1;

                return new Pay2SIpnModel
                {
                    partnerCode = form["partnerCode"].ToString(),
                    orderId = form["orderId"].ToString(),
                    requestId = form["requestId"].ToString(),
                    amount = L(form["amount"].ToString()),
                    orderInfo = form["orderInfo"].ToString(),
                    orderType = form["orderType"].ToString(),
                    transId = L(form["transId"].ToString()),
                    resultCode = I(form["resultCode"].ToString()),
                    message = form["message"].ToString(),
                    payType = form["payType"].ToString(),
                    responseTime = form["responseTime"].ToString(),
                    extraData = form["extraData"].ToString(),
                    m2signature = form["m2signature"].ToString(),
                };
            }

            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;

            return JsonSerializer.Deserialize<Pay2SIpnModel>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ReadPay2SIpnAsync failed");
            return null;
        }
    }


    // =========================
    // Helper: xác nhận thanh toán thành công (idempotent)
    // =========================
    // =========================
    // Helper: xác nhận thanh toán thành công (idempotent + chống race)
    // =========================
    private async Task<bool> ConfirmPaymentAsync(
    string orderCode,
    long? amountVnd,
    string provider,
    int method,
    bool forceWhenReturn,
    CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) return false;

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
                return false;
            }

            long orderId = (long)o.id;
            string? payStatus = (string?)o.payment_status;

            var existedPaidTx = await con.ExecuteScalarAsync<int>(new CommandDefinition(
                """
            SELECT COUNT(1)
            FROM dbo.tbl_payment_transaction t
            WHERE t.order_id = @oid AND t.status = 1 AND t.provider = @prov
            """,
                new { oid = orderId, prov = provider },
                transaction: tx,
                cancellationToken: ct,
                commandTimeout: 15));

            if (existedPaidTx > 0 || string.Equals(payStatus, "Paid", StringComparison.OrdinalIgnoreCase))
            {
                await con.ExecuteAsync(new CommandDefinition(
                    """
                UPDATE dbo.tbl_orders
                SET payment_status='Paid',
                    paid_at = COALESCE(paid_at, SYSDATETIME()),
                    updated_at = SYSDATETIME()
                WHERE id=@oid
                """,
                    new { oid = orderId },
                    transaction: tx,
                    cancellationToken: ct,
                    commandTimeout: 15));

                tx.Commit();
                return true;
            }

            if (amountVnd.HasValue)
            {
                var expected = (long)Math.Round((decimal)o.pay_total, MidpointRounding.AwayFromZero);
                if (expected != amountVnd.Value)
                {
                    await con.ExecuteAsync(new CommandDefinition(
                        """
                    INSERT dbo.tbl_payment_transaction
                        (order_id, provider, method, status, amount, currency, transaction_id, merchant_ref,
                         error_code, error_message, created_at, updated_at)
                    VALUES
                        (@oid, @prov, @meth, 0, @amount, 'VND', @tx, @mref,
                         'AMOUNT_MISMATCH', 'Amount mismatch (return/ipn)', SYSDATETIME(), SYSDATETIME())
                    """,
                        new
                        {
                            oid = orderId,
                            prov = provider,
                            meth = method,
                            amount = amountVnd.Value,
                            tx = Guid.NewGuid().ToString("N"),
                            mref = orderCode
                        },
                        transaction: tx,
                        cancellationToken: ct,
                        commandTimeout: 15));

                    tx.Commit();
                    return false;
                }
            }
            else if (!forceWhenReturn)
            {
                tx.Rollback();
                return false;
            }

            await con.ExecuteAsync(new CommandDefinition(
                """
            IF NOT EXISTS (
                SELECT 1 FROM dbo.tbl_payment_transaction
                WHERE order_id=@oid AND status=1 AND provider=@prov
            )
            BEGIN
                INSERT dbo.tbl_payment_transaction
                    (order_id, provider, method, status, amount, currency, transaction_id, merchant_ref,
                     paid_at, created_at, updated_at)
                VALUES
                    (@oid, @prov, @meth, 1,
                     COALESCE(@amount, (SELECT CAST(ROUND(pay_total,0) AS BIGINT) FROM dbo.tbl_orders WHERE id=@oid)),
                     'VND', @tx, @mref, SYSDATETIME(), SYSDATETIME(), SYSDATETIME())
            END
            """,
                new
                {
                    oid = orderId,
                    amount = amountVnd,
                    tx = Guid.NewGuid().ToString("N"),
                    prov = provider,
                    meth = method,
                    mref = orderCode
                },
                transaction: tx,
                cancellationToken: ct,
                commandTimeout: 15));

            await con.ExecuteAsync(new CommandDefinition(
                """
            UPDATE dbo.tbl_orders
            SET payment_status='Paid',
                paid_at = COALESCE(paid_at, SYSDATETIME()),
                updated_at = SYSDATETIME()
            WHERE id=@oid
            """,
                new { oid = orderId },
                transaction: tx,
                cancellationToken: ct,
                commandTimeout: 15));

            tx.Commit();
            return true;
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            _log.LogError(ex, "ConfirmPaymentAsync failed for {code}", orderCode);
            return false;
        }
    }



    // =========================
    // USER RETURN (UI) - PAY2S
    // =========================
    // =========================
    // USER RETURN (UI) - PAY2S
    // =========================
    [HttpGet("payment/pay2s-return")]
    public async Task<IActionResult> Pay2SReturn(CancellationToken ct)
    {
        _log.LogInformation("PAY2S REDIRECT query: {q}", Request.QueryString.Value);

        if (!_pay2s.ValidateRedirectSignature(Request.Query))
        {
            _log.LogWarning("PAY2S REDIRECT invalid signature");
            return Redirect(ComposeFeUrl("/CartPage/CheckoutConfirm.aspx?payfail=1&prov=pay2s"));
        }

        var rcRaw = (string?)Request.Query["resultCode"];
        var okRc = int.TryParse(rcRaw, out var rc) && rc == 0;

        var orderCode = ((string?)Request.Query["orderId"])?.Trim();
        var amountRaw = (string?)Request.Query["amount"];

        long? amountVnd = null;
        if (long.TryParse(amountRaw, out var a) && a > 0) amountVnd = a;

        if (_flags.Value.ConfirmOnReturnIfOk && okRc && !string.IsNullOrWhiteSpace(orderCode))
        {
            var ok = await ConfirmPaymentAsync(orderCode!, amountVnd, provider: "PAY2S", method: 2, forceWhenReturn: true, ct);
            if (ok) await TryNotifyAdminPaidOnceAsync(orderCode!, ct);
        }

        var url = okRc
            ? (string.IsNullOrWhiteSpace(orderCode)
                ? ComposeFeUrl("/CartPage/ThankYou.aspx")
                : ComposeFeUrl($"/CartPage/ThankYou.aspx?code={Uri.EscapeDataString(orderCode)}"))
            : (string.IsNullOrWhiteSpace(orderCode)
                ? ComposeFeUrl("/CartPage/CheckoutConfirm.aspx?payfail=1&prov=pay2s")
                : ComposeFeUrl($"/CartPage/CheckoutConfirm.aspx?payfail=1&prov=pay2s&code={Uri.EscapeDataString(orderCode)}"));

        return Redirect(url);
    }



    // =========================
    // IPN (SERVER-TO-SERVER) - PAY2S
    // =========================
    [HttpPost("payment/pay2s-ipn")]
    public async Task<IActionResult> Pay2SIpn(CancellationToken ct)
    {
        var model = await ReadPay2SIpnAsync(ct);
        _log.LogInformation("PAY2S IPN model: {m}", System.Text.Json.JsonSerializer.Serialize(model));

        if (model == null || string.IsNullOrWhiteSpace(model.orderId))
            return Ok(new { success = false });

        if (!_pay2s.ValidateIpnSignature(model))
        {
            _log.LogWarning("PAY2S IPN invalid signature");
            return Ok(new { success = false }); // để Pay2S retry
        }

        var orderCode = model.orderId.Trim();
        var amountVnd = (long?)model.amount;

        if (model.resultCode == 0)
        {
            var ok = await ConfirmPaymentAsync(orderCode, amountVnd, provider: "PAY2S", method: 2, forceWhenReturn: false, ct);
            if (ok) await TryNotifyAdminPaidOnceAsync(orderCode, ct);
            return Ok(new { success = ok });
        }

        using (var con = _db.Create())
        {
            await con.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.tbl_orders
            SET payment_status='Failed', updated_at=SYSDATETIME()
            WHERE order_code=@c
        """, new { c = orderCode }, cancellationToken: ct, commandTimeout: 15));
        }

        return Ok(new { success = true });
    }


    private string ComposeFeUrl(string pathAndQuery)
    {
        var baseUrl = _fe?.Value?.BaseUrl?.TrimEnd('/') ?? "";
        if (string.IsNullOrEmpty(baseUrl))
            return pathAndQuery.StartsWith("/") ? pathAndQuery : "/" + pathAndQuery;

        if (!pathAndQuery.StartsWith("/")) pathAndQuery = "/" + pathAndQuery;
        return baseUrl + pathAndQuery;
    }
    private async Task<bool> TryNotifyAdminPaidOnceAsync(string orderCode, CancellationToken ct)
    {
        using var con = _db.Create();

        var row = await con.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            """
        SELECT id, order_code, pay_total, ship_name, ship_phone, ship_full_address,
               payment_method, placed_at, paid_at
        FROM dbo.tbl_orders
        WHERE order_code=@c AND payment_status='Paid'
        """,
            new { c = orderCode },
            cancellationToken: ct,
            commandTimeout: 15));

        if (row == null) return false;

        long orderId = (long)row.id;

        var existed = await con.ExecuteScalarAsync<int>(new CommandDefinition(
            """
        SELECT COUNT(1)
        FROM dbo.tbl_payment_transaction
        WHERE order_id=@oid AND error_code='ADMIN_TG_PAID'
        """,
            new { oid = orderId },
            cancellationToken: ct,
            commandTimeout: 15));

        if (existed > 0) return true;

        await con.ExecuteAsync(new CommandDefinition(
            """
        INSERT dbo.tbl_payment_transaction
            (order_id, provider, method, status, amount, currency, transaction_id, merchant_ref,
             error_code, error_message, created_at, updated_at)
        VALUES
            (@oid, 'PAY2S', COALESCE(@m,0), 1, @amt, 'VND', @tx, @mref,
             'ADMIN_TG_PAID', 'Telegram notified when paid', SYSDATETIME(), SYSDATETIME())
        """,
            new
            {
                oid = orderId,
                m = (byte?)row.payment_method,
                amt = (long)Math.Round((decimal)row.pay_total, MidpointRounding.AwayFromZero),
                tx = Guid.NewGuid().ToString("N"),
                mref = (string)row.order_code
            },
            cancellationToken: ct,
            commandTimeout: 15));

        string fullAddr = (string?)row.ship_full_address ?? "";
        string? shortAddr = string.IsNullOrWhiteSpace(fullAddr)
            ? null
            : (fullAddr.Length <= 100 ? fullAddr : fullAddr.Substring(0, 97) + "...");

        var notifier = HttpContext.RequestServices.GetRequiredService<HAShop.Api.Services.IAdminOrderNotifier>();

        await notifier.NotifyNewOrderAsync(
            orderId,
            (string)row.order_code,
            (decimal)row.pay_total,
            (string?)row.ship_name ?? "",
            (string?)row.ship_phone ?? "",
            shortAddr,
            (byte?)row.payment_method,
            (DateTime?)row.paid_at ?? (DateTime?)row.placed_at,
            ct);

        return true;
    }

}
