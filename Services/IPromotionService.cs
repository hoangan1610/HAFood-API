// File: Api/Services/IPromotionService.cs
using System.Threading;
using System.Threading.Tasks;
using HAShop.Api.DTOs;

namespace HAShop.Api.Services
{
    public interface IPromotionService
    {
        Task<PromoListResponse> ListActiveAsync(long? userId, string? deviceUuid, PromoListRequest req, CancellationToken ct = default);
        Task<PromoQuoteResponse> QuoteAsync(long? userId, string? deviceUuid, PromoQuoteRequest req, CancellationToken ct = default);
        Task<PromoReserveResponse> ReserveAsync(long userIdOr0, string? deviceUuid, PromoReserveRequest req, CancellationToken ct = default);
        Task<PromoReleaseResponse> ReleaseAsync(PromoReleaseRequest req, CancellationToken ct = default);
    }
}
