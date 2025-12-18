using HAShop.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace HAShop.Api.Workers
{
    public sealed class ScheduledArticlePublisherWorker : BackgroundService
    {
        private readonly ISqlConnectionFactory _db;
        private readonly ILogger<ScheduledArticlePublisherWorker> _log;

        public ScheduledArticlePublisherWorker(ISqlConnectionFactory db, ILogger<ScheduledArticlePublisherWorker> log)
        {
            _db = db;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Đợi app lên ổn định chút (optional)
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Nếu bạn đang chạy InMemory/NullSqlConnectionFactory thì sẽ throw -> log và chờ
                    await using var conn = (SqlConnection)_db.Create();
                    await conn.OpenAsync(stoppingToken);

                    await using var cmd = new SqlCommand("dbo.usp_article_publish_due", conn)
                    {
                        CommandType = CommandType.StoredProcedure,
                        CommandTimeout = 30
                    };
                    cmd.Parameters.Add(new SqlParameter("@maxBatch", SqlDbType.Int) { Value = 200 });

                    // SP trả về 1 dòng scalar: published_count
                    var obj = await cmd.ExecuteScalarAsync(stoppingToken);
                    var published = (obj == null || obj == DBNull.Value) ? 0 : Convert.ToInt32(obj);

                    if (published > 0)
                        _log.LogInformation("Published scheduled articles: {Count}", published);
                }
                catch (OperationCanceledException)
                {
                    // app shutdown
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "ScheduledArticlePublisherWorker error");
                }

                // Tần suất chạy (tuỳ bạn 10s/30s/60s)
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
