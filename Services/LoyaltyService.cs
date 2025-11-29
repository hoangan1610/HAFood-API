using System;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using HAShop.Api.Utils;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Services
{
    public sealed class LoyaltyService : ILoyaltyService
    {
        private readonly ISqlConnectionFactory _dbFactory;
        private readonly INotificationService _notifications;
        private readonly ILogger<LoyaltyService> _logger;

        public LoyaltyService(
            ISqlConnectionFactory dbFactory,
            INotificationService notifications,
            ILogger<LoyaltyService> logger)
        {
            _dbFactory = dbFactory;
            _notifications = notifications;
            _logger = logger;
        }

        private static string MapTierName(byte tier) => tier switch
        {
            0 => "Thành viên",
            1 => "Silver",
            2 => "Gold",
            3 => "Platinum",
            _ => "Thành viên"
        };

        public async Task<LoyaltyOrderPointsResult> AddPointsFromOrderAsync(
            long userInfoId,
            long orderId,
            int points,
            string? reason,
            CancellationToken ct = default)
        {
            if (points <= 0)
            {
                return new LoyaltyOrderPointsResult
                {
                    User_Info_Id = userInfoId,
                    Order_Id = orderId,
                    Points_Added = 0,
                    New_Total_Points = 0,
                    Old_Tier = 0,
                    New_Tier = 0,
                    Tier_Changed = false
                };
            }

            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var p = new DynamicParameters();
            p.Add("@user_info_id", userInfoId, DbType.Int64);
            p.Add("@order_id", orderId, DbType.Int64);
            p.Add("@points", points, DbType.Int32);
            p.Add("@reason", string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), DbType.String);

            p.Add("@points_added", dbType: DbType.Int32, direction: ParameterDirection.Output);
            p.Add("@new_total_points", dbType: DbType.Int32, direction: ParameterDirection.Output);
            p.Add("@old_tier", dbType: DbType.Byte, direction: ParameterDirection.Output);
            p.Add("@new_tier", dbType: DbType.Byte, direction: ParameterDirection.Output);
            p.Add("@tier_changed", dbType: DbType.Boolean, direction: ParameterDirection.Output);

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_loyalty_add_points_for_order",
                    p,
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct,
                    commandTimeout: 30
                ));

                var pointsAdded = p.Get<int>("@points_added");
                var newTotal = p.Get<int>("@new_total_points");
                var oldTier = p.Get<byte>("@old_tier");
                var newTier = p.Get<byte>("@new_tier");
                var tierChanged = p.Get<bool>("@tier_changed");

                var result = new LoyaltyOrderPointsResult
                {
                    User_Info_Id = userInfoId,
                    Order_Id = orderId,
                    Points_Added = pointsAdded,
                    New_Total_Points = newTotal,
                    Old_Tier = oldTier,
                    New_Tier = newTier,
                    Tier_Changed = tierChanged
                };

                // Không cộng được (do points <=0 hoặc SP trả 0) thì thôi, không notify
                if (pointsAdded <= 0)
                    return result;

                // ===== Notify 1: LOYALTY_POINTS_EARNED =====
                try
                {
                    var dataObj = new
                    {
                        order_id = orderId,
                        points = pointsAdded,
                        total_points = newTotal,
                        reason = reason
                    };
                    var dataJson = JsonSerializer.Serialize(dataObj);

                    var title = $"Bạn vừa nhận +{pointsAdded} điểm HAFood";
                    var body = string.IsNullOrWhiteSpace(reason)
                        ? $"Cảm ơn bạn đã hoàn tất đơn hàng #{orderId}. Điểm tích luỹ hiện tại: {newTotal}."
                        : $"{reason}.\nĐiểm tích luỹ hiện tại: {newTotal}.";

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
                        "Send notification LOYALTY_POINTS_EARNED failed for order {OrderId}, user {UserId}",
                        orderId, userInfoId);
                }

                // ===== Notify 2: LOYALTY_TIER_CHANGED (nếu có) =====
                if (tierChanged)
                {
                    try
                    {
                        var oldTierName = MapTierName(oldTier);
                        var newTierName = MapTierName(newTier);

                        var title2 = newTier > oldTier
                            ? "Chúc mừng, bạn đã lên hạng thành viên!"
                            : "Hạng thành viên của bạn đã thay đổi";

                        var body2 = newTier > oldTier
                            ? $"Bạn vừa được nâng hạng từ {oldTierName} lên {newTierName}. Cảm ơn bạn đã đồng hành cùng HAFood!"
                            : $"Hạng thành viên của bạn thay đổi từ {oldTierName} sang {newTierName}.";

                        var dataObj2 = new
                        {
                            order_id = orderId,
                            old_tier = oldTier,
                            new_tier = newTier,
                            old_tier_name = oldTierName,
                            new_tier_name = newTierName,
                            total_points = newTotal
                        };
                        var dataJson2 = JsonSerializer.Serialize(dataObj2);

                        await _notifications.CreateInAppAsync(
                            userInfoId,
                            NotificationTypes.LOYALTY_TIER_CHANGED,
                            title2,
                            body2,
                            dataJson2,
                            ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Send notification LOYALTY_TIER_CHANGED failed for order {OrderId}, user {UserId}",
                            orderId, userInfoId);
                    }
                }

                return result;
            }
            catch (SqlException ex) when (ex.Number == 50601) // LOYALTY_USER_NOT_FOUND
            {
                throw new AppException("LOYALTY_USER_NOT_FOUND", ex.Message, ex);
            }
            catch (SqlException ex) when (ex.Number == 50602) // ORDER_NOT_COMPLETED
            {
                // tuỳ anh: có thể return result rỗng thay vì throw
                throw new AppException("ORDER_NOT_COMPLETED", ex.Message, ex);
            }
        }
    }
}
