using System.Text.Json;
using HAShop.Api.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace HAShop.Api.Sockets
{
    /// <summary>
    /// SSE endpoint cho FlashSale (stream thay đổi real-time).
    /// </summary>
    public static class FlashSaleSseEndpoint
    {
        // JSON option dùng cho SSE
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <summary>
        /// Đăng ký route SSE: GET /api/realtime/flashsale
        /// </summary>
        public static void MapFlashSaleSse(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/realtime/flashsale", FlashSaleStreamAsync)
                .WithDisplayName("FlashSale SSE stream")
                .AllowAnonymous(); // flashsale ngoài trang chủ
        }

        private static async Task FlashSaleStreamAsync(
            HttpContext context,
            [FromServices] IFlashSaleBroadcaster broadcaster,
            [FromQuery] byte? channel,
            CancellationToken token)
        {
            // Header SSE chuẩn
            context.Response.Headers["Content-Type"] = "text/event-stream; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["Connection"] = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no"; // cho Nginx

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                token, context.RequestAborted);
            var ct = cts.Token;

            // comment mở đầu
            await context.Response.WriteAsync($": connected {DateTime.UtcNow:o}\n\n", ct);
            await context.Response.Body.FlushAsync(ct);

            try
            {
                await foreach (var snap in broadcaster.Subscribe(channel, ct).WithCancellation(ct))
                {
                    var payload = new
                    {
                        channel = snap.Channel,
                        serverNow = snap.ServerNow,
                        items = snap.Items
                    };

                    var json = JsonSerializer.Serialize(payload, JsonOpts);

                    await context.Response.WriteAsync("event: flashsale.updated\n", ct);
                    await context.Response.WriteAsync("data: " + json + "\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // client đóng tab / hủy request → im lặng
            }
            catch (Exception ex)
            {
                // nếu muốn, có thể không gửi gì; ở đây gửi 1 event lỗi rồi thôi
                try
                {
                    var err = JsonSerializer.Serialize(new
                    {
                        message = "internal error",
                        detail = ex.Message
                    }, JsonOpts);

                    await context.Response.WriteAsync("event: flashsale.error\n", CancellationToken.None);
                    await context.Response.WriteAsync("data: " + err + "\n\n", CancellationToken.None);
                    await context.Response.Body.FlushAsync(CancellationToken.None);
                }
                catch
                {
                    // nuốt lỗi
                }
            }
        }
    }
}
