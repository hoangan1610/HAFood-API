namespace HAShop.Api.DTOs;

public sealed class NotificationDto
{
    public long Id { get; set; }
    public byte Type { get; set; }          // 1=Order, 2=Loyalty,...
    public byte Channel { get; set; }       // 1=InApp, 2=Email,...
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Data { get; set; }       // JSON payload
    public byte Status { get; set; }        // 0=Mới,1=Đã đọc,...
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsRead { get; set; }
}

public sealed record NotificationLatestResultDto(
    IReadOnlyList<NotificationDto> Items,
    int TotalUnread
);

public sealed record NotificationPagedResultDto(
    int Page,
    int PageSize,
    int TotalRows,
    int TotalUnread,
    IReadOnlyList<NotificationDto> Items
);

// Dùng cho các service nội bộ muốn tạo thông báo
public sealed record NotificationAddCommand(
    long UserInfoId,
    long? DeviceId,
    long? OrderId,
    byte Type,
    byte Channel,
    string Title,
    string Body,
    string? DataJson
);
