// HAShop.Api/DTOs/SwitchPaymentDtos.cs
namespace HAShop.Api.DTOs;

public record SwitchPaymentRequest
{
    public byte New_Method { get; init; }       // 0=COD, 1=ZaloPay, 2=VNPAY
    public string? Reason { get; init; }
}

public record SwitchPaymentResponse
{
    public string Order_Code { get; init; } = "";
    public byte New_Method { get; init; }
    public string New_Status { get; init; } = "";  // LƯU Ý: dùng New_Status (đúng với code controller)
}