using System.Data.Common;
using Dapper;
using HAShop.Api.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Workers.ReviewModeration;

public sealed class ReviewModerationWorker : BackgroundService
{
    private readonly IReviewModerationQueue _queue;
    private readonly IServiceProvider _sp;
    private readonly ILogger<ReviewModerationWorker> _logger;

    // interval quét DB để recover job
    private static readonly TimeSpan RecoverInterval = TimeSpan.FromSeconds(10);
    private const int RecoverBatchSize = 50;

    public ReviewModerationWorker(
        IReviewModerationQueue queue,
        IServiceProvider sp,
        ILogger<ReviewModerationWorker> logger)
    {
        _queue = queue;
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // chạy song song: 1) loop xử lý queue, 2) loop recover
        var processTask = ProcessQueueLoop(stoppingToken);
        var recoverTask = RecoverLoop(stoppingToken);

        await Task.WhenAll(processTask, recoverTask);
    }

    private async Task ProcessQueueLoop(CancellationToken ct)
    {
        await foreach (var reviewId in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var scope = _sp.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<IReviewModerationRunner>();
                await runner.RunAsync(reviewId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Moderation worker failed. ReviewId={ReviewId}", reviewId);
            }
        }
    }

    private async Task RecoverLoop(CancellationToken ct)
    {
        // chạy ngay 1 lần khi start
        await RecoverPendingAsync(ct);

        using var timer = new PeriodicTimer(RecoverInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await RecoverPendingAsync(ct);
        }
    }

    private async Task RecoverPendingAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

            using var con = dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            // NOTE: dựa vào ai_checked_at null => chưa xử lý
            const string sql = @"
SELECT TOP (@take) id
FROM dbo.tbl_product_review
WHERE status = 0
  AND ai_checked_at IS NULL
ORDER BY id ASC;";

            var ids = (await con.QueryAsync<long>(new CommandDefinition(
                sql,
                new { take = RecoverBatchSize },
                cancellationToken: ct,
                commandTimeout: 15
            ))).ToArray();

            foreach (var id in ids)
                _queue.Enqueue(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RecoverPendingAsync failed");
        }
    }
}
