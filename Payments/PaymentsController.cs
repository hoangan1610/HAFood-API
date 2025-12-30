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
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Linq;


[ApiController]
public class PaymentsController : ControllerBase
{
    private readonly Pay2SService _pay2s;
    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<PaymentsController> _log;
    private readonly IOptions<PaymentsFlags> _flags;
    private readonly IOptions<FrontendOptions> _fe;
    private readonly IOptions<ZaloPayOptions> _zpOpt;

    public PaymentsController(
     Pay2SService pay2s,
     ISqlConnectionFactory db,
     ILogger<PaymentsController> log,
     IOptions<PaymentsFlags> flags,
     IOptions<FrontendOptions> fe,
     IOptions<ZaloPayOptions> zpOpt)
    {
        _pay2s = pay2s;
        _db = db;
        _log = log;
        _flags = flags;
        _fe = fe;
        _zpOpt = zpOpt;
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
            if (ok) await TryNotifyAdminPaidOnceAsync(orderCode!, "PAY2S", 2, ct);

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
            if (ok) await TryNotifyAdminPaidOnceAsync(orderCode, "PAY2S", 2, ct);

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

    private static string QFirst(Microsoft.AspNetCore.Http.IQueryCollection q, string key)
    => q.TryGetValue(key, out var v) ? (v.FirstOrDefault() ?? "") : "";

    private static string FFirst(Microsoft.AspNetCore.Http.IFormCollection f, string key)
        => f.TryGetValue(key, out var v) ? (v.FirstOrDefault() ?? "") : "";

    private static string HmacSha256Hex(string key, string data, bool lower)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key ?? ""));
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes(data ?? ""));
        var sb = new StringBuilder(hash.Length * 2);
        var fmt = lower ? "x2" : "X2";
        foreach (var b in hash) sb.Append(b.ToString(fmt));
        return sb.ToString();
    }

    // ✅ ZaloPay Redirect checksum verify (GET return)
    private bool ValidateZaloPayRedirectChecksum(
        string appid,
        string apptransid,
        string pmcid,
        string bankcode,
        string amount,
        string discountamount,
        string status,
        string checksum)
    {
        // raw = appid|apptransid|pmcid|bankcode|amount|discountamount|status
        var raw = $"{appid}|{apptransid}|{pmcid}|{bankcode}|{amount}|{discountamount}|{status}";

        var opt = _zpOpt.Value;
        var computed = HmacSha256Hex(opt.Key2 ?? "", raw, opt.LowercaseMac);

        var ok = (checksum ?? "").Equals(computed, StringComparison.OrdinalIgnoreCase);
        if (!ok)
            _log.LogWarning("ZP checksum FAIL. raw={raw} computed={c} received={r}", raw, computed, checksum);

        return ok;
    }
    [HttpGet("payment/zalopay-return")]
    public async Task<IActionResult> ZaloPayReturn(CancellationToken ct)
    {
        _log.LogInformation("ZP REDIRECT query: {q}", Request.QueryString.Value);

        // ⚠️ lấy FIRST để né duplicate param
        var appid = QFirst(Request.Query, "appid");
        var apptransid = QFirst(Request.Query, "apptransid");
        var pmcid = QFirst(Request.Query, "pmcid");
        var bankcode = QFirst(Request.Query, "bankcode");
        var amountRaw = QFirst(Request.Query, "amount");
        var discountRaw = QFirst(Request.Query, "discountamount");
        var statusRaw = QFirst(Request.Query, "status");
        var checksum = QFirst(Request.Query, "checksum");

        // order code (bạn đang append ?code=... trong redirecturl)
        var orderCode = (QFirst(Request.Query, "code") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(orderCode))
            orderCode = (DeriveOrderCodeFromAppTransId(apptransid) ?? "").Trim();

        // Validate checksum
        if (!ValidateZaloPayRedirectChecksum(appid, apptransid, pmcid, bankcode, amountRaw, discountRaw, statusRaw, checksum))
        {
            _log.LogWarning("ZP REDIRECT invalid checksum");
            return Redirect(ComposeFeUrl("/CartPage/CheckoutConfirm.aspx?payfail=1&prov=zalopay"));
        }

        int.TryParse(statusRaw, out var status);
        var okStatus = (status == 1); // ✅ ZP redirect: status=1 = thanh toán thành công

        long? amountVnd = null;
        if (long.TryParse(amountRaw, out var a) && a > 0) amountVnd = a;

        if (!string.IsNullOrWhiteSpace(orderCode))
        {
            if (okStatus)
            {
                // (tuỳ chọn) confirm on return
                if (_flags.Value.ConfirmOnReturnIfOk)
                {
                    var ok = await ConfirmPaymentAsync(
                        orderCode,
                        amountVnd,
                        provider: "ZALOPAY",
                        method: 1,
                        forceWhenReturn: true,
                        ct);

                    if (ok) await TryNotifyAdminPaidOnceAsync(orderCode, "ZALOPAY", 1, ct);
                }
            }
            else
            {
                // ✅ status != 1: user cancel / fail
                var newPaymentStatus = "Unpaid";
                if (status > 0 && status != 1) newPaymentStatus = "Failed";

                using var con = _db.Create();
                await con.ExecuteAsync(new CommandDefinition(
                    """
                UPDATE dbo.tbl_orders
                SET payment_status=@stt, updated_at=SYSDATETIME()
                WHERE order_code=@c
                """,
                    new { c = orderCode, stt = newPaymentStatus },
                    cancellationToken: ct,
                    commandTimeout: 15));
            }
        }

        // Redirect FE
        var url = okStatus
            ? (string.IsNullOrWhiteSpace(orderCode)
                ? ComposeFeUrl("/CartPage/ThankYou.aspx")
                : ComposeFeUrl($"/CartPage/ThankYou.aspx?code={Uri.EscapeDataString(orderCode)}"))
            : (string.IsNullOrWhiteSpace(orderCode)
                ? ComposeFeUrl("/CartPage/CheckoutConfirm.aspx?payfail=1&prov=zalopay")
                : ComposeFeUrl($"/CartPage/CheckoutConfirm.aspx?payfail=1&prov=zalopay&code={Uri.EscapeDataString(orderCode)}"));

        return Redirect(url);
    }

    [HttpPost("payment/zalopay-ipn")]
    public async Task<IActionResult> ZaloPayIpn(CancellationToken ct)
    {
        try
        {
            // ZP callback thường là form-urlencoded: data + mac
            if (!Request.HasFormContentType)
            {
                _log.LogWarning("ZP IPN: not form content type");
                return Ok(new { return_code = -1, return_message = "invalid content-type" });
            }

            var form = await Request.ReadFormAsync(ct);
            var data = FFirst(form, "data");
            var mac = FFirst(form, "mac");

            if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(mac))
            {
                _log.LogWarning("ZP IPN: missing data/mac");
                return Ok(new { return_code = -1, return_message = "missing data/mac" });
            }

            // verify mac = HMAC_SHA256(Key2, data)
            var opt = _zpOpt.Value;
            var computed = HmacSha256Hex(opt.Key2 ?? "", data, opt.LowercaseMac);

            if (!mac.Equals(computed, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("ZP IPN MAC FAIL. computed={c} received={r}", computed, mac);
                return Ok(new { return_code = -1, return_message = "mac invalid" }); // để ZP retry
            }

            // parse data json
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            var appTransId = root.TryGetProperty("app_trans_id", out var at) ? at.GetString() : null;
            int status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;
            long amount = root.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number ? am.GetInt64() : 0;

            // derive orderCode từ app_trans_id: yyMMdd_<orderCode>_<rnd>
            var orderCode = DeriveOrderCodeFromAppTransId(appTransId);

            _log.LogInformation("ZP IPN parsed: app_trans_id={atid} status={st} amount={amt} orderCode={code}",
                appTransId, status, amount, orderCode);

            if (string.IsNullOrWhiteSpace(orderCode))
            {
                // không map được order -> vẫn trả OK để khỏi retry vô hạn
                return Ok(new { return_code = 1, return_message = "ok" });
            }

            if (status == 1)
            {
                var ok = await ConfirmPaymentAsync(
                    orderCode,
                    amount > 0 ? (long?)amount : null,
                    provider: "ZALOPAY",
                    method: 1,
                    forceWhenReturn: false,
                    ct);

                if (ok) await TryNotifyAdminPaidOnceAsync(orderCode, "ZALOPAY", 1, ct);

                return Ok(new { return_code = 1, return_message = "ok" });
            }
            else
            {
                // ✅ status != 1: user cancel / fail
                // cancel -> Unpaid (để user chọn cổng khác), fail nặng -> Failed
                var newPaymentStatus = "Unpaid";

                // Quy ước đơn giản:
                // - status âm / 0: cancel hoặc không thành công => Unpaid
                // - status dương khác 1: fail => Failed
                if (status > 0 && status != 1) newPaymentStatus = "Failed";

                using var con = _db.Create();
                await con.ExecuteAsync(new CommandDefinition(
                    """
                UPDATE dbo.tbl_orders
                SET payment_status=@stt, updated_at=SYSDATETIME()
                WHERE order_code=@c
                """,
                    new { c = orderCode, stt = newPaymentStatus },
                    cancellationToken: ct,
                    commandTimeout: 15));

                return Ok(new { return_code = 1, return_message = "ok" });
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ZP IPN error");
            return Ok(new { return_code = 0, return_message = "internal error" });
        }
    }


    // ✅ derive orderCode từ app_trans_id dạng yyMMdd_<orderCode>_<rnd>
    private static string? DeriveOrderCodeFromAppTransId(string? appTransId)
    {
        if (string.IsNullOrWhiteSpace(appTransId)) return null;
        var parts = appTransId.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) return parts[1]; // "251219000004"
        return null;
    }

    private string ComposeFeUrl(string pathAndQuery)
    {
        var baseUrl = _fe?.Value?.BaseUrl?.TrimEnd('/') ?? "";
        if (string.IsNullOrEmpty(baseUrl))
            return pathAndQuery.StartsWith("/") ? pathAndQuery : "/" + pathAndQuery;

        if (!pathAndQuery.StartsWith("/")) pathAndQuery = "/" + pathAndQuery;
        return baseUrl + pathAndQuery;
    }
    private async Task<bool> TryNotifyAdminPaidOnceAsync(
    string orderCode,
    string provider,
    int method,
    CancellationToken ct)
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

        // ✅ idempotent theo từng provider (tránh ZaloPay ghi đè Pay2S)
        var existed = await con.ExecuteScalarAsync<int>(new CommandDefinition(
            """
        SELECT COUNT(1)
        FROM dbo.tbl_payment_transaction
        WHERE order_id=@oid AND error_code='ADMIN_TG_PAID' AND provider=@prov
        """,
            new { oid = orderId, prov = provider },
            cancellationToken: ct,
            commandTimeout: 15));

        if (existed > 0) return true;

        await con.ExecuteAsync(new CommandDefinition(
            """
        INSERT dbo.tbl_payment_transaction
            (order_id, provider, method, status, amount, currency, transaction_id, merchant_ref,
             error_code, error_message, created_at, updated_at)
        VALUES
            (@oid, @prov, @meth, 1, @amt, 'VND', @tx, @mref,
             'ADMIN_TG_PAID', 'Telegram notified when paid', SYSDATETIME(), SYSDATETIME())
        """,
            new
            {
                oid = orderId,
                prov = provider,
                meth = method,
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
