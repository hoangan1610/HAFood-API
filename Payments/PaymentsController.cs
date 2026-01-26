using Dapper;
using HAShop.Api.Data;
using HAShop.Api.Options;
using HAShop.Api.Payments;
using HAShop.Api.Services;
using HAShop.Api.Utils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

[ApiController]
public class PaymentsController : ControllerBase
{
    private readonly Pay2SService _pay2s;
    private readonly MomoService _momo;
    private readonly ISqlConnectionFactory _db;
    private readonly ILogger<PaymentsController> _log;
    private readonly IOptions<PaymentsFlags> _flags;
    private readonly IOptions<FrontendOptions> _fe;

    private readonly IOptions<ZaloPayOptions> _zpOpt;
    private readonly IZaloPayGateway _zpGateway;

    private readonly IWebHostEnvironment _env;

    private const int METHOD_MOMO = 1;
    private const int METHOD_PAY2S = 2;
    private const int METHOD_ZALOPAY = 9;

    private const string PROV_MOMO = "MOMO";
    private const string PROV_PAY2S = "PAY2S";
    private const string PROV_ZALOPAY = "ZALOPAY";

    private const string FE_CHECKOUT_CONFIRM = "/CartPage/CheckoutConfirm";
    private const string FE_THANKYOU = "/CartPage/ThankYou";

    private const string DEV_FE_BASE = "https://localhost:44336";
    private const string DEV_API_BASE = "http://localhost:8080";

    public PaymentsController(
        Pay2SService pay2s,
        MomoService momo,
        ISqlConnectionFactory db,
        ILogger<PaymentsController> log,
        IOptions<PaymentsFlags> flags,
        IOptions<FrontendOptions> fe,
        IOptions<ZaloPayOptions> zpOpt,
        IZaloPayGateway zpGateway,
        IWebHostEnvironment env)
    {
        _pay2s = pay2s;
        _momo = momo;
        _db = db;
        _log = log;
        _flags = flags;
        _fe = fe;
        _zpOpt = zpOpt;
        _zpGateway = zpGateway;
        _env = env;
    }

