namespace HAShop.Api.DTOs;

public record OrderItemDto(
  long Id, long Order_Id, long Variant_Id, long Product_Id, string Sku,
  string? Name_Variant, decimal Price_Variant, string? Image_Variant,
  int Quantity, decimal Line_Subtotal, DateTime Created_At, DateTime Updated_At,
  // enrich
  string? Product_Name, string? Brand_Name, string? Image_Product
);

public record OrderHeaderDto(
  long Id, long User_Info_Id, long? Address_Id, string Order_Code,
  string Ship_Name, string Ship_Full_Address, string Ship_Phone,
  byte Status, decimal Sub_Total, decimal Discount_Total, decimal Shipping_Total,
  decimal Vat_Total, decimal Pay_Total, string Ip, long? Device_Id,
  byte? Payment_Method, DateTime? Placed_At, DateTime? Confirmed_At,
  DateTime? Shipped_At, DateTime? Delivered_At, DateTime? Canceled_At,
  string? Note, DateTime Created_At, DateTime Updated_At, long? Cart_Id
);

public record OrderDetailDto(OrderHeaderDto Header, IReadOnlyList<OrderItemDto> Items);

public record PlaceOrderRequest(
  long? Cart_Id,
  string Ship_Name,
  string Ship_Full_Address,
  string Ship_Phone,
  byte Payment_Method,
  string Ip,
  string? Note,
  long? Address_Id,
  long? Device_Id
);

public record PlaceOrderResponse(long Order_Id, string Order_Code);

public record OrdersPageDto(IReadOnlyList<OrderHeaderDto> Items, int TotalCount, int Page, int PageSize);

public record PaymentCreateRequest(
  long Order_Id, string Provider, byte Method, byte Status,
  decimal Amount, string Currency, string Transaction_Id, string? Merchant_Ref,
  string? Error_Code, string? Error_Message, DateTime? Paid_At
);
public record PaymentCreateResponse(long Payment_Id);
