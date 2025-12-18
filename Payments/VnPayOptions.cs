namespace HAShop.Api.Payments
{
    public class VnPayOptions
    {
        public string TmnCode { get; set; } = "";
        public string HashSecret { get; set; } = "";
        public string PayUrl { get; set; } = "";
        public string ReturnUrl { get; set; } = "";

        // Chỉ để bạn lưu cấu hình & log (IPN cấu hình ở portal VNPAY)
        public string? IpnUrl { get; set; }

        // Encode/sign options
        public bool CompatEncodeWithPlus { get; set; } = true;  // space -> '+'
        public bool LowercaseHash { get; set; } = false;        // khuyến nghị false => uppercase
        public int? ExpireMinutes { get; set; } = 15;
    }
}
