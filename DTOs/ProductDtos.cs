namespace HAShop.Api.DTOs;

public record ProductListItemDto(
    long Product_Id,
    string Product_Name,
    string Brand_Name,
    long Category_Id,
    string Category_Name,
    byte Status,
    bool Is_Deleted,
    DateTime Created_At,
    DateTime Updated_At,
    int Total_Stock,
    decimal Min_Retail_Price,   // đổi từ decimal? -> decimal
    decimal Max_Retail_Price,   // đổi từ decimal? -> decimal
    int Has_Variants            // đổi từ bool -> int
)
{
    public bool Has_Variants_Bool => Has_Variants != 0;
}

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public record VariantDto(
    long Id, string Sku, string? Name, string? Image, string? Meta_Data, int? Weight,
    decimal Cost_Price, decimal Finished_Cost, decimal Wholesale_Price, decimal Retail_Price,
    int Stock, byte Status, DateTime Created_At, DateTime Updated_At
);

public record ProductDetailDto(
    long Id, long Category_Id, string Brand_Name, string Name, string? Tag, string? Product_Keyword,
    string? Detail, string Image_Product, string? Expiry, byte Status, DateTime Created_At, DateTime Updated_At,
    IReadOnlyList<VariantDto> Variants
);

public record ProductBySkuDto(
    long Product_Id, long Category_Id, string Brand_Name, string Product_Name,
    string? Tag, string? Product_Keyword, string Image_Product, byte Status,
    DateTime Created_At, DateTime Updated_At,
    long Variant_Id, string Sku, string Variant_Name, string? Image, string? Meta_Data, int? Weight,
    decimal Cost_Price, decimal Finished_Cost, decimal Wholesale_Price, decimal Retail_Price,
    int Stock, byte Variant_Status
);

public record VariantWithProductDto(
    long Variant_Id, long Product_Id, string Product_Name, string Brand_Name,
    long Category_Id, string Category_Name,
    string Sku, string Variant_Name, string? Image, string? Meta_Data, int? Weight,
    decimal Cost_Price, decimal Finished_Cost, decimal Wholesale_Price, decimal Retail_Price,
    int Stock, byte Status, DateTime Created_At, DateTime Updated_At
);

public record CategoryTreeDto(long Id, long? Parent_Id, string Name, string Path, int Level);
public record BrandItemDto(string Brand_Name, int Product_Count);
public record SuggestResponse(IReadOnlyList<string> Items);

public record AdjustStockRequest(int Delta);
public record AdjustStockResponse(bool Success);
