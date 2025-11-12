namespace HAShop.Api.DTOs;

public sealed class AddressDto
{
    public long Id { get; set; }
    public long UserInfoId { get; set; }
    public byte? Type { get; set; }
    public string? Label { get; set; }
    public bool IsDefault { get; set; }
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public byte Status { get; set; }
    public string FullAddress { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public record AddressCreateRequest(
    byte? Type,
    string? Label,
    bool IsDefault,
    string? FullName,
    string? Phone,
    string FullAddress
);

public record AddressUpdateRequest(
    byte? Type,
    string? Label,
    bool? IsDefault,
    string? FullName,
    string? Phone,
    string? FullAddress,
    byte? Status           // 1=active, 0=deleted (thường không dùng ở Update, nhưng để mở rộng)
);

public record ApiOkResponse<T>(bool Success, T Data, string? Message = null);
public record ApiFailResponse(bool Success, string Code, string? Message = null);
