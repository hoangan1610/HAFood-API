using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace HAShop.Api.Payments;

public class VnPayService
{
    private readonly VnPayOptions _opt;
    private readonly ILogger<VnPayService> _log;

    public VnPayService(IOptions<VnPayOptions> opt, ILogger<VnPayService> log)
    {
        // TRIM để tránh ký sai vì khoảng trắng vô tình copy
        var v = opt.Value;
        v.TmnCode = (v.TmnCode ?? "").Trim();
        v.HashSecret = (v.HashSecret ?? "").Trim();
        v.PayUrl = (v.PayUrl ?? "").Trim();
        v.ReturnUrl = (v.ReturnUrl ?? "").Trim();
        v.IpnUrl = string.IsNullOrWhiteSpace(v.IpnUrl) ? null : v.IpnUrl.Trim();

        _opt = v;
        _log = log;
    }

    public string CreatePaymentUrl(string orderCode, long amountVnd, string clientIp, string? orderInfo)
    {
        var nowVN = ToVNTime(DateTime.UtcNow);
        var create = nowVN.ToString("yyyyMMddHHmmss");
        var expire = _opt.ExpireMinutes.HasValue
            ? nowVN.AddMinutes(_opt.ExpireMinutes.Value).ToString("yyyyMMddHHmmss")
            : null;

        var data = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Version"] = "2.1.0",
            ["vnp_Command"] = "pay",
            ["vnp_TmnCode"] = _opt.TmnCode,
            ["vnp_Amount"] = (amountVnd * 100).ToString(), // *100
            ["vnp_CurrCode"] = "VND",
            ["vnp_TxnRef"] = orderCode,
            ["vnp_OrderInfo"] = orderInfo ?? $"Thanh toan don {orderCode}",
            ["vnp_OrderType"] = "other",
            ["vnp_Locale"] = "vn",
            ["vnp_IpAddr"] = string.IsNullOrWhiteSpace(clientIp) ? "127.0.0.1" : clientIp,
            ["vnp_CreateDate"] = create,
            ["vnp_ReturnUrl"] = _opt.ReturnUrl
        };

        if (!string.IsNullOrWhiteSpace(_opt.IpnUrl))
            data["vnp_IpnUrl"] = _opt.IpnUrl!;
        if (!string.IsNullOrEmpty(expire))
            data["vnp_ExpireDate"] = expire;

        // CHẾ ĐỘ ENCODE theo option:
        // CompatEncodeWithPlus=true  -> khoảng trắng thành '+'
        // CompatEncodeWithPlus=false -> khoảng trắng thành '%20'
        var unsigned = _opt.CompatEncodeWithPlus ? BuildQueryPlus(data) : BuildQueryStrict(data);

        // CHỮ KÝ theo option
        var sign = _opt.LowercaseHash
            ? HmacSHA512HexLower(_opt.HashSecret, unsigned)
            : HmacSHA512HexUpper(_opt.HashSecret, unsigned);

        _log.LogInformation("VNPAY unsigned: {unsigned}", unsigned);
        _log.LogInformation("VNPAY sign:     {sign}", sign);

        var sb = new StringBuilder();
        sb.Append(_opt.PayUrl);
        sb.Append('?').Append(unsigned);
        // TUYỆT ĐỐI không gửi vnp_SecureHashType (sandbox hay báo 70)
        if (_opt.AppendSecureHashType) { /* giữ false: không append */ }
        sb.Append("&vnp_SecureHash=").Append(sign);

        var url = sb.ToString();
        _log.LogInformation("VNPAY url:      {url}", url);
        _log.LogInformation("VNPAY mode: return={ret}, ipn={ipn}", _opt.ReturnUrl, _opt.IpnUrl ?? "(null)");
        return url;
    }

    public bool ValidateSignature(IQueryCollection qs)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in qs)
        {
            var k = kv.Key;
            if (!k.StartsWith("vnp_", StringComparison.Ordinal)) continue;
            if (k.Equals("vnp_SecureHash", StringComparison.OrdinalIgnoreCase)) continue;
            if (k.Equals("vnp_SecureHashType", StringComparison.OrdinalIgnoreCase)) continue;
            var v = kv.Value.ToString();
            if (!string.IsNullOrEmpty(v)) fields[k] = v;
        }

        // Phải build lại CHÍNH XÁC cùng chế độ encode như khi tạo URL
        var unsigned = _opt.CompatEncodeWithPlus ? BuildQueryPlus(fields) : BuildQueryStrict(fields);

        var computedUpper = HmacSHA512HexUpper(_opt.HashSecret, unsigned);
        var computedLower = HmacSHA512HexLower(_opt.HashSecret, unsigned);
        var received = qs["vnp_SecureHash"].ToString();

        var ok = received.Equals(computedUpper, StringComparison.OrdinalIgnoreCase)
              || received.Equals(computedLower, StringComparison.OrdinalIgnoreCase);

        if (!ok)
            _log.LogWarning("ValidateSignature FAIL. unsigned={unsigned} computedUpper={upper} received={recv}",
                unsigned, computedUpper, received);

        return ok;
    }

    private static string BuildQueryStrict(SortedDictionary<string, string> dict)
    {
        var sb = new StringBuilder();
        foreach (var kv in dict)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(kv.Key))
              .Append('=')
              .Append(Uri.EscapeDataString(kv.Value));
        }
        return sb.ToString();
    }

    private static string BuildQueryPlus(SortedDictionary<string, string> dict)
    {
        static string Enc(string s)
        {
            // encode chuẩn rồi đổi %20 -> +
            return Uri.EscapeDataString(s).Replace("%20", "+");
        }
        var sb = new StringBuilder();
        foreach (var kv in dict)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Enc(kv.Key)).Append('=').Append(Enc(kv.Value));
        }
        return sb.ToString();
    }

    private static string HmacSHA512HexUpper(string key, string data)
    {
        using var h = new HMACSHA512(Encoding.UTF8.GetBytes(key));
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes(data));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }
    private static string HmacSHA512HexLower(string key, string data)
    {
        using var h = new HMACSHA512(Encoding.UTF8.GetBytes(key));
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes(data));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static DateTime ToVNTime(DateTime utc)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time")); }
        catch { return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")); }
    }
}
