using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.Services;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Workers.ReviewModeration;

public sealed class ReviewModerationRunner : IReviewModerationRunner
{
    private readonly ISqlConnectionFactory _dbFactory;
    private readonly IReviewModerationService _moderation;
    private readonly IReviewService _reviewService;                 // ✅ dùng để tạo notify qua SetStatusAsync
    private readonly ILogger<ReviewModerationRunner> _logger;

    private const long SystemAutoAdminUserId = 0;

    public ReviewModerationRunner(
        ISqlConnectionFactory dbFactory,
        IReviewModerationService moderation,
        IReviewService reviewService,
        ILogger<ReviewModerationRunner> logger)
    {
        _dbFactory = dbFactory;
        _moderation = moderation;
        _reviewService = reviewService;
        _logger = logger;
    }

    public async Task RunAsync(long reviewId, CancellationToken ct = default)
    {
        using var con = _dbFactory.Create();
        await ((DbConnection)con).OpenAsync(ct);

        // 1) Load review + idempotent + chỉ xử lý PENDING
        const string sqlGet = @"
SELECT TOP(1)
    id,
    product_id,
    user_info_id,
    rating,
    title,
    content,
    has_image,
    is_verified_purchase,
    status,
    is_hidden,
    ai_checked_at,
    ai_decision_source
FROM dbo.tbl_product_review WITH (READPAST)
WHERE id = @id;";

        var r = await con.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
            sqlGet, new { id = reviewId }, cancellationToken: ct, commandTimeout: 15));

        if (r == null)
        {
            _logger.LogWarning("Review not found for moderation. ReviewId={ReviewId}", reviewId);
            return;
        }

        // ✅ đã check AI rồi -> skip
        if (r.ai_checked_at != null || r.ai_decision_source != null)
            return;

        byte status = (byte)r.status;

        // ✅ chỉ xử lý review pending
        if (status != 0)
            return;

        bool isHidden = (bool)r.is_hidden;
        if (isHidden) return;

        long productId = (long)r.product_id;
        long userId = (long)r.user_info_id;
        int rating = (int)(byte)r.rating;
        string? title = r.title as string;
        string? content = r.content as string;
        bool hasImage = (bool)r.has_image;
        bool verified = (bool)r.is_verified_purchase;

        // 2) Moderate
        ReviewModerationResult mod;
        try
        {
            mod = await _moderation.ModerateAsync(
                productId: productId,
                userId: userId,
                rating: rating,
                title: title,
                content: content,
                hasImage: hasImage,
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModerateAsync failed. ReviewId={ReviewId}", reviewId);
            return; // giữ pending
        }

        // 3) Update AI fields (idempotent bằng WHERE ai_checked_at IS NULL)
        byte aiSource = 1;
        string? aiReason = mod.Reason;
        string[] flags = mod.Flags ?? Array.Empty<string>();
        string? aiFlagsJson = flags.Length > 0 ? JsonSerializer.Serialize(flags) : null;

        const string sqlUpdateAi = @"
UPDATE dbo.tbl_product_review
SET
    ai_decision_source = @ai_source,
    ai_reason          = @ai_reason,
    ai_flags_json      = @ai_flags_json,
    ai_checked_at      = SYSUTCDATETIME(),
    updated_at         = SYSUTCDATETIME()
WHERE id = @id
  AND ai_checked_at IS NULL;";

        var affected = await con.ExecuteAsync(new CommandDefinition(
            sqlUpdateAi,
            new
            {
                id = reviewId,
                ai_source = aiSource,
                ai_reason = (object?)aiReason ?? DBNull.Value,
                ai_flags_json = (object?)aiFlagsJson ?? DBNull.Value
            },
            cancellationToken: ct,
            commandTimeout: 15));

        // nếu 0 => có worker khác update trước rồi
        if (affected == 0)
            return;

        // =========================================================
        // 4) GATE: chỉ thỏa TẤT CẢ điều kiện mới được duyệt
        // =========================================================
        // Điều kiện cứng bạn có thể chỉnh:
        // A) Verified purchase bắt buộc
        // B) Không có flag nguy hiểm (contains_url, suspicious_contact_or_link,...)
        // C) Nội dung tối thiểu (tránh spam rỗng)
        // D) Moderation kết luận Approved
        // =========================================================

        // flag nguy hiểm: bạn đang dùng ["contains_url"] + reason "SUSPICIOUS_CONTACT_OR_LINK"
        bool hasUrlFlag = flags.Any(f => string.Equals(f, "contains_url", StringComparison.OrdinalIgnoreCase));
        bool suspicious = hasUrlFlag;

        // content tối thiểu (bạn chỉnh tùy ý)
        var titleTrim = (title ?? "").Trim();
        var contentTrim = (content ?? "").Trim();
        bool hasSomeText = (titleTrim.Length + contentTrim.Length) >= 6; // ví dụ: tổng >= 6 ký tự

        // Nếu moderation reject -> reject luôn
        if (mod.IsRejected)
        {
            await SafeRejectAsync(reviewId, mod.Reason ?? "AUTO_REJECTED", ct);
            return;
        }

        // Nếu có URL / suspicious -> reject luôn (đúng case facebook link)
        if (suspicious)
        {
            await SafeRejectAsync(reviewId, mod.Reason ?? "SUSPICIOUS_CONTACT_OR_LINK", ct);
            return;
        }

        // Nếu không verified hoặc text quá ngắn -> giữ pending (admin duyệt)
        if (!verified || !hasSomeText)
        {
            _logger.LogInformation(
                "Review {ReviewId} kept pending by gate. verified={Verified}, hasSomeText={HasSomeText}, reason={Reason}",
                reviewId, verified, hasSomeText, mod.Reason);
            return;
        }

        // Chỉ approve nếu moderation approved
        if (mod.IsApproved)
        {
            // ✅ quan trọng: đi qua ReviewService.SetStatusAsync để tạo notification
            await _reviewService.SetStatusAsync(
                adminUserId: SystemAutoAdminUserId,
                reviewId: reviewId,
                newStatus: 1,
                rejectedReason: "",
                ct: ct);

            _logger.LogInformation("Review {ReviewId} auto APPROVED. Reason={Reason}", reviewId, mod.Reason);
            return;
        }

        // còn lại: manual -> giữ pending (KHÔNG gọi set_status=0 vì đang 0 sẽ bị 50505)
        _logger.LogInformation("Review {ReviewId} kept pending (manual). Reason={Reason}", reviewId, mod.Reason);
    }

    private async Task SafeRejectAsync(long reviewId, string reason, CancellationToken ct)
    {
        try
        {
            await _reviewService.SetStatusAsync(
                adminUserId: SystemAutoAdminUserId,
                reviewId: reviewId,
                newStatus: 2,
                rejectedReason: reason,
                ct: ct);

            _logger.LogInformation("Review {ReviewId} auto REJECTED. Reason={Reason}", reviewId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reject failed (maybe already moved). ReviewId={ReviewId}", reviewId);
        }
    }
}
