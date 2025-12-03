using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HAShop.Api.Data;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Services
{
    public sealed class MissionService : IMissionService
    {
        private readonly ISqlConnectionFactory _db;
        private readonly ILogger<MissionService> _logger;

        public MissionService(
            ISqlConnectionFactory db,
            ILogger<MissionService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<IReadOnlyList<UserMissionDto>> GetUserMissionsAsync(
            long userId,
            CancellationToken ct)
        {
            using var con = _db.Create();

            const string sql = @"
SELECT 
    m.id              AS Id,
    m.code            AS Code,
    m.name            AS Name,
    m.description     AS Description,
    m.mission_type    AS MissionType,
    m.reward_type     AS RewardType,
    m.reward_value    AS RewardValue,
    m.max_per_user    AS MaxPerUser,
    m.display_order   AS DisplayOrder,
    m.is_featured     AS IsFeatured,
    ISNULL(l.times_completed, 0)  AS TimesCompleted,
    l.last_completed_at           AS LastCompletedAt
FROM dbo.tbl_mission m
LEFT JOIN dbo.tbl_mission_user_log l
       ON l.mission_id   = m.id
      AND l.user_info_id = @user_id
WHERE m.is_active = 1
ORDER BY m.id ASC;";

            var list = (await con.QueryAsync<UserMissionDto>(
                new CommandDefinition(
                    sql,
                    new { user_id = userId },
                    cancellationToken: ct
                ))).ToList();

            // ===== Map Status
            foreach (var m in list)
            {
                var max = m.MaxPerUser;
                var done = m.TimesCompleted;

                if (max.HasValue && max.Value > 0)
                {
                    if (done >= max.Value)
                    {
                        m.Status = "maxed";
                    }
                    else if (done > 0)
                    {
                        m.Status = "completed";
                    }
                    else
                    {
                        m.Status = "available";
                    }
                }
                else
                {
                    // không giới hạn → đã từng hoàn thành = completed
                    m.Status = done > 0 ? "completed" : "available";
                }
            }

            // ===== Helper nhỏ cho sort
            static int StatusRank(string? status)
            {
                switch ((status ?? "").ToLowerInvariant())
                {
                    case "available": return 0;
                    case "completed": return 1;
                    case "maxed": return 2;
                    default: return 9;
                }
            }

            // nếu DisplayOrder null → đẩy về cuối nhóm
            static int DisplayOrderVal(UserMissionDto m)
            {
                return m.DisplayOrder ?? int.MaxValue;
            }

            static int RewardVal(UserMissionDto m)
            {
                return m.RewardValue ?? 0;
            }

            // Sort:
            // 1. Mission featured (is_featured = 1) lên trước
            // 2. Trong từng nhóm, sort theo status: available → completed → maxed
            // 3. Sau đó theo display_order tăng dần
            // 4. Sau đó theo reward_value giảm dần
            // 5. Cuối cùng theo Id để ổn định
            list = list
                .OrderByDescending(m => m.IsFeatured)           // 1. featured trước
                .ThenBy(m => StatusRank(m.Status))             // 2. trạng thái
                .ThenBy(m => DisplayOrderVal(m))               // 3. thứ tự hiển thị
                .ThenByDescending(m => RewardVal(m))           // 4. giá trị thưởng
                .ThenBy(m => m.Id)                             // 5. ổn định
                .ToList();

            return list;
        }

        public async Task CheckOrderMissionsAsync(
            long orderId,
            long userId,
            decimal payTotal,
            DateTime? deliveredAt,
            CancellationToken ct)
        {
            // Đơn chưa giao → chưa tính mission
            if (deliveredAt is null)
                return;

            using var con = _db.Create();

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_mission_check_order_delivered",
                    new
                    {
                        order_id = orderId,
                        user_info_id = userId,
                        pay_total = payTotal,
                        delivered_at = deliveredAt
                    },
                    commandType: System.Data.CommandType.StoredProcedure,
                    cancellationToken: ct,
                    commandTimeout: 30
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "usp_mission_check_order_delivered failed. OrderId={OrderId}, UserId={UserId}",
                    orderId, userId);
            }
        }

        // ✅ Mission review: REVIEW_AFTER_DELIVERY
        public async Task CheckReviewMissionsAsync(
            long reviewId,
            long userId,
            long productId,
            long? orderId,
            byte rating,
            CancellationToken ct)
        {
            using var con = _db.Create();

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_mission_check_review_created",
                    new
                    {
                        review_id = reviewId,
                        user_info_id = userId,
                        product_id = productId,
                        order_id = orderId,
                        rating = rating
                    },
                    commandType: System.Data.CommandType.StoredProcedure,
                    cancellationToken: ct,
                    commandTimeout: 30
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "usp_mission_check_review_created failed. ReviewId={ReviewId}, UserId={UserId}",
                    reviewId, userId);
            }
        }
    }
}
