// HAShop.Api/Payments/ZaloPayService.cs
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HAShop.Api.Payments
{
    public class ZaloPayService : IZaloPayGateway
    {
        private readonly ZaloPayOptions _opt;
        private readonly ILogger<ZaloPayService> _log;

        public ZaloPayService(IOptions<ZaloPayOptions> opt, ILogger<ZaloPayService> log)
        {
            var v = opt.Value;
            v.Key1 = (v.Key1 ?? "").Trim();
            v.Key2 = (v.Key2 ?? "").Trim();
            v.CreateUrl = (v.CreateUrl ?? "").Trim();
            v.QueryUrl = (v.QueryUrl ?? "").Trim();
            v.RefundUrl = (v.RefundUrl ?? "").Trim();
            v.ReturnUrl = (v.ReturnUrl ?? "").Trim();
            v.IpnUrl = string.IsNullOrWhiteSpace(v.IpnUrl) ? null : v.IpnUrl.Trim();

            _opt = v;
            _log = log;
        }

        // Tạo app_trans_id mới theo format yymmdd_..._rnd để tránh trùng khi xin link nhiều lần
        private static string NewAppTransId(string orderCode)
        {
            var ymd = DateTime.UtcNow.ToString("yyMMdd"); // ZP yêu cầu yymmdd ở đầu
            var rnd = RandomNumberGenerator.GetInt32(100, 1000); // 100..999
            var tail = string.IsNullOrWhiteSpace(orderCode)
                ? "x"
                : (orderCode.Length > 20 ? orderCode[^20..] : orderCode);

            var raw = $"{ymd}_{tail}_{rnd}";
            return raw.Length <= 64 ? raw : raw[..64];
        }

        public async Task<ZpCreateOrderResult> CreateOrderAsync(
    string orderCode, long amountVnd, string description,
    string? appUser, string? clientReturnUrl, CancellationToken ct)
        {
            // ❗️Mỗi lần tạo link phải có app_trans_id mới → tránh sub_return_code = -68 (Mã giao dịch bị trùng)
            var app_trans_id = NewAppTransId(orderCode);
            var app_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // ✅ Redirect URL: CHỈ append code, KHÔNG append apptransid (ZaloPay tự gắn apptransid vào query)
            var baseRet = string.IsNullOrWhiteSpace(clientReturnUrl) ? _opt.ReturnUrl : clientReturnUrl;

            // Nếu baseRet chưa có query -> ?, có rồi -> &
            var retWithQuery = baseRet.Contains('?')
                ? $"{baseRet}&code={Uri.EscapeDataString(orderCode)}"
                : $"{baseRet}?code={Uri.EscapeDataString(orderCode)}";

            // embed_data: redirecturl + merchantinfo
            var embed = new
            {
                redirecturl = retWithQuery,
                merchantinfo = new { orderCode, app_trans_id }
            };
            var embedJson = JsonSerializer.Serialize(embed);
            var itemJson = "[]";

            // mac = HMAC_SHA256(app_id|app_trans_id|app_user|amount|app_time|embed_data|item)
            var macRaw = $"{_opt.AppId}|{app_trans_id}|{(appUser ?? "guest")}|{amountVnd}|{app_time}|{embedJson}|{itemJson}";
            var mac = HmacSha256Hex(_opt.Key1, macRaw, _opt.LowercaseMac);

            var body = new Dictionary<string, object?>
            {
                ["app_id"] = _opt.AppId,
                ["app_trans_id"] = app_trans_id,
                ["app_user"] = appUser ?? "guest",
                ["app_time"] = app_time,
                ["amount"] = amountVnd,
                ["item"] = itemJson,
                ["embed_data"] = embedJson,
                ["description"] = string.IsNullOrWhiteSpace(description) ? $"Thanh toan don {orderCode}" : description,
                ["mac"] = mac,
                ["callback_url"] = _opt.IpnUrl
            };

            _log.LogInformation("ZP create raw: {raw}", macRaw);

            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, _opt.CreateUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

            var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            _log.LogInformation("ZP create resp: {code} {text}", (int)resp.StatusCode, text);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"ZaloPay /create HTTP {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var return_code = root.GetProperty("return_code").GetInt32(); // 1 = success
            if (return_code != 1)
            {
                var sub = root.TryGetProperty("sub_return_message", out var subEl) ? subEl.GetString() : null;
                var msg = root.TryGetProperty("return_message", out var msgEl) ? msgEl.GetString() : null;
                var reason = !string.IsNullOrWhiteSpace(sub) ? sub : (msg ?? "(no message)");
                throw new InvalidOperationException($"ZaloPay /create error: {return_code} - {reason}");
            }

            var orderUrl = root.TryGetProperty("order_url", out var ou) ? ou.GetString() : null;
            var qrCode = root.TryGetProperty("qr_code", out var qr) ? qr.GetString() : null;
            var token = root.TryGetProperty("zp_trans_token", out var tk) ? tk.GetString() : null;

            if (string.IsNullOrWhiteSpace(orderUrl) || string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("ZaloPay /create missing order_url or token.");

            return new ZpCreateOrderResult(orderUrl!, qrCode, token!, app_trans_id);
        }


        public bool ValidateIpn(IDictionary<string, string> fields, out string raw, out string computed)
        {
            var data = fields.TryGetValue("data", out var d) ? d : "";
            raw = data;
            computed = HmacSha256Hex(_opt.Key2, data, _opt.LowercaseMac);
            var received = fields.TryGetValue("mac", out var mac) ? mac : "";
            var ok = received.Equals(computed, StringComparison.OrdinalIgnoreCase);
            if (!ok) _log.LogWarning("ZP IPN MAC FAIL. computed={c} received={r}", computed, received);
            return ok;
        }

        public bool ValidateReturn(IDictionary<string, string> fields, out string raw, out string computed)
        {
            // Return dùng cùng cách kiểm tra với IPN ở phía ZP
            return ValidateIpn(fields, out raw, out computed);
        }

        // Query trạng thái giao dịch theo app_trans_id
        public async Task<ZpQueryResult> QueryAsync(string appTransId, CancellationToken ct)
        {
            // mac = HMAC_SHA256(app_id|app_trans_id|key1)
            var macRaw = $"{_opt.AppId}|{appTransId}|{_opt.Key1}";
            var mac = HmacSha256Hex(_opt.Key1, macRaw, _opt.LowercaseMac);

            var body = new Dictionary<string, object?>
            {
                ["app_id"] = _opt.AppId,
                ["app_trans_id"] = appTransId,
                ["mac"] = mac
            };

            using var http = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, _opt.QueryUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

            var resp = await http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            _log.LogInformation("ZP query resp: {code} {text}", (int)resp.StatusCode, text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var rc = root.GetProperty("return_code").GetInt32();
            var msg = root.TryGetProperty("return_message", out var m) ? m.GetString() : null;
            long? amt = root.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt64() : null;
            var zpid = root.TryGetProperty("zp_trans_id", out var z) ? z.ToString() : null;

            return new ZpQueryResult(rc, msg, amt, zpid);
        }

        private static string HmacSha256Hex(string key, string data, bool lower)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            var hash = h.ComputeHash(Encoding.UTF8.GetBytes(data));
            var sb = new StringBuilder(hash.Length * 2);
            var fmt = lower ? "x2" : "X2";
            foreach (var b in hash) sb.Append(b.ToString(fmt));
            return sb.ToString();
        }
    }
}
