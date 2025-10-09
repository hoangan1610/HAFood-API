// Services/EmailQueueWorker.cs
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace HAShop.Api.Services;

public class EmailQueueWorker : BackgroundService
{
    private readonly IEmailQueueRepository _repo;
    private readonly ISendGridSender _sender;
    private readonly SendGridOptions _opt;
    private readonly ILogger<EmailQueueWorker> _log;

    public EmailQueueWorker(
        IEmailQueueRepository repo,
        ISendGridSender sender,
        IOptions<SendGridOptions> opt,
        ILogger<EmailQueueWorker> log)
    {
        _repo = repo; _sender = sender; _opt = opt.Value; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("EmailQueueWorker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var jobs = await _repo.DequeueAsync(_opt.BatchSize, stoppingToken);
                if (jobs.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_opt.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                foreach (var j in jobs)
                {
                    try
                    {
                        var vars = JsonSerializer.Deserialize<object>(j.VariablesJson)
                                   ?? new { };
                        await _sender.SendTemplateAsync(j.Recipient, j.TemplateId, vars, stoppingToken);
                        await _repo.MarkSentAsync(j.Id, null, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Send failed for job {Id}", j.Id);
                        await _repo.MarkFailedAsync(j.Id, ex.Message, stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker loop error");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        _log.LogInformation("EmailQueueWorker stopped.");
    }
}
