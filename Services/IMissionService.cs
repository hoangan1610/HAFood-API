using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HAShop.Api.Services
{
    public interface IMissionService
    {
        Task CheckOrderMissionsAsync(
            long orderId,
            long userId,
            decimal payTotal,
            DateTime? deliveredAt,
            CancellationToken ct);

        Task CheckReviewMissionsAsync(
            long reviewId,
            long userId,
            long productId,
            long? orderId,
            byte rating,
            CancellationToken ct);

        Task<IReadOnlyList<UserMissionDto>> GetUserMissionsAsync(
            long userId,
            CancellationToken ct);
    }

    public sealed class UserMissionDto
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }

        public byte MissionType { get; set; }   // 0 = Order, 1 = Review...
        public byte RewardType { get; set; }    // 0 = Spin, 1 = Loyalty

        public int? RewardValue { get; set; }
        public int? MaxPerUser { get; set; }

        public int TimesCompleted { get; set; }
        public DateTime? LastCompletedAt { get; set; }

        // mới thêm
        public int? DisplayOrder { get; set; }
        public bool IsFeatured { get; set; }

        // Tính trong service: "available", "completed", "maxed"
        public string Status { get; set; } = "";
    }
}
