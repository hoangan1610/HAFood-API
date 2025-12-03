using System.Data;
using System.Text.Json;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using HAShop.Api.Utils;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Services
{
    public interface IGamificationService
    {
        Task<LoyaltySummaryDto> GetLoyaltySummaryAsync(long userInfoId, CancellationToken ct);
        Task<GamCheckinResponseDto> CheckinAsync(long userInfoId, byte channel, long? deviceId, string? ip, CancellationToken ct);
        Task<IReadOnlyList<GamSpinTurnDto>> GetSpinsAsync(long userInfoId, CancellationToken ct);
        Task<GamSpinRollResponseDto> SpinAsync(long userInfoId, long spinTurnId, byte? channel, long? deviceId, string? ip, CancellationToken ct);

        Task<GamSpinConfigDto?> GetActiveSpinConfigAsync(byte? channel, CancellationToken ct);
        Task<GamStatusDto> GetStatusAsync(long userInfoId, byte? channel, CancellationToken ct);

        Task<LoyaltyRedeemResponseDto> RedeemRewardAsync(
       long userInfoId,
       long rewardId,
       int quantity,
       byte channel,
       long? deviceId,
       string? ip,
       CancellationToken ct);

        Task<IReadOnlyList<LoyaltyRewardDto>> GetLoyaltyRewardsAsync(
    byte? channel,
    CancellationToken ct);

    }

    public sealed class GamificationService : IGamificationService
    {
        private readonly ISqlConnectionFactory _db;
        private readonly INotificationService _notifications;
        private readonly ILogger<GamificationService> _logger;

        public GamificationService(
            ISqlConnectionFactory db,
            INotificationService notifications,
            ILogger<GamificationService> logger)
        {
            _db = db;
            _notifications = notifications;
            _logger = logger;
        }

        // ========== LOYALTY SUMMARY ==========
        public async Task<LoyaltySummaryDto> GetLoyaltySummaryAsync(long userInfoId, CancellationToken ct)
        {
            using var con = _db.Create();
            var p = new DynamicParameters();
            p.Add("@user_id", userInfoId);

            var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                """
                SELECT user_info_id,
                       total_points,
                       lifetime_points,
                       tier,
                       streak_days,
                       max_streak_days,
                       last_checkin_date
                FROM dbo.tbl_loyalty_account
                WHERE user_info_id = @user_id
                """,
                p,
                commandType: CommandType.Text,
                cancellationToken: ct));

            if (row is null)
            {
                return new LoyaltySummaryDto(
                    User_Info_Id: userInfoId,
                    Total_Points: 0,
                    Lifetime_Points: 0,
                    Tier: 0,
                    Streak_Days: 0,
                    Max_Streak_Days: 0,
                    Last_Checkin_Date: null
                );
            }

            return new LoyaltySummaryDto(
                User_Info_Id: (long)row.user_info_id,
                Total_Points: (int)row.total_points,
                Lifetime_Points: (int)row.lifetime_points,
                Tier: (byte)row.tier,
                Streak_Days: (int)row.streak_days,
                Max_Streak_Days: (int)row.max_streak_days,
                Last_Checkin_Date: (DateTime?)row.last_checkin_date
            );
        }

        // ========== CHECKIN ==========
        public async Task<GamCheckinResponseDto> CheckinAsync(
            long userInfoId,
            byte channel,
            long? deviceId,
            string? ip,
            CancellationToken ct)
        {
            using var con = _db.Create();
            var p = new DynamicParameters();
            p.Add("@user_info_id", userInfoId);
            p.Add("@channel", channel);
            p.Add("@device_id", deviceId);
            p.Add("@ip", ip);

            var row = await con.QueryFirstAsync(new CommandDefinition(
                "dbo.usp_gam_checkin",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            bool success = (bool)row.success;

            var dto = new GamCheckinResponseDto(
                Success: success,
                Error_Code: row.error_code as string,
                Error_Message: row.error_message as string,
                Streak_Days: row.streak_days is null ? null : (int?)row.streak_days,
                Total_Points: row.total_points is null ? null : (int?)row.total_points,
                Spins_Created: row.spins_created is null ? null : (int?)row.spins_created
            );

            // 🔔 Gửi notify nếu điểm danh thành công
            if (success)
            {
                try
                {
                    var dataObj = new
                    {
                        type = "checkin",
                        streak_days = dto.Streak_Days,
                        total_points = dto.Total_Points,
                        spins_created = dto.Spins_Created
                    };
                    var dataJson = JsonSerializer.Serialize(dataObj);

                    var streak = dto.Streak_Days ?? 0;
                    var totalPoints = dto.Total_Points ?? 0;
                    var spins = dto.Spins_Created ?? 0;

                    var title = "Điểm danh thành công";
                    var body = $"Bạn đã điểm danh ngày thứ {streak}. Tổng điểm hiện tại: {totalPoints} điểm.";
                    if (spins > 0)
                    {
                        body += $" Nhận thêm {spins} lượt quay may mắn.";
                    }

                    await _notifications.CreateInAppAsync(
                        userInfoId,
                        NotificationTypes.CHECKIN_SUCCESS,
                        title,
                        body,
                        dataJson,
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Send notification CHECKIN_SUCCESS failed. userInfoId={UserId}",
                        userInfoId);
                }
            }
            else if (string.Equals(dto.Error_Code, "CHECKIN_ALREADY_DONE", StringComparison.OrdinalIgnoreCase))
            {
                // Tuỳ anh: có thể không cần notify, FE đã show toast từ API.
                // Nếu muốn vẫn notify:
                /*
                try
                {
                    var dataJson = JsonSerializer.Serialize(new { type = "checkin_already_done" });

                    await _notifications.CreateInAppAsync(
                        userInfoId,
                        NotificationTypes.CHECKIN_ALREADY_DONE,
                        "Bạn đã điểm danh hôm nay",
                        "Mỗi ngày chỉ điểm danh 1 lần, hãy quay lại vào ngày mai nhé.",
                        dataJson,
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Send notification CHECKIN_ALREADY_DONE failed. userInfoId={UserId}",
                        userInfoId);
                }
                */
            }

            return dto;
        }

        // ========== LẤY DANH SÁCH LƯỢT QUAY ==========
        public async Task<IReadOnlyList<GamSpinTurnDto>> GetSpinsAsync(long userInfoId, CancellationToken ct)
        {
            using var con = _db.Create();
            var p = new DynamicParameters();
            p.Add("@user_id", userInfoId);

            var rows = await con.QueryAsync(new CommandDefinition(
                """
                SELECT id,
                       spin_config_id,
                       status,
                       earned_at,
                       used_at,
                       expire_at
                FROM dbo.tbl_gam_spin_turn
                WHERE user_info_id = @user_id
                ORDER BY earned_at DESC
                """,
                p,
                commandType: CommandType.Text,
                cancellationToken: ct));

            var list = new List<GamSpinTurnDto>();
            foreach (var r in rows)
            {
                list.Add(new GamSpinTurnDto(
                    Id: (long)r.id,
                    Spin_Config_Id: (long)r.spin_config_id,
                    Status: (byte)r.status,
                    Earned_At: (DateTime)r.earned_at,
                    Used_At: (DateTime?)r.used_at,
                    Expire_At: (DateTime?)r.expire_at
                ));
            }

            return list;
        }

        // ========== QUAY VÒNG QUAY ==========
        public async Task<GamSpinRollResponseDto> SpinAsync(
            long userInfoId,
            long spinTurnId,
            byte? channel,
            long? deviceId,
            string? ip,
            CancellationToken ct)
        {
            using var con = _db.Create();
            var p = new DynamicParameters();
            p.Add("@spin_turn_id", spinTurnId);
            p.Add("@user_info_id", userInfoId);
            p.Add("@channel", channel);
            p.Add("@device_id", deviceId);
            p.Add("@ip", ip);

            var row = await con.QueryFirstAsync(new CommandDefinition(
                "dbo.usp_gam_spin_roll",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            bool success = (bool)row.success;

            var dto = new GamSpinRollResponseDto(
                Success: success,
                Error_Code: row.error_code as string,
                Error_Message: row.error_message as string,
                Spin_Result_Id: row.spin_result_id is null ? null : (long?)row.spin_result_id,
                Config_Item_Id: row.config_item_id is null ? null : (long?)row.config_item_id,
                Reward_Type: row.reward_type is null ? null : (byte?)row.reward_type,
                Reward_Value: row.reward_value is null ? null : (decimal?)row.reward_value,
                Promotion_Id: row.promotion_id is null ? null : (long?)row.promotion_id,
                Promotion_Code: row.promotion_code as string,
                Min_Order_Amount: row.min_order_amount is null ? null : (decimal?)row.min_order_amount,
                Total_Points: row.total_points is null ? null : (int?)row.total_points,
                Points_Added: row.points_added is null ? null : (int?)row.points_added,
                Extra_Spins_Created: row.extra_spins_created is null ? null : (int?)row.extra_spins_created
            );

            if (!success)
                return dto;

            // 🔔 1) Notify Loyalty nếu có cộng điểm
            if (dto.Points_Added.HasValue && dto.Points_Added.Value > 0)
            {
                try
                {
                    var dataObj = new
                    {
                        type = "loyalty_points_earned",
                        source = "spin",
                        points = dto.Points_Added.Value,
                        total_points = dto.Total_Points,
                        spin_result_id = dto.Spin_Result_Id
                    };
                    var dataJson = JsonSerializer.Serialize(dataObj);

                    var title = "Bạn vừa được cộng điểm thưởng";
                    var body =
                        $"Nhờ vòng quay may mắn, bạn nhận được +{dto.Points_Added.Value} điểm. " +
                        $"Tổng điểm hiện tại: {dto.Total_Points ?? 0}.";

                    await _notifications.CreateInAppAsync(
                        userInfoId,
                        NotificationTypes.LOYALTY_POINTS_EARNED,
                        title,
                        body,
                        dataJson,
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Send notification LOYALTY_POINTS_EARNED from spin failed. userInfoId={UserId}, spinTurnId={SpinTurnId}",
                        userInfoId, spinTurnId);
                }
            }

            // 🔔 2) Notify SPIN_BIG_PRIZE nếu trúng thưởng lớn
            try
            {
                bool isBigPrize = false;

                // Rule tạm: nếu là voucher / tiền >= 50k, hoặc được thêm nhiều lượt quay
                if (dto.Reward_Type.HasValue)
                {
                    // 1 = promotion, 2 = points (tuỳ mapping DB của anh)
                    if (dto.Reward_Type.Value == 1 && dto.Reward_Value.HasValue && dto.Reward_Value.Value >= 50000)
                    {
                        isBigPrize = true;
                    }
                    else if (dto.Reward_Type.Value == 2 && dto.Reward_Value.HasValue && dto.Reward_Value.Value >= 1000)
                    {
                        isBigPrize = true;
                    }
                }

                if (!isBigPrize && dto.Extra_Spins_Created.HasValue && dto.Extra_Spins_Created.Value >= 3)
                {
                    isBigPrize = true;
                }

                if (isBigPrize)
                {
                    var dataObj = new
                    {
                        type = "spin_big_prize",
                        reward_type = dto.Reward_Type,
                        reward_value = dto.Reward_Value,
                        promotion_id = dto.Promotion_Id,
                        promotion_code = dto.Promotion_Code,
                        min_order_amount = dto.Min_Order_Amount
                    };
                    var dataJson = JsonSerializer.Serialize(dataObj);

                    var title = "Chúc mừng! Bạn trúng thưởng lớn 🎉";

                    string rewardText;
                    if (dto.Promotion_Code is not null)
                    {
                        rewardText = $"Bạn nhận được mã giảm giá {dto.Promotion_Code}.";
                    }
                    else if (dto.Reward_Type == 2 && dto.Reward_Value.HasValue)
                    {
                        rewardText = $"Bạn nhận được {dto.Reward_Value.Value:N0} điểm thưởng.";
                    }
                    else
                    {
                        rewardText = "Bạn nhận được phần thưởng giá trị từ vòng quay.";
                    }

                    var body = rewardText + " Hãy vào mục Ưu đãi / Vòng quay để xem chi tiết.";

                    await _notifications.CreateInAppAsync(
                        userInfoId,
                        NotificationTypes.SPIN_BIG_PRIZE,
                        title,
                        body,
                        dataJson,
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Send notification SPIN_BIG_PRIZE failed. userInfoId={UserId}, spinTurnId={SpinTurnId}",
                    userInfoId, spinTurnId);
            }

            return dto;
        }

        // ========== LẤY CONFIG VÒNG QUAY ĐANG HOẠT ĐỘNG ==========
        public async Task<GamSpinConfigDto?> GetActiveSpinConfigAsync(byte? channel, CancellationToken ct)
        {
            using var con = _db.Create();

            var p = new DynamicParameters();
            p.Add("@channel", channel);

            var cfg = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                """
                SELECT TOP (1)
                       id,
                       name,
                       description,
                       status,
                       channel,
                       start_at,
                       end_at,
                       max_spins_per_day
                FROM dbo.tbl_gam_spin_config
                WHERE status = 1
                  AND (@channel IS NULL OR channel IS NULL OR channel = @channel)
                  AND (start_at IS NULL OR start_at <= SYSDATETIME())
                  AND (end_at   IS NULL OR end_at   >= SYSDATETIME())
                ORDER BY id DESC
                """,
                p,
                commandType: CommandType.Text,
                cancellationToken: ct));

            if (cfg is null) return null;

            long cfgId = (long)cfg.id;

            var itemsRaw = await con.QueryAsync(new CommandDefinition(
                """
                SELECT id,
                       display_order,
                       label,
                       reward_type,
                       reward_value,
                       promotion_id,
                       min_order_amount,
                       weight,
                       icon_key
                FROM dbo.tbl_gam_spin_config_item
                WHERE spin_config_id = @cfgId
                  AND is_active = 1
                ORDER BY display_order, id
                """,
                new { cfgId },
                commandType: CommandType.Text,
                cancellationToken: ct));

            var items = new List<GamSpinConfigItemDto>();
            foreach (var r in itemsRaw)
            {
                items.Add(new GamSpinConfigItemDto(
                    Id: (long)r.id,
                    Display_Order: (int)r.display_order,
                    Label: (string)r.label,
                    Reward_Type: (byte)r.reward_type,
                    Reward_Value: r.reward_value is null ? null : (decimal?)r.reward_value,
                    Promotion_Id: r.promotion_id is null ? null : (long?)r.promotion_id,
                    Min_Order_Amount: r.min_order_amount is null ? null : (decimal?)r.min_order_amount,
                    Weight: (int)r.weight,
                    Icon_Key: r.icon_key as string
                ));
            }

            return new GamSpinConfigDto(
                Id: cfgId,
                Name: (string)cfg.name,
                Description: cfg.description as string,
                Status: (byte)cfg.status,
                Channel: cfg.channel is null ? null : (byte?)cfg.channel,
                Start_At: (DateTime?)cfg.start_at,
                End_At: (DateTime?)cfg.end_at,
                Max_Spins_Per_Day: cfg.max_spins_per_day is null ? null : (int?)cfg.max_spins_per_day,
                Items: items
            );
        }

        // ========== TRẠNG THÁI GAMIFICATION ==========
        public async Task<GamStatusDto> GetStatusAsync(long userInfoId, byte? channel, CancellationToken ct)
        {
            using var con = _db.Create();

            // 1) Loyalty / checkin
            var p1 = new DynamicParameters();
            p1.Add("@user_id", userInfoId);

            var loyaltyRow = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                """
                SELECT
                    total_points,
                    streak_days,
                    CASE
                        WHEN last_checkin_date IS NOT NULL
                             AND DATEDIFF(DAY, last_checkin_date, SYSDATETIME()) = 0
                        THEN 1 ELSE 0
                    END AS has_checked_in_today
                FROM dbo.tbl_loyalty_account
                WHERE user_info_id = @user_id
                """,
                p1,
                commandType: CommandType.Text,
                cancellationToken: ct));

            bool hasCheckedInToday = false;
            int? totalPoints = null;
            int? streakDays = null;

            if (loyaltyRow is not null)
            {
                hasCheckedInToday = (int)loyaltyRow.has_checked_in_today == 1;
                totalPoints = (int?)loyaltyRow.total_points;
                streakDays = (int?)loyaltyRow.streak_days;
            }

            // 2) Spin còn lại
            var p2 = new DynamicParameters();
            p2.Add("@user_id", userInfoId);
            p2.Add("@channel", channel);

            int remainingSpins = await con.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                SELECT COUNT(*) 
                FROM dbo.tbl_gam_spin_turn t
                WHERE t.user_info_id = @user_id
                  AND t.status = 0
                  AND (t.expire_at IS NULL OR t.expire_at >= SYSDATETIME())
                  AND (@channel IS NULL OR t.channel IS NULL OR t.channel = @channel)
                """,
                p2,
                commandType: CommandType.Text,
                cancellationToken: ct));

            return new GamStatusDto(
                Has_Checked_In_Today: hasCheckedInToday,
                Remaining_Spins: remainingSpins,
                Total_Points: totalPoints,
                Streak_Days: streakDays
            );
        }

        public async Task<LoyaltyRedeemResponseDto> RedeemRewardAsync(
    long userInfoId,
    long rewardId,
    int quantity,
    byte channel,
    long? deviceId,
    string? ip,
    CancellationToken ct)
        {
            using var con = _db.Create();

            var p = new DynamicParameters();
            p.Add("@user_info_id", userInfoId);
            p.Add("@reward_id", rewardId);
            p.Add("@quantity", quantity);
            p.Add("@channel", channel);
            p.Add("@ip", ip);

            var row = await con.QueryFirstAsync(new CommandDefinition(
                "dbo.usp_loyalty_redeem_reward",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            var dto = new LoyaltyRedeemResponseDto(
                Success: (bool)row.success,
                Error_Code: row.error_code as string,
                Error_Message: row.error_message as string,
                Points_Spent: row.points_spent is null ? null : (int?)row.points_spent,
                Total_Points: row.total_points is null ? null : (int?)row.total_points,
                Spins_Created: row.spins_created is null ? null : (int?)row.spins_created,
                Promotion_Code: row.promotion_code as string,
                Promotion_Issue_Id: row.promotion_issue_id is null ? null : (long?)row.promotion_issue_id
            );

            // Nếu muốn sau này có notify "đã đổi quà", có thể dùng _notifications ở đây (TODO).

            return dto;
        }

        public async Task<IReadOnlyList<LoyaltyRewardDto>> GetLoyaltyRewardsAsync(
    byte? channel,
    CancellationToken ct)
        {
            using var con = _db.Create();

            var p = new DynamicParameters();
            p.Add("@channel", channel);

            var rows = await con.QueryAsync(new CommandDefinition(
                """
        SELECT id,
               name,
               description,
               points_cost,
               reward_type,
               spins_created,
               promotion_id
        FROM dbo.tbl_loyalty_reward
        WHERE status = 1
          AND (start_at IS NULL OR start_at <= SYSDATETIME())
          AND (end_at   IS NULL OR end_at   >= SYSDATETIME())
          AND (@channel IS NULL OR channel IS NULL OR channel = @channel)
        ORDER BY points_cost, id
        """,
                p,
                commandType: CommandType.Text,
                cancellationToken: ct));

            var list = new List<LoyaltyRewardDto>();
            foreach (var r in rows)
            {
                list.Add(new LoyaltyRewardDto(
                    Id: (long)r.id,
                    Name: (string)r.name,
                    Description: r.description as string,
                    Points_Cost: (int)r.points_cost,
                    Reward_Type: (byte)r.reward_type,
                    Spins_Created: r.spins_created is null ? null : (int?)r.spins_created,
                    Promotion_Id: r.promotion_id is null ? null : (long?)r.promotion_id
                ));
            }

            return list;
        }


    }
}
