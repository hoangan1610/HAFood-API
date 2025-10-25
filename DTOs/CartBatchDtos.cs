// HAShop.Api/DTOs/CartBatchDtos.cs
namespace HAShop.Api.DTOs;

public record CartQtyChangeDto(long Line_Id, int Quantity);

public record CartBatchRequest(
    string? Device_Uuid,       // optional: nếu FE chưa có cart_id
    long? Device_Id,           // optional
    long? Cart_Id,             // optional: nếu FE đã có cart_id thì gửi thẳng
    IReadOnlyList<CartQtyChangeDto> Changes
);

public record CartLineState(long Line_Id, int Quantity, decimal Price_Variant);
public record CartTotals(decimal Subtotal, decimal Vat, decimal Shipping, decimal Grand);
public record CartCompactResponse(IReadOnlyList<CartLineState> Lines, CartTotals Totals);

public record CartLineCompactDto(long Line_Id, long Variant_Id, int Quantity, decimal Price_Variant);
public record CartTotalsDto(decimal Subtotal, decimal Vat, decimal Shipping, decimal Grand);
public record CartCompactDto(IReadOnlyList<CartLineCompactDto> Lines, CartTotalsDto Totals);