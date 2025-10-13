namespace HAShop.Api.DTOs;

public record NotificationDto(
  long Id, long User_Info_Id, long? Device_Id, long? Order_Id,
  byte Type, byte Channel, string Title, string Body, string? Data,
  byte Status, DateTime? Delivered_At, DateTime? Read_At,
  DateTime Created_At, DateTime Updated_At
);

public record NotificationsPageDto(IReadOnlyList<NotificationDto> Items, int TotalCount, int Page, int PageSize);
public record MarkReadRequest(long Id);
public record MarkDeliveredRequest(long Id);
public record UnreadCountDto(int Count);
