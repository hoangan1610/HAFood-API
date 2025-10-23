using System.ComponentModel.DataAnnotations;

namespace HAShop.Api.DTOs;

public record OrderItemDto(
  long Id,
  long Order_Id,
  long Variant_Id,
  long Product_Id,
  string Sku,
  string? Name_Variant,
  decimal Price_Variant,
  string? Image_Variant,
  int Quantity,
  decimal Line_Subtotal,
  DateTime Created_At,
  DateTime Updated_At,

  // ĐÚNG THỨ TỰ THEO SELECT: brand_name, product_name, image_product
  string? Brand_Name,
  string? Product_Name,
  string? Image_Product
);


public record OrderHeaderDto(
  long Id,
  long User_Info_Id,
  long? Address_Id,
  string Order_Code,
  string Ship_Name,
  string Ship_Full_Address,
  string Ship_Phone,
  byte Status,
  decimal Sub_Total,
  decimal Discount_Total,
  decimal Shipping_Total,
  decimal Vat_Total,
  decimal Pay_Total,
  string Ip,
  long? Device_Id,          // đổi: nullable
  byte? Payment_Method,     // đổi: nullable
  DateTime? Placed_At,      // an toàn: nullable
  DateTime? Confirmed_At,
  DateTime? Shipped_At,
  DateTime? Delivered_At,
  DateTime? Canceled_At,
  string? Note,
  DateTime Created_At,
  DateTime Updated_At,
  long? Cart_Id,            // đổi: nullable

  // === MỚI: các cột thanh toán ===
  string? Payment_Status,
  string? Payment_Provider,
  string? Payment_Ref,
  DateTime? Paid_At
);


public record OrderDetailDto(OrderHeaderDto Header, IReadOnlyList<OrderItemDto> Items);



public record PlaceOrderResponse(long Order_Id, string Order_Code);

public record OrdersPageDto(IReadOnlyList<OrderHeaderDto> Items, int TotalCount, int Page, int PageSize);

public record PaymentCreateRequest(
  long Order_Id, string Provider, byte Method, byte Status,
  decimal Amount, string Currency, string Transaction_Id, string? Merchant_Ref,
  string? Error_Code, string? Error_Message, DateTime? Paid_At
);
public record PaymentCreateResponse(long Payment_Id);

public record class PlaceOrderRequest
{
    public long? Cart_Id { get; init; }

    [Required] public string Ship_Name { get; init; } = "";
    [Required] public string Ship_Full_Address { get; init; } = "";
    [Required] public string Ship_Phone { get; init; } = "";

    [Required] public byte Payment_Method { get; init; }

    public string Ip { get; init; } = "";
    public string? Note { get; init; }
    public long? Address_Id { get; init; }
    public long? Device_Id { get; init; }

    public string? Promo_Code { get; init; }
    public long[]? Selected_Line_Ids { get; init; }
    public List<CheckoutItemDto>? Items { get; init; }
}
public record class CheckoutItemDto
{
    public long Variant_Id { get; init; }
    public int Quantity { get; init; }
}
