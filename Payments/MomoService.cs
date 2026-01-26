using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HAShop.Api.Payments;

public sealed class MomoService
{
    private readonly MomoOptions _opt;
    private readonly ILogger<MomoService> _log;
    private readonly HttpClient _http;

    public MomoService(HttpClient http, IOptions<MomoOptions> opt, ILogger<MomoService> log)
    {
        _http = http;
        _log = log;

        var v = opt.Value;
        v.PartnerCode = (v.PartnerCode ?? "").Trim();
        v.AccessKey = (v.AccessKey ?? "").Trim();
        v.SecretKey = (v.SecretKey ?? "").Trim();
        v.BaseUrl = (v.BaseUrl ?? "").Trim().TrimEnd('/');
        v.RedirectUrl = (v.RedirectUrl ?? "").Trim();
        v.IpnUrl = (v.IpnUrl ?? "").Trim();
        v.RequestType = string.IsNullOrWhiteSpace(v.RequestType) ? "captureWallet" : v.RequestType.Trim();
        v.Lang = string.IsNullOrWhiteSpace(v.Lang) ? "vi" : v.Lang.Trim();

        _opt = v;
    }

    public sealed record CreateResult(
        string payUrl,
        string? qrCodeUrl,
        string? deeplink,
        long amount,
        string orderId,   // ✅ providerOrderId (unique)
        string requestId);

