using System.Threading;
using System.Threading.Tasks;
using HAShop.Api.DTOs;

namespace HAShop.Api.Services
{
    public interface ILoyaltyService
    {
        /// <summary>
        /// Cộng điểm khi đơn hàng hoàn tất (status = 3).
        /// Gọi SP: usp_loyalty_add_points_for_order
        /// và tự bắn notify LOYALTY_POINTS_EARNED + LOYALTY_TIER_CHANGED (nếu cần).
        /// </summary>
        Task<LoyaltyOrderPointsResult> AddPointsFromOrderAsync(
            long userInfoId,
            long orderId,
            int points,
            string? reason,
            CancellationToken ct = default);
    }
}
