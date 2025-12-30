namespace HAShop.Api.Workers.ReviewModeration;

public interface IReviewModerationRunner
{
    Task RunAsync(long reviewId, CancellationToken ct = default);
}