    public async Task<CreateResult> CreatePaymentAsync(
        string orderCode,
        long amountVnd,
        string? orderInfo,
        string? extraDataBase64,
        CancellationToken ct,
        string? redirectUrlOverride = null,
        string? ipnUrlOverride = null)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) throw new ArgumentException("orderCode is empty");
        if (amountVnd < 1000) throw new ArgumentException("amountVnd must be >= 1000");
        if (string.IsNullOrWhiteSpace(_opt.BaseUrl)) throw new InvalidOperationException("MoMo.BaseUrl missing");
        if (string.IsNullOrWhiteSpace(_opt.RedirectUrl)) throw new InvalidOperationException("MoMo.RedirectUrl missing");
        if (string.IsNullOrWhiteSpace(_opt.IpnUrl)) throw new InvalidOperationException("MoMo.IpnUrl missing");
        if (string.IsNullOrWhiteSpace(_opt.PartnerCode)) throw new InvalidOperationException("MoMo.PartnerCode missing");
        if (string.IsNullOrWhiteSpace(_opt.AccessKey)) throw new InvalidOperationException("MoMo.AccessKey missing");
        if (string.IsNullOrWhiteSpace(_opt.SecretKey)) throw new InvalidOperationException("MoMo.SecretKey missing");

        var endpoint = _opt.BaseUrl + "/v2/gateway/api/create";

        var requestId = Guid.NewGuid().ToString("N"); // <= 50 chars

        // ✅ FIX: orderId phải unique mỗi lần create link (tránh resultCode=41)
        var orderId = PaymentIdUtil.NewProviderOrderId(orderCode.Trim());

        var info = string.IsNullOrWhiteSpace(orderInfo)
            ? $"Thanh toan don {orderCode.Trim()}" // hiển thị mã đơn thật
            : orderInfo!.Trim();

        var extraData = extraDataBase64 ?? "";

        var redirectUrl = string.IsNullOrWhiteSpace(redirectUrlOverride)
            ? _opt.RedirectUrl
            : redirectUrlOverride!.Trim();

        var ipnUrl = string.IsNullOrWhiteSpace(ipnUrlOverride)
            ? _opt.IpnUrl
            : ipnUrlOverride!.Trim();

        // rawHash đúng format docs v2 (sorted field order)
        var rawHash =
            $"accessKey={_opt.AccessKey}" +
            $"&amount={amountVnd}" +
            $"&extraData={extraData}" +
            $"&ipnUrl={ipnUrl}" +
            $"&orderId={orderId}" +
            $"&orderInfo={info}" +
            $"&partnerCode={_opt.PartnerCode}" +
            $"&redirectUrl={redirectUrl}" +
            $"&requestId={requestId}" +
            $"&requestType={_opt.RequestType}";

        var signature = HmacSha256HexLower(_opt.SecretKey, rawHash);

        object payload = _opt.IncludeAccessKeyInPayload
            ? new
            {
                partnerCode = _opt.PartnerCode,
                accessKey = _opt.AccessKey,
                requestId,
                amount = amountVnd,
                orderId,
                orderInfo = info,
                redirectUrl,
                ipnUrl,
                requestType = _opt.RequestType,
                extraData,
                autoCapture = _opt.AutoCapture,
                lang = _opt.Lang,
                signature
            }
            : new
            {
                partnerCode = _opt.PartnerCode,
                requestId,
                amount = amountVnd,
                orderId,
                orderInfo = info,
                redirectUrl,
                ipnUrl,
                requestType = _opt.RequestType,
                extraData,
                autoCapture = _opt.AutoCapture,
                lang = _opt.Lang,
                signature
            };

        var json = JsonSerializer.Serialize(payload);

        _log.LogInformation(
            "MOMO create providerOrderId={orderId} requestId={requestId} redirectUrl={redirectUrl} ipnUrl={ipnUrl} rawHash={rawHash}",
            orderId, requestId, redirectUrl, ipnUrl, rawHash);

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        _log.LogInformation("MOMO create status={st} body={body}", (int)resp.StatusCode, body);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MoMo create HTTP {(int)resp.StatusCode}. Body={body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        int resultCode = root.TryGetProperty("resultCode", out var rc) && rc.ValueKind == JsonValueKind.Number ? rc.GetInt32() : -1;
        string message = root.TryGetProperty("message", out var msg) ? (msg.GetString() ?? "") : "";

        if (resultCode != 0)
            throw new InvalidOperationException($"MoMo create failed: resultCode={resultCode} message={message}. Body={body}");

        var payUrl = root.TryGetProperty("payUrl", out var p) ? (p.GetString() ?? "") : "";
        if (string.IsNullOrWhiteSpace(payUrl))
            throw new InvalidOperationException("MoMo create: missing payUrl. Body=" + body);

        string? qrCodeUrl = root.TryGetProperty("qrCodeUrl", out var q) ? q.GetString() : null;
        string? deeplink = root.TryGetProperty("deeplink", out var d) ? d.GetString() : null;

        return new CreateResult(payUrl, qrCodeUrl, deeplink, amountVnd, orderId, requestId);
    }

    public bool ValidateResultSignature(
        string amount,
        string extraData,
        string message,
        string orderId,
        string orderInfo,
        string orderType,
        string partnerCode,
        string payType,
        string requestId,
        string responseTime,
        string resultCode,
        string transId,
        string receivedSignature)
    {
        var rawHash =
            $"accessKey={_opt.AccessKey}" +
            $"&amount={amount}" +
            $"&extraData={extraData}" +
            $"&message={message}" +
            $"&orderId={orderId}" +
            $"&orderInfo={orderInfo}" +
            $"&orderType={orderType}" +
            $"&partnerCode={partnerCode}" +
            $"&payType={payType}" +
            $"&requestId={requestId}" +
            $"&responseTime={responseTime}" +
            $"&resultCode={resultCode}" +
            $"&transId={transId}";

        var expected = HmacSha256HexLower(_opt.SecretKey, rawHash);

        var ok = string.Equals(receivedSignature ?? "", expected, StringComparison.OrdinalIgnoreCase);
        if (!ok)
            _log.LogWarning("MOMO signature FAIL orderId={orderId} requestId={requestId}", orderId, requestId);

        return ok;
    }

    private static string HmacSha256HexLower(string key, string data)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key ?? ""));
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes(data ?? ""));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
