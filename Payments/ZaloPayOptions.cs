// HAShop.Api/Payments/ZaloPayOptions.cs
namespace HAShop.Api.Payments
{
    public class ZaloPayOptions
    {
        public int AppId { get; set; }
        public string Key1 { get; set; } = ""; // ký request
        public string Key2 { get; set; } = ""; // verify callback/IPN
        public string CreateUrl { get; set; } = "";
        public string QueryUrl { get; set; } = "";
        public string RefundUrl { get; set; } = "";
        public string ReturnUrl { get; set; } = "";
        public string? IpnUrl { get; set; }
        public bool LowercaseMac { get; set; } = true;
    }

    public interface IZaloPayGateway
    {
        Task<ZpCreateOrderResult> CreateOrderAsync(
            string orderCode, long amountVnd, string description,
            string? appUser, string? clientReturnUrl, CancellationToken ct);

        bool ValidateIpn(IDictionary<string, string> fields, out string raw, out string computed);
        bool ValidateReturn(IDictionary<string, string> fields, out string raw, out string computed);
        Task<ZpQueryResult> QueryAsync(string appTransId, CancellationToken ct);
    }

    // Thêm app_trans_id
    public record ZpCreateOrderResult(
        string order_url,
        string? qr_code,
        string zp_trans_token,
        string app_trans_id
    );
  
    // NEW
    public record ZpQueryResult(int return_code, string? return_message, long? amount, string? zp_trans_id);
}
