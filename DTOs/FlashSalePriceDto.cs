namespace HAShop.Api.DTOs;

public record FlashSalePriceDto(
    DateTime Server_Now,
    long Variant_Id,
    decimal Base_Price,
    decimal? Campaign_Price,
    decimal Effective_Price,
    long? Vpo_Id,
    DateTime? End_At,
    int Sold_Count,
    int? Qty_Cap_Total
);

public record FlashSaleActiveItemDto(
    DateTime Server_Now,
    long Vpo_Id,
    long Variant_Id,
    DateTime Start_At,
    DateTime End_At,
    int? Qty_Cap_Total,
    int Sold_Count,
    decimal Retail_Price,
    decimal? Sale_Price,
    decimal? Percent_Off,
    decimal Effective_Price
);

public record FlashSaleReserveRequest(long Vpo_Id, int Qty);
public record FlashSaleReserveResponse(long Vpo_Id, int Sold_Count, int? Qty_Cap_Total, int? Remaining);

public record FlashSaleReleaseRequest(long Vpo_Id, int Qty);
public record FlashSaleReleaseResponse(long Vpo_Id, int Sold_Count, int? Qty_Cap_Total, int? Remaining);
