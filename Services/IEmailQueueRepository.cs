// Services/IEmailQueueRepository.cs
using System.Data;
using Dapper;
using HAShop.Api.Data;

namespace HAShop.Api.Services;

public interface IEmailQueueRepository
{
    Task<IReadOnlyList<EmailQueueJob>> DequeueAsync(int max, CancellationToken ct);
    Task MarkSentAsync(long id, string? providerMsgId, CancellationToken ct);
    Task MarkFailedAsync(long id, string error, CancellationToken ct);
}

public class EmailQueueRepository : IEmailQueueRepository
{
    private readonly ISqlConnectionFactory _cf;
    public EmailQueueRepository(ISqlConnectionFactory cf) => _cf = cf;

    public async Task<IReadOnlyList<EmailQueueJob>> DequeueAsync(int max, CancellationToken ct)
    {
        using var conn = _cf.Create();
        var rows = await conn.QueryAsync<EmailQueueJob>(
            new CommandDefinition(
                "dbo.usp_email_queue_dequeue",
                new { max },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        return rows.AsList();
    }

    public async Task MarkSentAsync(long id, string? providerMsgId, CancellationToken ct)
    {
        using var conn = _cf.Create();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_email_queue_mark_sent",
                new { id, provider_msg_id = providerMsgId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
    }

    public async Task MarkFailedAsync(long id, string error, CancellationToken ct)
    {
        using var conn = _cf.Create();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "dbo.usp_email_queue_mark_failed",
                new { id, error },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
    }
}
