using System;
using System.Security.Cryptography;

namespace HAShop.Api.Payments
{
    public static class PaymentIdUtil
    {
        // ✅ Tạo providerOrderId unique mỗi lần tạo link
        // Output chủ yếu là số (phù hợp các cổng hay kén ký tự)
        public static string NewProviderOrderId(string orderCode)
        {
            var code = (orderCode ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code)) code = "ORDER";

            // millis + rnd để tránh trùng tuyệt đối
            var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // 13 digits
            var rnd = RandomNumberGenerator.GetInt32(100, 999);
            return $"{code}{ms}{rnd}";
        }

        // ✅ requestId unique (Pay2S)
        public static string NewRequestId()
        {
            var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rnd = RandomNumberGenerator.GetInt32(100, 999);
            return $"{ms}{rnd}";
        }
    }
}
