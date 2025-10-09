// Services/EmailQueueJob.cs
namespace HAShop.Api.Services;
public record EmailQueueJob(
    long Id,
    long OtpId,
    string Recipient,
    string TemplateId,
    string VariablesJson,
    string? IdempotencyKey
);
