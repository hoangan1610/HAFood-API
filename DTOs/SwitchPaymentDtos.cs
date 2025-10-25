// HAShop.Api/DTOs/SwitchPaymentDtos.cs
namespace HAShop.Api.DTOs;

public record SwitchPaymentRequest(
    byte New_Method,          // 0=COD, 1=MoMo, 2=VNPAY
    string? Reason = null     // ví dụ "USER_CANCELLED_GATEWAY"
);

public record SwitchPaymentResponse(
    string Order_Code,
    byte Payment_Method,
    string? Payment_Status     // "Unpaid"/"Pending"/"Failed"/"Paid"...
);
