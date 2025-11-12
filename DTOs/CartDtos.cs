namespace HAShop.Api.DTOs;

public record CartHeaderDto(
    long Cart_Id,
    long? User_Info_Id,
    long? Device_Id,
    byte Status,
    DateTime Created_At,
    DateTime Updated_At,
    int Item_Count,
    decimal Subtotal
);

// NOTE: thêm Price_Effective để map từ usp_cart_view (alias: price_effective)
public record CartItemDto(
    long Id,
    long Cart_Id,
    long Variant_Id,
    string? Name_Variant,
    decimal Price_Variant,
    decimal Price_Base, 
    decimal Price_Effective,        // <= NEW: effective price từ SP
    string? Image_Variant,
    int Quantity,
    byte Status,
    DateTime Added_At,
    DateTime Updated_At,
    string Sku,
    string Variant_Name,
    decimal Variant_Retail_Price,
    int Variant_Stock,
    long Product_Id,
    string Product_Name,
    string Brand_Name,
    long Category_Id,
    string Image_Product
);

public record CartViewDto(
    CartHeaderDto Header,
    IReadOnlyList<CartItemDto> Items
);

public record CartAddRequest(
    long Variant_Id,
    int Quantity,
    string? Name_Variant = null,
    decimal? Price_Variant = null,
    string? Image_Variant = null
);

public record CartUpdateQtyRequest(int Quantity);

// ===== Batch Compact DTOs =====
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

// Fallback (giữ cho tương thích với chỗ code cũ, không khuyến nghị xài cho FS)
public record CartLineCompactDto(long Line_Id, long Variant_Id, int Quantity, decimal Price_Variant);
public record CartTotalsDto(decimal Subtotal, decimal Vat, decimal Shipping, decimal Grand);
public record CartCompactDto(IReadOnlyList<CartLineCompactDto> Lines, CartTotalsDto Totals);
