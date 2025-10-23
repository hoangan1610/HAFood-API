using HAShop.Api.DTOs;
using Microsoft.AspNetCore.Http;
using System.Threading;

namespace HAShop.Api.Services
{
    public interface IDeviceService
    {
        Task<long?> ResolveDevicePkAsync(Guid? deviceUuid, long? deviceId, long? userInfoId, HttpContext http, CancellationToken ct);

        // ✅ Thêm method upsert để DeviceController gọi
        Task<DeviceUpsertResponse> UpsertDeviceAsync(DeviceUpsertRequest req, string? ipFromContext, CancellationToken ct);
    }
}
