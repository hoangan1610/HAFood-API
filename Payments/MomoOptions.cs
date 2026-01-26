namespace HAShop.Api.Payments;

public sealed class MomoOptions
{
    public string PartnerCode { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    // Production: https://payment.momo.vn
    // Sandbox: https://test-payment.momo.vn
    public string BaseUrl { get; set; } = "";

    public string RedirectUrl { get; set; } = "";
    public string IpnUrl { get; set; } = "";

    public string RequestType { get; set; } = "captureWallet";

    public bool AutoCapture { get; set; } = true;
    public string Lang { get; set; } = "vi";

    // ✅ một số môi trường yêu cầu gửi accessKey trong payload, một số thì không
    public bool IncludeAccessKeyInPayload { get; set; } = false;
}
