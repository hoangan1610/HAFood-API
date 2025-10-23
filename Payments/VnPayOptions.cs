namespace HAShop.Api.Payments
{
    public class VnPayOptions
    {
        public string TmnCode { get; set; } = "";
        public string HashSecret { get; set; } = "";
        public string PayUrl { get; set; } = "";
        public string ReturnUrl { get; set; } = "";
        public string? IpnUrl { get; set; }

        // Tùy chỉnh tương thích signature/encode
        public bool CompatEncodeWithPlus { get; set; } = true; // << đổi sang true
        public bool LowercaseHash { get; set; } = true;        // << đổi sang true
        public bool AppendSecureHashType { get; set; } = false;// << bắt buộc false (đừng gửi vnp_SecureHashType)
        public int? ExpireMinutes { get; set; } = 15;
    }
}
