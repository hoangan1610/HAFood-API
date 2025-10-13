using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using System.Data;

namespace HAShop.Api.Services;

public interface INotificationService
{
    Task<long> CreateAsync(long userId, long? deviceId, long? orderId, byte type, byte channel,
                           string title, string body, string? data, byte status, CancellationToken ct);
    Task<NotificationsPageDto> ListByUserAsync(long userId, byte? status, int page, int pageSize, CancellationToken ct);
    Task MarkReadAsync(long id, CancellationToken ct);
    Task MarkAllReadAsync(long userId, CancellationToken ct);
    Task MarkDeliveredAsync(long id, CancellationToken ct);
    Task<int> GetUnreadCountAsync(long userId, CancellationToken ct);
}

public class NotificationService(ISqlConnectionFactory db) : INotificationService
{
    public async Task<long> CreateAsync(long userId, long? deviceId, long? orderId, byte type, byte channel,
                                        string title, string body, string? data, byte status, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@user_info_id", userId);
        p.Add("@device_id", deviceId);
        p.Add("@order_id", orderId);
        p.Add("@type", type);
        p.Add("@channel", channel);
        p.Add("@title", title);
        p.Add("@body", body);
        p.Add("@data", data);
        p.Add("@status", status);
        p.Add("@id", dbType: DbType.Int64, direction: ParameterDirection.Output);

        await con.ExecuteAsync(new CommandDefinition("dbo.usp_notification_create", p,
            commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return p.Get<long>("@id");
    }

    public async Task<NotificationsPageDto> ListByUserAsync(long userId, byte? status, int page, int pageSize, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@user_info_id", userId);
        p.Add("@status", status);
        p.Add("@page", page);
        p.Add("@page_size", pageSize);
        p.Add("@total_count", dbType: DbType.Int32, direction: ParameterDirection.Output);

        var items = (await con.QueryAsync<NotificationDto>(new CommandDefinition(
            "dbo.usp_notifications_list_by_user", p, commandType: CommandType.StoredProcedure, cancellationToken: ct))).AsList();
        var total = p.Get<int>("@total_count");
        return new NotificationsPageDto(items, total, page, pageSize);
    }

    public async Task MarkReadAsync(long id, CancellationToken ct)
    {
        using var con = db.Create();
        await con.ExecuteAsync(new CommandDefinition("dbo.usp_notification_mark_read",
            new { id }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task MarkAllReadAsync(long userId, CancellationToken ct)
    {
        using var con = db.Create();
        await con.ExecuteAsync(new CommandDefinition("dbo.usp_notification_mark_all_read",
            new { user_info_id = userId }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task MarkDeliveredAsync(long id, CancellationToken ct)
    {
        using var con = db.Create();
        await con.ExecuteAsync(new CommandDefinition("dbo.usp_notification_mark_delivered",
            new { id }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<int> GetUnreadCountAsync(long userId, CancellationToken ct)
    {
        using var con = db.Create();
        return await con.ExecuteScalarAsync<int>(new CommandDefinition(
            "dbo.usp_notification_unread_count", new { user_info_id = userId },
            commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }
}
