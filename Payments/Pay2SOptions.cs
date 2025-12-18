namespace HAShop.Api.Payments;

public class Pay2SOptions
{
    public string PartnerCode { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    // sandbox: https://sandbox-payment.pay2s.vn/v1/gateway/api/create
    public string Endpoint { get; set; } = "";

    // redirectUrl & ipnUrl bạn cấu hình (và cũng gửi trong request create)
    public string RedirectUrl { get; set; } = "";
    public string IpnUrl { get; set; } = "";

    // mặc định theo docs: requestType=pay2s
    public string RequestType { get; set; } = "pay2s";

    // bắt buộc phải gửi bankAccounts (signature rawHash dùng literal bankAccounts=Array) :contentReference[oaicite:1]{index=1}
    public List<Pay2SBankAccount> BankAccounts { get; set; } = new();
}

public class Pay2SBankAccount
{
    public string account_number { get; set; } = "";
    public string bank_id { get; set; } = "";
}
