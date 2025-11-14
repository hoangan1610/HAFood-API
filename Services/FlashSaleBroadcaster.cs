using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace HAShop.Api.Realtime
{
    // Snapshot gửi ra cho client
    public sealed record FlashSaleSnapshot(
        byte? Channel,
        DateTime ServerNow,
        IReadOnlyList<FlashSaleActiveItemDto> Items
    );

    public interface IFlashSaleBroadcaster
    {
        /// <summary>
        /// Client subscribe theo channel, nhận về stream snapshot.
        /// </summary>
        IAsyncEnumerable<FlashSaleSnapshot> Subscribe(byte? channel, CancellationToken ct);
    }

    public sealed class FlashSaleBroadcaster : BackgroundService, IFlashSaleBroadcaster
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<FlashSaleBroadcaster> _log;

        // key channel -> (subscriberId -> subscriber)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Subscriber>> _subscribers =
            new();

        // key channel -> last hash (dùng để tránh broadcast trùng)
        private readonly ConcurrentDictionary<string, string> _lastHashes = new();

        // key channel -> last snapshot (để subscriber mới vào nhận ngay)
        private readonly ConcurrentDictionary<string, FlashSaleSnapshot> _lastSnapshots = new();

        private sealed class Subscriber
        {
            public Channel<FlashSaleSnapshot> Stream { get; } =
                System.Threading.Channels.Channel.CreateUnbounded<FlashSaleSnapshot>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false
                    });

            public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        }

        public FlashSaleBroadcaster(IServiceScopeFactory scopeFactory, ILogger<FlashSaleBroadcaster> log)
        {
            _scopeFactory = scopeFactory;
            _log = log;
        }

        private static string ChannelKey(byte? channel) =>
            channel.HasValue ? channel.Value.ToString() : "null";

        // API cho SSE endpoint
        public IAsyncEnumerable<FlashSaleSnapshot> Subscribe(byte? channel, CancellationToken ct)
        {
            var key = ChannelKey(channel);
            var dict = _subscribers.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, Subscriber>());

            var id = Guid.NewGuid();
            var sub = new Subscriber();
            dict[id] = sub;

            // Nếu đã có snapshot cuối cùng cho channel này → bắn ngay 1 cái cho client mới
            if (_lastSnapshots.TryGetValue(key, out var snap))
            {
                sub.Stream.Writer.TryWrite(snap);
            }

            return ReadLoopAsync(id, key, sub, ct);
        }

        private async IAsyncEnumerable<FlashSaleSnapshot> ReadLoopAsync(
            Guid subscriberId,
            string key,
            Subscriber sub,
            [EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                await foreach (var item in sub.Stream.Reader.ReadAllAsync(ct))
                {
                    sub.LastSeenUtc = DateTime.UtcNow;
                    yield return item;
                }
            }
            finally
            {
                if (_subscribers.TryGetValue(key, out var dict))
                    dict.TryRemove(subscriberId, out _);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var pollWhenActive = TimeSpan.FromSeconds(5);
            var pollWhenIdle = TimeSpan.FromSeconds(20);
            var cleanupInterval = TimeSpan.FromMinutes(2);
            var lastCleanup = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var keys = _subscribers.Keys.ToArray();
                    if (keys.Length == 0)
                    {
                        await Task.Delay(pollWhenIdle, stoppingToken);
                        continue;
                    }

                    using var scope = _scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<IFlashSaleService>();

                    foreach (var key in keys)
                    {
                        if (!_subscribers.TryGetValue(key, out var subs) || subs.IsEmpty)
                            continue;

                        byte? channel = key == "null" ? (byte?)null : byte.Parse(key);

                        var list = (await svc.GetActiveAsync(channel, stoppingToken))
                                   ?? Array.Empty<FlashSaleActiveItemDto>();

                        // Tạo bản "ổn định" chỉ chứa các field quan trọng, KHÔNG chứa server_Now
                        var stableItems = list.Select(x => new
                        {
                            x.Vpo_Id,
                            x.Variant_Id,
                            x.Start_At,
                            x.End_At,
                            x.Qty_Cap_Total,
                            x.Sold_Count,
                            x.Retail_Price,
                            x.Sale_Price,
                            x.Percent_Off,
                            x.Effective_Price
                        }).ToArray();

                        // tính hash theo (channel + stableItems) để tránh gửi trùng
                        var hashPayload = new { channel, items = stableItems };
                        var hashJson = JsonSerializer.Serialize(hashPayload);
                        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashJson));
                        var hash = Convert.ToBase64String(hashBytes);

                        if (_lastHashes.TryGetValue(key, out var last) && last == hash)
                            continue; // không đổi gì => khỏi broadcast

                        _lastHashes[key] = hash;

                        var snapshot = new FlashSaleSnapshot(channel, DateTime.UtcNow, list);
                        _lastSnapshots[key] = snapshot; // để subscriber mới vào thấy ngay

                        foreach (var kv in subs.ToArray())
                        {
                            var sub = kv.Value;
                            sub.Stream.Writer.TryWrite(snapshot);
                        }
                    }


                    // dọn subscriber old
                    var now = DateTime.UtcNow;
                    if (now - lastCleanup >= cleanupInterval)
                    {
                        foreach (var pair in _subscribers)
                        {
                            var subs = pair.Value;
                            foreach (var kv in subs.ToArray())
                            {
                                if (now - kv.Value.LastSeenUtc > TimeSpan.FromMinutes(5))
                                {
                                    subs.TryRemove(kv.Key, out _);
                                    kv.Value.Stream.Writer.TryComplete();
                                }
                            }
                        }
                        lastCleanup = now;
                    }

                    await Task.Delay(pollWhenActive, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "FlashSaleBroadcaster loop error");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }

            foreach (var dict in _subscribers.Values)
            {
                foreach (var sub in dict.Values)
                {
                    sub.Stream.Writer.TryComplete();
                }
            }
        }
    }
}
