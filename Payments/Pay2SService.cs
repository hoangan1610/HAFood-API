using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HAShop.Api.Payments;

public class Pay2SService
{
    private readonly Pay2SOptions _opt;
    private readonly ILogger<Pay2SService> _log;
    private readonly HttpClient _http;

    public Pay2SService(HttpClient http, IOptions<Pay2SOptions> opt, ILogger<Pay2SService> log)
    {
        _http = http;
        _log = log;

        var v = opt.Value;
        v.PartnerCode = (v.PartnerCode ?? "").Trim();
        v.AccessKey = (v.AccessKey ?? "").Trim();
        v.SecretKey = (v.SecretKey ?? "").Trim();
        v.Endpoint = (v.Endpoint ?? "").Trim();
        v.RedirectUrl = (v.RedirectUrl ?? "").Trim();
        v.IpnUrl = (v.IpnUrl ?? "").Trim();
        v.RequestType = string.IsNullOrWhiteSpace(v.RequestType) ? "pay2s" : v.RequestType.Trim();

        foreach (var b in v.BankAccounts ?? new())
        {
            b.account_number = (b.account_number ?? "").Trim();
            b.bank_id = (b.bank_id ?? "").Trim();
        }

        _opt = v;
    }

    // Pay2S amount là VND (KHÔNG *100) :contentReference[oaicite:2]{index=2}
    public async Task<string> CreatePaymentUrlAsync(string orderCode, long amountVnd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(orderCode)) throw new ArgumentException("orderCode is empty");
        if (amountVnd <= 0) throw new ArgumentException("amountVnd must be > 0");
        if (string.IsNullOrWhiteSpace(_opt.Endpoint)) throw new InvalidOperationException("Pay2S.Endpoint missing");
        if (_opt.BankAccounts == null || _opt.BankAccounts.Count == 0)
            throw new InvalidOperationException("Pay2S.BankAccounts missing (need at least 1)");

        var requestId = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var orderId = orderCode;

        // orderInfo: 10-32 ký tự, chỉ chữ + số, không ký tự đặc biệt :contentReference[oaicite:3]{index=3}
        var orderInfo = BuildOrderInfo(orderCode);

        // rawHash theo docs create: accessKey=...&amount=...&bankAccounts=Array&ipnUrl=...&orderId=...&orderInfo=...&partnerCode=...&redirectUrl=...&requestId=...&requestType=... :contentReference[oaicite:4]{index=4}
        var rawHash =
            $"accessKey={_opt.AccessKey}" +
            $"&amount={amountVnd}" +
            $"&bankAccounts=Array" +
            $"&ipnUrl={_opt.IpnUrl}" +
            $"&orderId={orderId}" +
            $"&orderInfo={orderInfo}" +
            $"&partnerCode={_opt.PartnerCode}" +
            $"&redirectUrl={_opt.RedirectUrl}" +
            $"&requestId={requestId}" +
            $"&requestType={_opt.RequestType}";

        var signature = HmacSha256HexLower(_opt.SecretKey, rawHash);

        var payload = new
        {
            accessKey = _opt.AccessKey,
            partnerCode = _opt.PartnerCode,
            partnerName = "HAFood",
            requestId = requestId,
            amount = amountVnd,
            orderId = orderId,
            orderInfo = orderInfo,
            orderType = _opt.RequestType,
            bankAccounts = _opt.BankAccounts,
            redirectUrl = _opt.RedirectUrl,
            ipnUrl = _opt.IpnUrl,
            requestType = _opt.RequestType,
            signature = signature
        };

        var json = JsonSerializer.Serialize(payload);
        _log.LogInformation("PAY2S create rawHash={rawHash}", rawHash);

        using var req = new HttpRequestMessage(HttpMethod.Post, _opt.Endpoint);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        _log.LogInformation("PAY2S create status={st} body={body}", (int)resp.StatusCode, body);

