namespace HAShop.Api.DTOs;

public record DeviceUpsertRequest(
    long? UserInfoId,
    Guid DeviceUuid,
    string? DeviceModel,
    string? Ip // có thể null, controller sẽ tự suy IP
);

public record DeviceUpsertResponse(
    bool Success,
    long DevicePk,
    string? Code = null,
    string? Message = null
);
