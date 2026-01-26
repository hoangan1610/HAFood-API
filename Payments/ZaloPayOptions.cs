// HAShop.Api/Payments/ZaloPayOptions.cs
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HAShop.Api.Payments
{
    public sealed class ZaloPayOptions
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

    // ✅ Records phải nằm trong cùng namespace HAShop.Api.Payments
    public sealed record ZpCreateOrderResult(
        string order_url,
        string? qr_code,
        string zp_trans_token,
        string app_trans_id
    );

    public sealed record ZpQueryResult(
        int return_code,
        string? return_message,
        long? amount,
        string? zp_trans_id
    );

    public interface IZaloPayGateway
    {
        Task<ZpCreateOrderResult> CreateOrderAsync(
            string orderCode,
            long amountVnd,
            string description,
            string? appUser,
            string? clientReturnUrl,
            CancellationToken ct);

        // IPN/Return dạng key-value
        bool ValidateIpn(IDictionary<string, string> fields, out string raw, out string computed);
        bool ValidateReturn(IDictionary<string, string> fields, out string raw, out string computed);

        Task<ZpQueryResult> QueryAsync(string appTransId, CancellationToken ct);
    }
}