        resp.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(body);

        if (doc.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.False)
        {
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "Pay2S error";
            throw new InvalidOperationException($"Pay2S create failed: {msg}. Body={body}");
        }

        if (doc.RootElement.TryGetProperty("payUrl", out var p) && p.ValueKind == JsonValueKind.String)
        {
            var payUrl = p.GetString();
            if (!string.IsNullOrWhiteSpace(payUrl)) return payUrl!;
        }

        throw new InvalidOperationException("Pay2S create: missing payUrl. Body=" + body);

    }

    // Redirect signature theo docs (Payment Notification) :contentReference[oaicite:5]{index=5}
    public bool ValidateRedirectSignature(IQueryCollection qs)
    {
        string Get(string k) => qs.TryGetValue(k, out var v) ? v.ToString() : "";

        var rawHash =
            $"accessKey={_opt.AccessKey}" +
            $"&amount={Get("amount")}" +
            $"&message={Get("message")}" +
            $"&orderId={Get("orderId")}" +
            $"&orderInfo={Get("orderInfo")}" +
            $"&orderType={Get("orderType")}" +
            $"&partnerCode={Get("partnerCode")}" +
            $"&payType={Get("payType")}" +
            $"&requestId={Get("requestId")}" +
            $"&responseTime={Get("responseTime")}" +
            $"&resultCode={Get("resultCode")}";

        var expected = HmacSha256HexLower(_opt.SecretKey, rawHash);
        var received = Get("m2signature");

        var ok = string.Equals(received, expected, StringComparison.OrdinalIgnoreCase);
        if (!ok) _log.LogWarning("PAY2S redirect signature FAIL rawHash={rawHash} expected={exp} recv={recv}", rawHash, expected, received);
        return ok;
    }

    // IPN signature theo docs (Instant Payment Notification) :contentReference[oaicite:6]{index=6}
    public bool ValidateIpnSignature(Pay2SIpnModel m)
    {
        var extraData = m.extraData ?? "";

        var rawHash =
            $"accessKey={_opt.AccessKey}" +
            $"&amount={m.amount}" +
            $"&extraData={extraData}" +
            $"&message={m.message}" +
            $"&orderId={m.orderId}" +
            $"&orderInfo={m.orderInfo}" +
            $"&orderType={m.orderType}" +
            $"&partnerCode={m.partnerCode}" +
            $"&payType={m.payType}" +
            $"&requestId={m.requestId}" +
            $"&responseTime={m.responseTime}" +
            $"&resultCode={m.resultCode}" +
            $"&transId={m.transId}";

        var expected = HmacSha256HexLower(_opt.SecretKey, rawHash);
        var received = m.m2signature ?? "";

        var ok = string.Equals(received, expected, StringComparison.OrdinalIgnoreCase);
        if (!ok) _log.LogWarning("PAY2S ipn signature FAIL rawHash={rawHash} expected={exp} recv={recv}", rawHash, expected, received);
        return ok;
    }

    private static string HmacSha256HexLower(string key, string data)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes(data));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string BuildOrderInfo(string orderCode)
    {
        // chỉ chữ + số
        var s = new string(orderCode.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(s)) s = "ORDER";

        s = "TT" + s;
        if (s.Length > 32) s = s.Substring(0, 32);
        if (s.Length < 10) s = s.PadRight(10, '0');
        return s;
    }
}

public class Pay2SIpnModel
{
    public string partnerCode { get; set; } = "";
    public string orderId { get; set; } = "";
    public string requestId { get; set; } = "";
    public long amount { get; set; }
    public string orderInfo { get; set; } = "";
    public string orderType { get; set; } = "";
    public long transId { get; set; }
    public int resultCode { get; set; }
    public string message { get; set; } = "";
    public string payType { get; set; } = "";
    public string? responseTime { get; set; }
    public string? extraData { get; set; }
    public string? m2signature { get; set; }
}
