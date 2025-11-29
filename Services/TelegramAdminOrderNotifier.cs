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
            var vn = new CultureInfo("vi-VN");
            var totalStr = payTotal.ToString("#,0", vn);

            string? orderUrl = null;
            if (!string.IsNullOrWhiteSpace(_orderUrlTemplate))
            {
                orderUrl = string.Format(_orderUrlTemplate, orderId);
            }

            string paymentText = paymentMethod switch
            {
                0 => "COD",
                1 => "ZaloPay",
                2 => "VNPAY",
                _ => "Khác"
            };

            string timeText = placedAt?.ToLocalTime().ToString("HH:mm dd/MM/yyyy", vn) ?? "N/A";

            string H(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

            var sb = new StringBuilder();
            sb.AppendLine("🛒 <b>ĐƠN HÀNG MỚI</b>");
            sb.AppendLine($"• Mã đơn: <b>#{H(orderCode)}</b>");
            sb.AppendLine($"• Khách: {H(shipName)}");
            sb.AppendLine($"• SĐT: <code>{H(shipPhone)}</code>");
            sb.AppendLine($"• Thành tiền: <b>{H(totalStr)} đ</b>");
            sb.AppendLine($"• Thanh toán: {H(paymentText)}");
            sb.AppendLine($"• Thời gian: {H(timeText)}");

            if (!string.IsNullOrWhiteSpace(shipAddressShort))
            {
                sb.AppendLine($"• Địa chỉ: {H(shipAddressShort)}");
            }

            if (!string.IsNullOrWhiteSpace(orderUrl))
            {
                sb.AppendLine();
                // Không HtmlEncode toàn bộ href, tránh phá http://
                var safeUrl = orderUrl; // vì mình tự control template nên ok
                sb.AppendLine($@"<a href=""{safeUrl}"">🔗 Xem chi tiết</a>");
            }

            var text = sb.ToString();

            var payload = new
            {
                chat_id = _adminChatId,
                text,
                parse_mode = "HTML"
            };

            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";

            var options = new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var json = JsonSerializer.Serialize(payload, options);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                using var resp = await _httpClient.PostAsync(url, content, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "Telegram sendMessage failed. Status={StatusCode}, Body={Body}",
                        resp.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "NotifyNewOrderAsync (Telegram) failed for order {OrderId}", orderId);
            }
        }
    }
}
