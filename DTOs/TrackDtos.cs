namespace HAShop.Api.DTOs;

public record TrackEventRequest(
  long? Device_Id,
  long? Product_Id,
  long? Variant_Id,
  long? Category_Id,
  string? Ip,
  string? User_Agent,
  string? Device_Type,
  string? Data // JSON string
);
public record TrackEventResponse(long Id);

public record TopProductViewDto(
  long Product_Id, string Product_Name, string Brand_Name,
  long Category_Id, string Category_Name, int Views
);

public record TopCategoryViewDto(
  long Category_Id, string Category_Name, int Views
);

public record RecentEventDto(
  long Id, long Device_Id, long? Product_Id, long? Variant_Id, long? Category_Id,
  string? Ip, string? User_Agent, string? Device_Type, string? Data, DateTime Created_At,
  // enrich
  string? Product_Name, string? Brand_Name, string? Image_Product,
  string? Category_Name
);
