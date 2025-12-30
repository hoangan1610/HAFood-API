using System.Threading.Channels;

namespace HAShop.Api.Workers.ReviewModeration;

public sealed class ReviewModerationQueue : IReviewModerationQueue
{
    private readonly Channel<long> _ch;

    public ReviewModerationQueue()
    {
        _ch = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ChannelReader<long> Reader => _ch.Reader;

    public bool Enqueue(long reviewId) => _ch.Writer.TryWrite(reviewId);
}
