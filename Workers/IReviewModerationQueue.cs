using System.Threading.Channels;

namespace HAShop.Api.Workers.ReviewModeration;

public interface IReviewModerationQueue
{
    bool Enqueue(long reviewId);
    ChannelReader<long> Reader { get; }
}
