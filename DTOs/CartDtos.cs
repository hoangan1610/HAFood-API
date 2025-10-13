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

public record CartItemDto(
    long Id,
    long Cart_Id,
    long Variant_Id,
    string? Name_Variant,
    decimal Price_Variant,
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

public record CartAddRequest(long Variant_Id, int Quantity, string? Name_Variant = null, decimal? Price_Variant = null, string? Image_Variant = null);
public record CartUpdateQtyRequest(int Quantity);
