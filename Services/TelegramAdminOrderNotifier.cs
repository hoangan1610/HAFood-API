using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Services
{
    public sealed class TelegramAdminOrderNotifier : IAdminOrderNotifier
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<TelegramAdminOrderNotifier> _logger;
        private readonly string _botToken;
        private readonly string _adminChatId;
        private readonly string? _orderUrlTemplate;

        private const int TELEGRAM_SAFE_MAX = 3900;
        private static readonly CultureInfo VnCulture = new("vi-VN");

        public TelegramAdminOrderNotifier(
            HttpClient httpClient,
            IConfiguration configuration,
            ILogger<TelegramAdminOrderNotifier> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            _botToken = configuration["Telegram:BotToken"]
                ?? throw new Exception("Missing config: Telegram:BotToken");

            _adminChatId = configuration["Telegram:AdminChatId"]
                ?? throw new Exception("Missing config: Telegram:AdminChatId");

            _orderUrlTemplate = configuration["Telegram:OrderUrlTemplate"];
        }

        public async Task NotifyNewOrderAsync(
            long orderId,
            string orderCode,
            decimal payTotal,
            string shipName,
            string shipPhone,
            string? shipAddressShort,
            byte? paymentMethod,
            DateTime? placedAt,
            CancellationToken ct)
        {
            var totalStr = payTotal.ToString("#,0", VnCulture);

            string? orderUrl = null;
            if (!string.IsNullOrWhiteSpace(_orderUrlTemplate))
            {
                try { orderUrl = string.Format(_orderUrlTemplate, orderId); }
                catch (Exception ex) { _logger.LogWarning(ex, "Invalid Telegram:OrderUrlTemplate format."); }
            }

            string paymentText = paymentMethod switch
            {
                0 => "COD",
                1 => "ZaloPay",
                2 => "Pay2S",
                _ => "Khác"
            };

            string timeText = placedAt.HasValue ? ToVietnamTimeText(placedAt.Value) : "N/A";

            static string H(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

            var sb = new StringBuilder();
            sb.AppendLine("🛒 <b>ĐƠN HÀNG</b>");
            sb.AppendLine($"• Mã đơn: <b>#{H(orderCode)}</b>");
            sb.AppendLine($"• Khách: {H(shipName)}");
            sb.AppendLine($"• SĐT: <code>{H(shipPhone)}</code>");
            sb.AppendLine($"• Thành tiền: <b>{H(totalStr)} đ</b>");
            sb.AppendLine($"• Thanh toán: {H(paymentText)}");
            sb.AppendLine($"• Thời gian: {H(timeText)}");

            if (!string.IsNullOrWhiteSpace(shipAddressShort))
                sb.AppendLine($"• Địa chỉ: {H(shipAddressShort)}");

            if (!string.IsNullOrWhiteSpace(orderUrl))
            {
                sb.AppendLine();
                // ✅ encode attribute để tránh lỗi HTML parse khi url có & ? =
                var safeUrl = WebUtility.HtmlEncode(orderUrl);
                sb.AppendLine($@"<a href=""{safeUrl}"">🔗 Xem chi tiết</a>");
            }

            var text = sb.ToString();
            if (text.Length > TELEGRAM_SAFE_MAX) text = text.Substring(0, TELEGRAM_SAFE_MAX) + "…";

            var payload = new
            {
                chat_id = _adminChatId,
                text,
                parse_mode = "HTML",
                disable_web_page_preview = true
            };

            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                using var resp = await _httpClient.PostAsync(url, content, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Telegram sendMessage failed. Status={StatusCode}, Body={Body}",
                        resp.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NotifyNewOrderAsync (Telegram) failed for order {OrderId}", orderId);
            }
        }

        private static string ToVietnamTimeText(DateTime dt)
        {
            var vn = GetVietnamTimeZone();

            // ✅ datetime2 từ SQL thường Kind=Unspecified
            // Nếu DB đang lưu GIỜ VN (GETDATE()/DateTime.Now) thì coi Unspecified là VN local
            if (dt.Kind == DateTimeKind.Unspecified)
            {
                return dt.ToString("HH:mm dd/MM/yyyy", VnCulture);
            }

            // Còn lại: nếu Utc/Local thì convert về VN
            var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            var vnTime = TimeZoneInfo.ConvertTimeFromUtc(utc, vn);
            return vnTime.ToString("HH:mm dd/MM/yyyy", VnCulture);
        }


        private static TimeZoneInfo GetVietnamTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"); } catch { }
            try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); } catch { }
            return TimeZoneInfo.Local;
        }
    }
}
