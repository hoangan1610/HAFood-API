using System.Data;
using System.Data.Common;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Services
{
    public interface INotificationService
    {
        Task<NotificationLatestResultDto> GetLatestAsync(
            long userId,
            byte channel,
            int take,
            CancellationToken ct = default);

        Task<NotificationPagedResultDto> GetPagedAsync(
            long userId,
            int page,
            int pageSize,
            byte channel,
            bool onlyUnread,
            byte? type,
            CancellationToken ct = default);

        Task<bool> MarkReadAsync(
            long userId,
            long notificationId,
            CancellationToken ct = default);

        Task<int> MarkAllReadAsync(
            long userId,
            byte channel,
            CancellationToken ct = default);

        // Cho các service khác gọi để tạo thông báo
        Task<long> AddAsync(
            NotificationAddCommand cmd,
            CancellationToken ct = default);

        // Helper cho in-app (channel=1)
        Task<long> CreateInAppAsync(
            long userId,
            byte type,
            string title,
            string body,
            string? dataJson,
            CancellationToken ct = default);
    }

    public sealed class NotificationService : INotificationService
    {
        private readonly ISqlConnectionFactory _dbFactory;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(
            ISqlConnectionFactory dbFactory,
            ILogger<NotificationService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        // Kiểu internal map đúng tên cột SQL
        private sealed class NotificationRow
        {
            public long id { get; set; }
            public byte type { get; set; }
            public byte channel { get; set; }
            public string title { get; set; } = string.Empty;
            public string body { get; set; } = string.Empty;
            public string? data { get; set; }
            public byte status { get; set; }
            public DateTime? delivered_at { get; set; }
            public DateTime? read_at { get; set; }
            public DateTime created_at { get; set; }
            public bool is_read { get; set; }
            public int? total_rows { get; set; }  // chỉ có ở SP paged
        }

        private static NotificationDto Map(NotificationRow r) =>
            new()
            {
                Id = r.id,
                Type = r.type,
                Channel = r.channel,
                Title = r.title,
                Body = r.body,
                Data = r.data,
                Status = r.status,
                DeliveredAt = r.delivered_at,
                ReadAt = r.read_at,
                CreatedAt = r.created_at,
                IsRead = r.is_read
            };

        public async Task<NotificationLatestResultDto> GetLatestAsync(
            long userId,
            byte channel,
            int take,
            CancellationToken ct = default)
        {
            if (take <= 0 || take > 50)
                take = 10;

            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var p = new DynamicParameters();
            p.Add("@user_info_id", userId, DbType.Int64);
            p.Add("@channel", channel, DbType.Byte);
            p.Add("@take", take, DbType.Int32);

            using var multi = await con.QueryMultipleAsync(new CommandDefinition(
                "dbo.usp_notification_get_latest",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            var rows = (await multi.ReadAsync<NotificationRow>()).ToList();
            var totalUnread = await multi.ReadSingleAsync<int>();

            var items = rows.Select(Map).ToList();

            return new NotificationLatestResultDto(items, totalUnread);
        }

        public async Task<NotificationPagedResultDto> GetPagedAsync(
            long userId,
            int page,
            int pageSize,
            byte channel,
            bool onlyUnread,
            byte? type,
            CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0 || pageSize > 200) pageSize = 20;

            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var p = new DynamicParameters();
            p.Add("@user_info_id", userId, DbType.Int64);
            p.Add("@page", page, DbType.Int32);
            p.Add("@page_size", pageSize, DbType.Int32);
            p.Add("@channel", channel, DbType.Byte);
            p.Add("@only_unread", onlyUnread, DbType.Boolean);
            p.Add("@type", type, DbType.Byte);

            using var multi = await con.QueryMultipleAsync(new CommandDefinition(
                "dbo.usp_notification_get_paged",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            var rows = (await multi.ReadAsync<NotificationRow>()).ToList();
            var totalUnread = await multi.ReadSingleAsync<int>();

            var totalRows = rows.Count == 0
                ? 0
                : rows[0].total_rows.GetValueOrDefault();

            var items = rows.Select(Map).ToList();

            return new NotificationPagedResultDto(
                Page: page,
                PageSize: pageSize,
                TotalRows: totalRows,
                TotalUnread: totalUnread,
                Items: items);
        }

        public async Task<bool> MarkReadAsync(
            long userId,
            long notificationId,
            CancellationToken ct = default)
        {
            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            await con.ExecuteAsync(new CommandDefinition(
                "EXEC sys.sp_set_session_context @key=N'user_id', @value=@v",
                new { v = userId },
                commandType: CommandType.Text,
                cancellationToken: ct));

            var p = new DynamicParameters();
            p.Add("@user_info_id", userId, DbType.Int64);
            p.Add("@notification_id", notificationId, DbType.Int64);

            var affected = await con.QuerySingleAsync<int>(new CommandDefinition(
                "dbo.usp_notification_mark_read",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            return affected > 0;
        }

        public async Task<int> MarkAllReadAsync(
            long userId,
            byte channel,
            CancellationToken ct = default)
        {
            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            await con.ExecuteAsync(new CommandDefinition(
                "EXEC sys.sp_set_session_context @key=N'user_id', @value=@v",
                new { v = userId },
                commandType: CommandType.Text,
                cancellationToken: ct));

            var p = new DynamicParameters();
            p.Add("@user_info_id", userId, DbType.Int64);
            p.Add("@channel", channel, DbType.Byte);

            var affected = await con.QuerySingleAsync<int>(new CommandDefinition(
                "dbo.usp_notification_mark_all_read",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            return affected;
        }

        public async Task<long> AddAsync(
            NotificationAddCommand cmd,
            CancellationToken ct = default)
        {
            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            await con.ExecuteAsync(new CommandDefinition(
                "EXEC sys.sp_set_session_context @key=N'user_id', @value=@v",
                new { v = cmd.UserInfoId },
                commandType: CommandType.Text,
                cancellationToken: ct));

            var p = new DynamicParameters();
            p.Add("@user_info_id", cmd.UserInfoId, DbType.Int64);
            p.Add("@device_id", cmd.DeviceId, DbType.Int64);
            p.Add("@order_id", cmd.OrderId, DbType.Int64);
            p.Add("@type", cmd.Type, DbType.Byte);
            p.Add("@channel", cmd.Channel, DbType.Byte);
            p.Add("@title", cmd.Title, DbType.String);
            p.Add("@body", cmd.Body, DbType.String);
            p.Add("@data", cmd.DataJson, DbType.String);
            p.Add("@id", dbType: DbType.Int64, direction: ParameterDirection.Output);

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_notification_add",
                    p,
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct));

                var id = p.Get<long>("@id");
                return id;
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex,
                    "Add notification failed for User {UserId}, Type {Type}, Channel {Channel}",
                    cmd.UserInfoId, cmd.Type, cmd.Channel);
                throw;
            }
        }

        public async Task<long> CreateInAppAsync(
            long userId,
            byte type,
            string title,
            string body,
            string? dataJson,
            CancellationToken ct = default)
        {
            var cmd = new NotificationAddCommand(
                UserInfoId: userId,
                DeviceId: null,
                OrderId: null,
                Type: type,
                Channel: 1, // in-app
                Title: title,
                Body: body,
                DataJson: dataJson
            );

            return await AddAsync(cmd, ct);
        }
    }
}