    // =========================================================
    // ✅ Resolve order_code từ providerOrderId (payment_ref) hoặc legacy order_code
    // =========================================================
    private async Task<string?> ResolveOrderCodeAsync(string? providerOrderId, CancellationToken ct)
    {
        var id = (providerOrderId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) return null;

        using var con = _db.Create();
        if (con is DbConnection dbc) await dbc.OpenAsync(ct);
        else con.Open();

        var code = await con.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            SELECT TOP(1) order_code
            FROM dbo.tbl_orders
            WHERE order_code=@id OR payment_ref=@id
            """,
            new { id },
            cancellationToken: ct,
            commandTimeout: 10
        ));

        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    // =========================================================
    // CREATE ZALOPAY ORDER
    // =========================================================
    public sealed class ZaloPayCreateRequest
    {
        public string OrderCode { get; set; } = "";
        public long AmountVnd { get; set; }
        public string? Description { get; set; }
        public string? AppUser { get; set; }
        public string? ClientReturnUrl { get; set; }
        public string? ClientPlatform { get; set; }
    }

    [HttpPost("payment/zalopay-create")]
    public async Task<IActionResult> ZaloPayCreate([FromBody] ZaloPayCreateRequest req, CancellationToken ct)
    {
        if (req == null) return BadRequest(new { ok = false, message = "missing body" });

        var orderCode = (req.OrderCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(orderCode))
            return BadRequest(new { ok = false, message = "orderCode required" });

        if (req.AmountVnd <= 0)
            return BadRequest(new { ok = false, message = "amountVnd must be > 0" });

        var platform = (req.ClientPlatform ?? "").Trim();
        if (string.IsNullOrWhiteSpace(platform))
            platform = Request.Headers.TryGetValue("X-Client-Platform", out var p) ? (p.ToString() ?? "") : "";
        platform = platform.Trim().ToLowerInvariant();

        var desc = string.IsNullOrWhiteSpace(req.Description)
            ? $"Thanh toan don {orderCode}"
            : req.Description!.Trim();

        try
        {
            var zp = await _zpGateway.CreateOrderAsync(
                orderCode: orderCode,
                amountVnd: req.AmountVnd,
                description: desc,
                appUser: req.AppUser,
                clientReturnUrl: req.ClientReturnUrl,
                ct: ct
            );

            var m = await MarkPendingAndLogCreateAsync(
                orderCode: orderCode,
                amountVnd: req.AmountVnd,
                provider: PROV_ZALOPAY,
                method: METHOD_ZALOPAY,
                transactionId: zp.app_trans_id ?? Guid.NewGuid().ToString("N"),
                ct: ct
            );

            if (!m.ok)
            {
                return Ok(new
                {
                    ok = false,
                    code = m.errCode,
                    message = "ZaloPay created but cannot mark pending",
                    provider = PROV_ZALOPAY,
                    order_url = zp.order_url,
                    app_trans_id = zp.app_trans_id,
                    zp_trans_token = zp.zp_trans_token
                });
            }

            return Ok(new
            {
                ok = true,
                provider = PROV_ZALOPAY,
                order_url = zp.order_url,
                zp_trans_token = zp.zp_trans_token,
                app_trans_id = zp.app_trans_id,
                qr_code = zp.qr_code,
                client_platform = platform
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ZP create failed for {code} platform={platform}", orderCode, platform);
            return Ok(new { ok = false, message = "ZaloPay create failed" });
        }
    }

    // =========================================================
    // CREATE MOMO PAYMENT
    // =========================================================
    public sealed class MomoCreateRequest
    {
        public string OrderCode { get; set; } = "";
        public long AmountVnd { get; set; }
        public string? Description { get; set; }
        public string? ExtraDataBase64 { get; set; }
    }

    [HttpPost("payment/momo-create")]
    public async Task<IActionResult> MomoCreate([FromBody] MomoCreateRequest req, CancellationToken ct)
    {
        if (req == null) return BadRequest(new { ok = false, message = "missing body" });

        var orderCode = (req.OrderCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(orderCode))
            return BadRequest(new { ok = false, message = "orderCode required" });

        if (req.AmountVnd <= 0)
            return BadRequest(new { ok = false, message = "amountVnd must be > 0" });

        var desc = string.IsNullOrWhiteSpace(req.Description)
            ? $"Thanh toan don {orderCode}"
            : req.Description!.Trim();

        string? redirectOverride = null;
        if (_env.IsDevelopment())
        {
            redirectOverride = DEV_API_BASE.TrimEnd('/') + "/payment/momo-return";
        }

        _log.LogInformation("MOMO DEV override env={env} isDev={isDev} redirectOverride={override}",
            _env.EnvironmentName, _env.IsDevelopment(), redirectOverride);

        var r = await _momo.CreatePaymentAsync(
            orderCode: orderCode,
            amountVnd: req.AmountVnd,
            orderInfo: desc,
            extraDataBase64: (req.ExtraDataBase64 ?? ""),
            ct: ct,
            redirectUrlOverride: redirectOverride,
            ipnUrlOverride: null
        );

        // ✅ FIX QUAN TRỌNG: payment_ref phải là providerOrderId (r.orderId)
        var m = await MarkPendingAndLogCreateAsync(
            orderCode: orderCode,
            amountVnd: req.AmountVnd,
            provider: PROV_MOMO,
            method: METHOD_MOMO,
            transactionId: r.orderId,  // ✅ trước bạn dùng requestId -> sai cho resolve
            ct: ct
        );

        return Ok(new
        {
            ok = m.ok,
            provider = PROV_MOMO,
            order_url = r.payUrl,
            redirect_override = redirectOverride,
            errCode = m.errCode,
            provider_order_id = r.orderId
        });
    }

    // =========================================================
    // USER RETURN (UI) - MOMO
    // =========================================================
    [HttpGet("payment/momo-return")]
    public async Task<IActionResult> MomoReturn(CancellationToken ct)
    {
        _log.LogInformation("MOMO REDIRECT query: {q}", Request.QueryString.Value);

        var partnerCode = QFirst(Request.Query, "partnerCode");
        var providerOrderId = QFirst(Request.Query, "orderId"); // ✅ giờ là providerOrderId
        var requestId = QFirst(Request.Query, "requestId");
        var amountRaw = QFirst(Request.Query, "amount");
        var orderInfo = QFirst(Request.Query, "orderInfo");
        var orderType = QFirst(Request.Query, "orderType");
        var transId = QFirst(Request.Query, "transId");
        var resultCodeRaw = QFirst(Request.Query, "resultCode");
        var message = QFirst(Request.Query, "message");
        var payType = QFirst(Request.Query, "payType");
        var responseTime = QFirst(Request.Query, "responseTime");
        var extraData = QFirst(Request.Query, "extraData");
        var signature = QFirst(Request.Query, "signature");

        // ✅ Resolve orderCode thật từ payment_ref
        var orderCode = (await ResolveOrderCodeAsync(providerOrderId, ct)) ?? (providerOrderId ?? "").Trim();

        long? amountVnd = null;
        if (long.TryParse(amountRaw, out var a) && a > 0) amountVnd = a;

        if (!_momo.ValidateResultSignature(
                amountRaw, extraData, message, providerOrderId, orderInfo, orderType,
                partnerCode, payType, requestId, responseTime, resultCodeRaw, transId, signature))
        {
            _log.LogWarning("MOMO REDIRECT invalid signature providerOrderId={orderId} requestId={requestId}", providerOrderId, requestId);

            if (!string.IsNullOrWhiteSpace(orderCode))
            {
                await MarkFailedAndReleaseAsync(
                    orderCode: orderCode,
                    provider: PROV_MOMO,
                    method: METHOD_MOMO,
                    newStatus: "Failed",
                    errorCode: "SIG_INVALID",
                    errorMessage: "Invalid signature on return",
                    amountVnd: amountVnd,
                    transactionId: !string.IsNullOrWhiteSpace(requestId) ? requestId : transId,
                    ct: ct
                );
            }

            return HtmlRedirect(ComposeFeUrl(
                $"{FE_CHECKOUT_CONFIRM}?payfail=1&prov=momo&sig=0&code={Uri.EscapeDataString(orderCode)}",
                forceDevLocal: true));
        }

        var okRc = int.TryParse(resultCodeRaw, out var rc) && rc == 0;

        if (_flags.Value.ConfirmOnReturnIfOk && okRc && !string.IsNullOrWhiteSpace(orderCode))
        {
            var ok = await ConfirmPaymentAsync(
                orderCode, amountVnd,
                provider: PROV_MOMO, method: METHOD_MOMO,
                forceWhenReturn: true, ct);

            if (ok)
            {
                await TryNotifyAdminPaidOnceAsync(orderCode, PROV_MOMO, METHOD_MOMO, ct);
                await TryNotifyUserPaidOnceAsync(orderCode, PROV_MOMO, METHOD_MOMO, ct);
            }
        }
        else if (!okRc && !string.IsNullOrWhiteSpace(orderCode))
        {
            var newStt = (rc == 1006) ? "Canceled" : "Failed";

            await MarkFailedAndReleaseAsync(
                orderCode: orderCode,
                provider: PROV_MOMO,
                method: METHOD_MOMO,
                newStatus: newStt,
                errorCode: "RC_" + rc,
                errorMessage: message ?? "",
                amountVnd: amountVnd,
                transactionId: !string.IsNullOrWhiteSpace(requestId) ? requestId : transId,
                ct: ct
            );
        }

        var url = okRc
            ? (string.IsNullOrWhiteSpace(orderCode)
                ? ComposeFeUrl(FE_THANKYOU, forceDevLocal: true)
                : ComposeFeUrl($"{FE_THANKYOU}?code={Uri.EscapeDataString(orderCode)}", forceDevLocal: true))
            : (string.IsNullOrWhiteSpace(orderCode)
                ? ComposeFeUrl($"{FE_CHECKOUT_CONFIRM}?payfail=1&prov=momo&rc={Uri.EscapeDataString(resultCodeRaw ?? "")}", forceDevLocal: true)
                : ComposeFeUrl($"{FE_CHECKOUT_CONFIRM}?payfail=1&prov=momo&rc={Uri.EscapeDataString(resultCodeRaw ?? "")}&code={Uri.EscapeDataString(orderCode)}", forceDevLocal: true));

        return HtmlRedirect(url);
    }

    // =========================================================
    // IPN (SERVER-TO-SERVER) - MOMO
    // =========================================================
    public sealed class MomoIpnModel
    {
        public string partnerCode { get; set; } = "";
        public string orderId { get; set; } = ""; // ✅ providerOrderId
        public string requestId { get; set; } = "";
        public long amount { get; set; }
        public string orderInfo { get; set; } = "";
        public string orderType { get; set; } = "";
        public long transId { get; set; }
        public int resultCode { get; set; }
        public string message { get; set; } = "";
        public string payType { get; set; } = "";
        public long responseTime { get; set; }
        public string extraData { get; set; } = "";
        public string signature { get; set; } = "";
    }

    [HttpPost("payment/momo-ipn")]
    public async Task<IActionResult> MomoIpn(CancellationToken ct)
    {
        MomoIpnModel? model;

        string body = "";
        try
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            body = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return Ok(new { resultCode = 1, message = "EMPTY_BODY" });

            model = JsonSerializer.Deserialize<MomoIpnModel>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "MOMO IPN read/parse failed body={body}", body);
            return Ok(new { resultCode = 1, message = "PARSE_FAILED" });
        }

        if (model == null || string.IsNullOrWhiteSpace(model.orderId))
            return Ok(new { resultCode = 1, message = "MISSING_ORDERID" });

        var sigOk = _momo.ValidateResultSignature(
            amount: model.amount.ToString(CultureInfo.InvariantCulture),
            extraData: model.extraData ?? "",
            message: model.message ?? "",
            orderId: model.orderId ?? "",
            orderInfo: model.orderInfo ?? "",
            orderType: model.orderType ?? "",
            partnerCode: model.partnerCode ?? "",
            payType: model.payType ?? "",
            requestId: model.requestId ?? "",
            responseTime: model.responseTime.ToString(CultureInfo.InvariantCulture),
            resultCode: model.resultCode.ToString(CultureInfo.InvariantCulture),
            transId: model.transId.ToString(CultureInfo.InvariantCulture),
            receivedSignature: model.signature ?? ""
        );

        if (!sigOk)
        {
            _log.LogWarning("MOMO IPN invalid signature orderId={orderId} requestId={requestId}", model.orderId, model.requestId);
            return Ok(new { resultCode = 1, message = "SIG_INVALID" });
        }

        // ✅ Resolve orderCode thật
        var orderCode = (await ResolveOrderCodeAsync(model.orderId, ct)) ?? model.orderId.Trim();
        var amountVnd = model.amount > 0 ? (long?)model.amount : null;

        if (model.resultCode == 0)
        {
            var ok = await ConfirmPaymentAsync(orderCode, amountVnd, provider: PROV_MOMO, method: METHOD_MOMO, forceWhenReturn: false, ct);
            if (ok)
            {
                await TryNotifyAdminPaidOnceAsync(orderCode, PROV_MOMO, METHOD_MOMO, ct);
                await TryNotifyUserPaidOnceAsync(orderCode, PROV_MOMO, METHOD_MOMO, ct);
            }

            return Ok(new { resultCode = 0, message = "OK" });
        }

        var newStt = (model.resultCode == 1006) ? "Canceled" : "Failed";

        await MarkFailedAndReleaseAsync(
            orderCode: orderCode,
            provider: PROV_MOMO,
            method: METHOD_MOMO,
            newStatus: newStt,
            errorCode: "RC_" + model.resultCode,
            errorMessage: model.message ?? "",
            amountVnd: amountVnd,
            transactionId: !string.IsNullOrWhiteSpace(model.requestId) ? model.requestId : model.transId.ToString(CultureInfo.InvariantCulture),
            ct: ct
        );

        return Ok(new { resultCode = 0, message = "OK" });
    }

    // =========================================================
    // USER RETURN (UI) - PAY2S
    // =========================================================
    [HttpGet("payment/pay2s-return")]
    public async Task<IActionResult> Pay2SReturn(CancellationToken ct)
    {
        _log.LogInformation("PAY2S REDIRECT query: {q}", Request.QueryString.Value);

        var amountRaw = QFirst(Request.Query, "amount");
        var message = QFirst(Request.Query, "message");
        var providerOrderId = QFirst(Request.Query, "orderId"); // ✅ giờ là providerOrderId
        var requestId = QFirst(Request.Query, "requestId");
        var resultCodeRaw = QFirst(Request.Query, "resultCode");

        // ✅ Resolve orderCode thật
        var orderCode = (await ResolveOrderCodeAsync(providerOrderId, ct)) ?? (providerOrderId ?? "").Trim();

        long? amountVnd = null;
        if (long.TryParse(amountRaw, out var a) && a > 0) amountVnd = a;

        var sigOk = _pay2s.ValidateRedirectSignature(Request.Query, Request.QueryString.Value);

        if (!sigOk)
        {
            _log.LogWarning("PAY2S REDIRECT invalid signature providerOrderId={orderId} requestId={requestId}", providerOrderId, requestId);

            if (!string.IsNullOrWhiteSpace(orderCode))
            {
                await MarkFailedAndReleaseAsync(
                    orderCode: orderCode,
                    provider: PROV_PAY2S,
                    method: METHOD_PAY2S,
                    newStatus: "Failed",
                    errorCode: "SIG_INVALID",
                    errorMessage: "Invalid signature on return",
                    amountVnd: amountVnd,
                    transactionId: requestId,
                    ct: ct
                );
            }

            return HtmlRedirect(ComposeFeUrl(
                $"{FE_CHECKOUT_CONFIRM}?payfail=1&prov=pay2s&sig=0&code={Uri.EscapeDataString(orderCode)}",
                forceDevLocal: true));
        }

        var okRc = int.TryParse(resultCodeRaw, out var rc) && rc == 0;

        if (_flags.Value.ConfirmOnReturnIfOk && okRc && !string.IsNullOrWhiteSpace(orderCode))
        {
            var ok = await ConfirmPaymentAsync(
                orderCode, amountVnd,
                provider: PROV_PAY2S, method: METHOD_PAY2S,
                forceWhenReturn: true, ct);

            if (ok)
            {
                await TryNotifyAdminPaidOnceAsync(orderCode, PROV_PAY2S, METHOD_PAY2S, ct);
                await TryNotifyUserPaidOnceAsync(orderCode, PROV_PAY2S, METHOD_PAY2S, ct);
            }
        }
        else if (!okRc && !string.IsNullOrWhiteSpace(orderCode))
        {
            await MarkFailedAndReleaseAsync(
                orderCode: orderCode,
                provider: PROV_PAY2S,
                method: METHOD_PAY2S,
                newStatus: "Failed",
                errorCode: "RC_" + rc,
                errorMessage: message ?? "",
                amountVnd: amountVnd,
                transactionId: requestId,
                ct: ct
            );
        }

        var url = okRc
            ? (string.IsNullOrWhiteSpace(orderCode)
                ? ComposeFeUrl(FE_THANKYOU, forceDevLocal: true)
                : ComposeFeUrl($"{FE_THANKYOU}?code={Uri.EscapeDataString(orderCode)}", forceDevLocal: true))
            : (string.IsNullOrWhiteSpace(orderCode)
                ? ComposeFeUrl($"{FE_CHECKOUT_CONFIRM}?payfail=1&prov=pay2s&rc={Uri.EscapeDataString(resultCodeRaw ?? "")}", forceDevLocal: true)
                : ComposeFeUrl($"{FE_CHECKOUT_CONFIRM}?payfail=1&prov=pay2s&rc={Uri.EscapeDataString(resultCodeRaw ?? "")}&code={Uri.EscapeDataString(orderCode)}", forceDevLocal: true));

        return HtmlRedirect(url);
    }

    // =========================================================
    // Utils
    // =========================================================
    private static string QFirst(IQueryCollection q, string key)
        => q.TryGetValue(key, out var v) ? (v.FirstOrDefault() ?? "") : "";

    private string ComposeFeUrl(string pathAndQuery, bool forceDevLocal)
    {
        string baseUrl;

        if (forceDevLocal && _env.IsDevelopment() && !string.IsNullOrWhiteSpace(DEV_FE_BASE))
        {
            baseUrl = DEV_FE_BASE.TrimEnd('/');
        }
        else
        {
            baseUrl = (_fe?.Value?.BaseUrl ?? "").TrimEnd('/');
        }

        if (string.IsNullOrEmpty(baseUrl))
        {
            return pathAndQuery.StartsWith("/") ? pathAndQuery : "/" + pathAndQuery;
        }

        if (!pathAndQuery.StartsWith("/")) pathAndQuery = "/" + pathAndQuery;
        return baseUrl + pathAndQuery;
    }

    // =========================================================
    // ✅ CONFIRM PAID: gọi thẳng USP
    // =========================================================
    private async Task<bool> ConfirmPaymentAsync(
        string orderCode,
        long? amountVnd,
        string provider,
        int method,
        bool forceWhenReturn,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) return false;
        if (!amountVnd.HasValue && !forceWhenReturn) return false;

        using var con = _db.Create();
        if (con is DbConnection dbc) await dbc.OpenAsync(ct);
        else con.Open();

        var p = new DynamicParameters();
        p.Add("@order_code", orderCode);
        p.Add("@provider", provider);
        p.Add("@method", (byte)method);
        p.Add("@amount_vnd", amountVnd);
        p.Add("@transaction_id", Guid.NewGuid().ToString("N"));
        p.Add("@result", dbType: DbType.Boolean, direction: ParameterDirection.Output);

        await con.ExecuteAsync(new CommandDefinition(
            "dbo.usp_payment_confirm_paid",
            p,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct,
            commandTimeout: 30
        ));

        return p.Get<bool>("@result");
    }

    private async Task MarkFailedAndReleaseAsync(
        string orderCode,
        string provider,
        int method,
        string newStatus,
        string? errorCode,
        string? errorMessage,
        long? amountVnd,
        string? transactionId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) return;

        using var con = _db.Create();
        if (con is DbConnection dbc) await dbc.OpenAsync(ct);
        else con.Open();

        var p = new DynamicParameters();
        p.Add("@order_code", orderCode);
        p.Add("@provider", provider);
        p.Add("@method", (byte)method);
        p.Add("@new_status", newStatus);
        p.Add("@error_code", errorCode);
        p.Add("@error_message", errorMessage ?? "");
        p.Add("@amount_vnd", amountVnd);
        p.Add("@transaction_id", transactionId);

        await con.ExecuteAsync(new CommandDefinition(
            "dbo.usp_payment_mark_failed_and_release",
            p,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct,
            commandTimeout: 30
        ));
    }

    // =========================================================
    // Notify admin once (idempotent)
    // =========================================================
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
                (@oid, @prov, @meth, 1,
                 CAST(@amt AS decimal(12,2)), 'VND', @tx, @mref,
                 'ADMIN_TG_PAID', 'Telegram notified when paid', SYSDATETIME(), SYSDATETIME())
            """,
            new
            {
                oid = orderId,
                prov = provider,
                meth = method,
                amt = (decimal)row.pay_total,
                tx = Guid.NewGuid().ToString("N"),
                mref = (string)row.order_code
            },
            cancellationToken: ct,
            commandTimeout: 15));

        string fullAddr = (string?)row.ship_full_address ?? "";
        string? shortAddr = string.IsNullOrWhiteSpace(fullAddr)
            ? null
            : (fullAddr.Length <= 100 ? fullAddr : fullAddr.Substring(0, 97) + "...");

        var notifier = HttpContext.RequestServices.GetRequiredService<IAdminOrderNotifier>();

        await notifier.NotifyNewOrderAsync(
            (long)row.id,
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

    // =========================================================
    // Mark Pending + Log create (y nguyên nhưng pref giờ là providerOrderId)
    // =========================================================
    private async Task<(bool ok, long orderId, decimal payTotal, string? errCode)> MarkPendingAndLogCreateAsync(
        string orderCode,
        long amountVnd,
        string provider,
        int method,
        string transactionId, // ✅ sẽ là providerOrderId (Momo/Pay2S)
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) return (false, 0, 0m, "ORDER_CODE_EMPTY");
        if (amountVnd <= 0) return (false, 0, 0m, "AMOUNT_INVALID");

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
                return (false, 0, 0m, "ORDER_NOT_FOUND");
            }

            long orderId = (long)o.id;
            decimal payTotal = (decimal)o.pay_total;
            string? payStatus = (string?)o.payment_status;

            if (string.Equals(payStatus, "Paid", StringComparison.OrdinalIgnoreCase))
            {
                tx.Rollback();
                return (false, orderId, payTotal, "ORDER_ALREADY_PAID");
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
                        txid = transactionId ?? Guid.NewGuid().ToString("N"),
                        mref = orderCode
                    },
                    transaction: tx,
                    cancellationToken: ct,
                    commandTimeout: 15));

                tx.Commit();
                return (false, orderId, payTotal, "AMOUNT_MISMATCH");
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
                new { id = orderId, prov = provider, pref = transactionId },
                transaction: tx,
                cancellationToken: ct,
                commandTimeout: 15));

            await con.ExecuteAsync(new CommandDefinition(
                """
                INSERT dbo.tbl_payment_transaction
                    (order_id, provider, method, status, amount, currency, transaction_id, merchant_ref,
                     created_at, updated_at)
                VALUES
                    (@oid, @prov, @meth, 0, CAST(@amt AS decimal(12,2)), 'VND', @txid, @mref,
                     SYSDATETIME(), SYSDATETIME())
                """,
                new
                {
                    oid = orderId,
                    prov = provider,
                    meth = method,
                    amt = expected,
                    txid = transactionId ?? Guid.NewGuid().ToString("N"),
                    mref = orderCode
                },
                transaction: tx,
                cancellationToken: ct,
                commandTimeout: 15));

            tx.Commit();
            return (true, orderId, payTotal, null);
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            _log.LogError(ex, "MarkPendingAndLogCreateAsync failed code={code} prov={prov}", orderCode, provider);
            return (false, 0, 0m, "DB_ERROR");
        }
    }

    private ContentResult HtmlRedirect(string url)
    {
        var safeUrl = System.Net.WebUtility.HtmlEncode(url);

        var html = $@"<!doctype html>
<html lang=""vi"">
<head>
  <meta charset=""utf-8"" />
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
  <meta http-equiv=""refresh"" content=""0;url={safeUrl}"" />
  <title>Redirecting…</title>
</head>
<body>
  <script>
    try {{
      window.location.replace({System.Text.Json.JsonSerializer.Serialize(url)});
    }} catch(e) {{
      window.location.href = {System.Text.Json.JsonSerializer.Serialize(url)};
    }}
  </script>
  <p>Đang chuyển về trang thanh toán…</p>
  <p>Nếu không tự chuyển, bấm: <a href=""{safeUrl}"">Quay về</a></p>
</body>
</html>";

        return Content(html, "text/html; charset=utf-8");
    }

    private async Task<bool> TryNotifyUserPaidOnceAsync(
        string orderCode,
        string provider,
        int method,
        CancellationToken ct)
    {
        using var con = _db.Create();

        var row = await con.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            """
            SELECT id, user_info_id, order_code, pay_total, placed_at, paid_at
            FROM dbo.tbl_orders
            WHERE order_code=@c AND payment_status='Paid'
            """,
            new { c = orderCode },
            cancellationToken: ct,
            commandTimeout: 15));

        if (row == null) return false;

        long orderId = (long)row.id;
        long userId = (long)row.user_info_id;
        decimal payTotal = (decimal)row.pay_total;

        var existed = await con.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(1)
            FROM dbo.tbl_payment_transaction
            WHERE order_id=@oid AND provider=@prov AND error_code='USER_INAPP_PAID'
            """,
            new { oid = orderId, prov = provider },
            cancellationToken: ct,
            commandTimeout: 15));

        if (existed > 0) return true;

        await con.ExecuteAsync(new CommandDefinition(
            """
            INSERT dbo.tbl_payment_transaction
                (order_id, provider, method, status, amount, currency, transaction_id, merchant_ref,
                 error_code, error_message, created_at, updated_at, paid_at)
            VALUES
                (@oid, @prov, @meth, 1, CAST(@amt AS decimal(12,2)), 'VND', @tx, @mref,
                 'USER_INAPP_PAID', 'InApp notified when paid', SYSDATETIME(), SYSDATETIME(), SYSDATETIME())
            """,
            new
            {
                oid = orderId,
                prov = provider,
                meth = method,
                amt = payTotal,
                tx = Guid.NewGuid().ToString("N"),
                mref = (string)row.order_code
            },
            cancellationToken: ct,
            commandTimeout: 15));

        int estPoints = payTotal > 0 ? (int)Math.Floor(payTotal / 1000m) : 0;

        var dataObj = new
        {
            order_id = orderId,
            order_code = (string)row.order_code,
            provider = provider,
            method = method,
            pay_total = payTotal,
            paid_at = (DateTime?)row.paid_at ?? (DateTime?)row.placed_at,
            est_points = estPoints
        };
        var dataJson = JsonSerializer.Serialize(dataObj);

        var title = $"Đơn {(string)row.order_code} đã thanh toán thành công";
        var body = estPoints > 0
            ? $"HAFood đã nhận được thanh toán. Nếu giao thành công, bạn sẽ nhận khoảng +{estPoints} điểm HAFood."
            : "HAFood đã nhận được thanh toán cho đơn hàng của bạn.";

        var notifications = HttpContext.RequestServices.GetRequiredService<INotificationService>();

        await notifications.CreateInAppAsync(
            userId,
            NotificationTypes.ORDER_STATUS_CHANGED,
            title,
            body,
            dataJson,
            ct);

        return true;
    }
}
