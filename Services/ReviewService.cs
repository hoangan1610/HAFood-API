using System.Data;
using System.Data.Common;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using HAShop.Api.Utils;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Text.Json;


namespace HAShop.Api.Services
{
    public interface IReviewService
    {
        Task<ProductReviewCreateResponse> CreateAsync(
            long userId,
            ProductReviewCreateRequest req,
            CancellationToken ct = default);

        Task<ProductReviewStatusUpdateResponse> SetStatusAsync(
            long adminUserId,
            long reviewId,
            byte newStatus,
            string rejectedReason,
            CancellationToken ct = default);

        Task<ProductReviewSummaryDto> GetSummaryAsync(
            long productId,
            long? variantId,
            CancellationToken ct = default);

        Task<ProductReviewListResponse> ListAsync(
            long productId,
            long? variantId,
            int page,
            int pageSize,
            byte? starFilter,
            bool? onlyHasImage,
            CancellationToken ct = default);

        Task<ProductReviewEligibilityDto> CheckEligibilityAsync(
            long userId,
            long productId,
            long? variantId,
            CancellationToken ct = default);

        Task AddImagesAsync(long reviewId, IEnumerable<string> imageUrls, CancellationToken ct = default);

        Task<ProductReviewReplyDto?> GetReplyAsync(long reviewId, CancellationToken ct = default);

        Task<ProductReviewReplySaveResponse> SaveReplyAsync(
            long adminUserId,
            long reviewId,
            string content,
            CancellationToken ct = default);
    }

    public sealed class ReviewService : IReviewService
    {
        private readonly ISqlConnectionFactory _dbFactory;
        private readonly ILogger<ReviewService> _logger;
        private readonly INotificationService _notifications;
        private readonly IMissionService _missions;
        private readonly IReviewModerationService _moderation;   // ✅ AI / rule duyệt review

        // Id "admin hệ thống" dùng cho auto duyệt
        // Bạn có thể đổi sang id thật của 1 admin trong DB
        private const long SystemAutoAdminUserId = 0;

        public ReviewService(
            ISqlConnectionFactory dbFactory,
            ILogger<ReviewService> logger,
            INotificationService notifications,
            IMissionService missions,
            IReviewModerationService moderation)
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _notifications = notifications;
            _missions = missions;
            _moderation = moderation;
        }

        // =========== CREATE ===========

        public async Task<ProductReviewCreateResponse> CreateAsync(
      long userId,
      ProductReviewCreateRequest req,
      CancellationToken ct = default)
        {
            if (req == null)
                throw new AppException("VALIDATION_FAILED", "Dữ liệu không hợp lệ.");

            if (req.Rating < 1 || req.Rating > 5)
                throw new AppException("RATING_OUT_OF_RANGE", "Điểm đánh giá phải từ 1 đến 5.");

            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            // Set session user nếu SP có xài
            await con.ExecuteAsync(new CommandDefinition(
                "EXEC sys.sp_set_session_context @key=N'user_id', @value=@v",
                new { v = userId },
                commandType: CommandType.Text,
                cancellationToken: ct,
                commandTimeout: 15
            ));

            var p = new DynamicParameters();
            p.Add("@user_info_id", userId, DbType.Int64);
            p.Add("@product_id", req.Product_Id, DbType.Int64);
            p.Add("@variant_id", req.Variant_Id, DbType.Int64);
            p.Add("@order_id", req.Order_Id, DbType.Int64);
            p.Add("@order_item_id", req.Order_Item_Id, DbType.Int64);

            p.Add("@rating", req.Rating, DbType.Byte);
            p.Add("@title", string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim(), DbType.String);
            p.Add("@content", string.IsNullOrWhiteSpace(req.Content) ? null : req.Content.Trim(), DbType.String);
            p.Add("@has_image", req.Has_Image ? 1 : 0, DbType.Byte);

            p.Add("@review_id", dbType: DbType.Int64, direction: ParameterDirection.Output);
            p.Add("@is_verified_purchase", dbType: DbType.Boolean, direction: ParameterDirection.Output);

            try
            {
                // 1) Tạo review
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_product_review_create",
                    p,
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct,
                    commandTimeout: 30
                ));

                var id = p.Get<long>("@review_id");
                var verified = p.Get<bool?>("@is_verified_purchase");

                // 2) Gọi mission sau khi tạo review thành công (giữ nguyên logic cũ)
                try
                {
                    await _missions.CheckReviewMissionsAsync(
                        reviewId: id,
                        userId: userId,
                        productId: req.Product_Id,
                        orderId: req.Order_Id,
                        rating: req.Rating,
                        ct: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "CheckReviewMissionsAsync failed. ReviewId={ReviewId}, UserId={UserId}",
                        id, userId);
                    // Không throw để không làm fail API review
                }

                // 3) Gọi AI / rule để auto duyệt / reject / pending
                try
                {
                    var mod = await _moderation.ModerateAsync(
                        productId: req.Product_Id,
                        userId: userId,
                        rating: req.Rating,
                        title: req.Title,
                        content: req.Content,
                        hasImage: req.Has_Image,
                        ct: ct);

                    // --- Lưu info AI vào 3 cột mới ---
                    byte? aiSource = 1; // 1 = rule/local AI (sau này nếu tách LLM riêng có thể gán 2)
                    string? aiReason = mod?.Reason;
                    string? aiFlagsJson = (mod?.Flags != null && mod.Flags.Length > 0)
                        ? JsonSerializer.Serialize(mod.Flags)
                        : null;

                    const string sqlUpdateAi = @"
UPDATE dbo.tbl_product_review
SET 
    ai_decision_source = @ai_source,
    ai_reason          = @ai_reason,
    ai_flags_json      = @ai_flags,
    updated_at         = SYSUTCDATETIME()
WHERE id = @id;";

                    await con.ExecuteAsync(new CommandDefinition(
                        sqlUpdateAi,
                        new
                        {
                            id,
                            ai_source = (object?)aiSource ?? DBNull.Value,
                            ai_reason = (object?)aiReason ?? DBNull.Value,
                            ai_flags = (object?)aiFlagsJson ?? DBNull.Value
                        },
                        cancellationToken: ct));

                    // --- Điều chỉnh status bằng API SetStatusAsync ---
                    if (mod.IsRejected)
                    {
                        await SetStatusAsync(
                            adminUserId: SystemAutoAdminUserId,
                            reviewId: id,
                            newStatus: 2, // REJECTED
                            rejectedReason: mod.Reason ?? "AUTO_REJECTED_BY_SYSTEM",
                            ct: ct);

                        _logger.LogInformation(
                            "Review {ReviewId} auto REJECTED by moderation. Reason={Reason}, Flags={Flags}",
                            id, mod.Reason, string.Join(',', mod.Flags));
                    }
                    else if (mod.IsApproved)
                    {
                        await SetStatusAsync(
                            adminUserId: SystemAutoAdminUserId,
                            reviewId: id,
                            newStatus: 1, // APPROVED
                            rejectedReason: string.Empty,
                            ct: ct);

                        _logger.LogInformation(
                            "Review {ReviewId} auto APPROVED by moderation. Reason={Reason}, Flags={Flags}",
                            id, mod.Reason, string.Join(',', mod.Flags));
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Review {ReviewId} requires manual review. Reason={Reason}, Flags={Flags}",
                            id, mod.Reason, string.Join(',', mod.Flags));
                        // status = 0 (PENDING) giữ nguyên
                    }
                }
                catch (Exception ex)
                {
                    // Nếu AI/moderation lỗi → không làm fail API review, chỉ log
                    _logger.LogError(ex,
                        "Moderation failed for review {ReviewId}. Keeping PENDING.",
                        id);
                }

                return new ProductReviewCreateResponse
                {
                    Success = true,
                    Code = null,
                    Message = "Đã gửi đánh giá.",
                    Review_Id = id,
                    Is_Verified_Purchase = verified
                };
            }
            // mapping lỗi nghiệp vụ từ SP
            catch (SqlException ex) when (ex.Number == 50501) // ORDER_NOT_FOUND hoặc ORDER_ITEM_NOT_FOUND
            {
                throw new AppException("ORDER_OR_ITEM_NOT_FOUND", ex.Message, ex);
            }
            catch (SqlException ex) when (ex.Number == 50502) // REVIEW_DUPLICATE
            {
                throw new AppException("REVIEW_ALREADY_EXISTS", ex.Message, ex);
            }
            catch (SqlException ex) when (ex.Number == 50503) // RATING_INVALID
            {
                throw new AppException("RATING_INVALID", ex.Message, ex);
            }
        }


        // =========== SET STATUS (ADMIN / SYSTEM) ===========

        public async Task<ProductReviewStatusUpdateResponse> SetStatusAsync(
            long adminUserId,
            long reviewId,
            byte newStatus,
            string rejectedReason,
            CancellationToken ct = default)
        {
            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var p = new DynamicParameters();
            p.Add("@review_id", reviewId, DbType.Int64);
            p.Add("@new_status", newStatus, DbType.Byte);
            p.Add("@admin_user_id", adminUserId, DbType.Int64);
            p.Add("@rejected_reason",
                string.IsNullOrWhiteSpace(rejectedReason) ? null : rejectedReason.Trim(),
                DbType.String);

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_product_review_set_status",
                    p,
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct,
                    commandTimeout: 30
                ));

                // Sau khi update thành công → gửi notify nếu cần
                try
                {
                    // Lấy lại thông tin review
                    var row = await con.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(
                        "SELECT user_info_id, product_id, rating, status FROM dbo.tbl_product_review WHERE id = @id",
                        new { id = reviewId },
                        cancellationToken: ct));

                    if (row != null)
                    {
                        long userId = row.user_info_id;
                        long productId = row.product_id;
                        byte status = row.status;

                        // Chỉ notify khi đã duyệt (status = 1)
                        if (status == 1)
                        {
                            var title = "Đánh giá của bạn đã được duyệt";
                            var body = "Cảm ơn bạn đã chia sẻ đánh giá về sản phẩm. Đánh giá đã được duyệt và hiển thị công khai.";

                            var dataObj = new
                            {
                                review_id = reviewId,
                                product_id = productId
                            };
                            var dataJson = System.Text.Json.JsonSerializer.Serialize(dataObj);

                            await _notifications.CreateInAppAsync(
                                userId,
                                NotificationTypes.REVIEW_REPLIED, // Nếu muốn có type riêng thì đổi ở đây
                                title,
                                body,
                                dataJson,
                                ct);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Send notification for review status change failed. ReviewId={ReviewId}",
                        reviewId);
                }

                return new ProductReviewStatusUpdateResponse
                {
                    Success = true,
                    Code = null,
                    Message = "Đã cập nhật trạng thái đánh giá."
                };
            }
            catch (SqlException ex) when (ex.Number == 50504) // REVIEW_NOT_FOUND
            {
                return new ProductReviewStatusUpdateResponse
                {
                    Success = false,
                    Code = "REVIEW_NOT_FOUND",
                    Message = ex.Message
                };
            }
            catch (SqlException ex) when (ex.Number == 50505) // REVIEW_ALREADY_APPROVED / INVALID_TRANSITION
            {
                return new ProductReviewStatusUpdateResponse
                {
                    Success = false,
                    Code = "REVIEW_STATUS_INVALID",
                    Message = ex.Message
                };
            }
            catch (SqlException ex) when (ex.Number == 50506) // PERMISSION_DENIED (nếu anh có)
            {
                return new ProductReviewStatusUpdateResponse
                {
                    Success = false,
                    Code = "PERMISSION_DENIED",
                    Message = ex.Message
                };
            }
        }

        // =========== SUMMARY ===========

        public async Task<ProductReviewSummaryDto> GetSummaryAsync(
            long productId,
            long? variantId,
            CancellationToken ct = default)
        {
            using var con = _dbFactory.Create();

            var p = new DynamicParameters();
            p.Add("@product_id", productId, DbType.Int64);
            p.Add("@variant_id", variantId, DbType.Int64);

            var cmd = new CommandDefinition(
                "dbo.usp_product_review_summary",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct,
                commandTimeout: 15
            );

            var dto = await con.QueryFirstOrDefaultAsync<ProductReviewSummaryDto>(cmd);

            // Nếu chưa có review, trả summary rỗng
            return dto ?? new ProductReviewSummaryDto
            {
                Product_Id = productId,
                Variant_Id = variantId,
                Total_Reviews = 0,
                Avg_Rating = 0
            };
        }

        // =========== LIST ===========

        public async Task<ProductReviewListResponse> ListAsync(
            long productId,
            long? variantId,
            int page,
            int pageSize,
            byte? starFilter,
            bool? onlyHasImage,
            CancellationToken ct = default)
        {
            using var con = _dbFactory.Create();

            if (page <= 0) page = 1;
            if (pageSize <= 0 || pageSize > 100) pageSize = 20;

            var p = new DynamicParameters();
            p.Add("@product_id", productId, DbType.Int64);
            p.Add("@variant_id", variantId, DbType.Int64);
            p.Add("@page", page, DbType.Int32);
            p.Add("@page_size", pageSize, DbType.Int32);
            p.Add("@star_filter", starFilter, DbType.Byte);          // nullable
            p.Add("@only_has_image", onlyHasImage, DbType.Boolean);  // nullable
            p.Add("@total_count", dbType: DbType.Int32, direction: ParameterDirection.Output);

            var cmd = new CommandDefinition(
                "dbo.usp_product_review_list",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct,
                commandTimeout: 30
            );

            var rows = (await con.QueryAsync<ProductReviewListItemDto>(cmd)).ToArray();
            var total = p.Get<int>("@total_count");

            return new ProductReviewListResponse(rows, total, page, pageSize);
        }

        // =========== ADD IMAGES ===========

        public async Task AddImagesAsync(long reviewId, IEnumerable<string> imageUrls, CancellationToken ct = default)
        {
            if (imageUrls == null) return;

            var urls = imageUrls
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (urls.Length == 0) return;

            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var now = DateTime.UtcNow;
            var order = 0;

            var rows = urls.Select(u => new
            {
                review_id = reviewId,
                image_url = u,
                sort_order = ++order,
                created_at = now
            });

            const string sqlInsert = @"
INSERT INTO dbo.tbl_product_review_image (review_id, image_url, sort_order, created_at)
VALUES (@review_id, @image_url, @sort_order, @created_at);";

            const string sqlUpdateFlag = @"
UPDATE dbo.tbl_product_review
SET has_image = 1, updated_at = SYSUTCDATETIME()
WHERE id = @id AND has_image = 0;";

            using (var tx = await ((DbConnection)con).BeginTransactionAsync(ct))
            {
                // insert ảnh
                await con.ExecuteAsync(new CommandDefinition(
                    sqlInsert,
                    rows,
                    transaction: (DbTransaction)tx,
                    cancellationToken: ct
                ));

                // đảm bảo cờ has_image = 1
                await con.ExecuteAsync(new CommandDefinition(
                    sqlUpdateFlag,
                    new { id = reviewId },
                    transaction: (DbTransaction)tx,
                    cancellationToken: ct
                ));

                await tx.CommitAsync(ct);
            }
        }

        // =========== ELIGIBILITY ===========

        public async Task<ProductReviewEligibilityDto> CheckEligibilityAsync(
            long userId,
            long productId,
            long? variantId,
            CancellationToken ct = default)
        {
            using var con = _dbFactory.Create();

            var p = new DynamicParameters();
            p.Add("@user_info_id", userId, DbType.Int64);
            p.Add("@product_id", productId, DbType.Int64);
            p.Add("@variant_id", variantId, DbType.Int64);

            var cmd = new CommandDefinition(
                "dbo.usp_product_review_can_create",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct,
                commandTimeout: 15
            );

            var row = await con.QueryFirstOrDefaultAsync(cmd);

            if (row == null)
            {
                return new ProductReviewEligibilityDto
                {
                    Can_Review = false,
                    Has_Purchase = false,
                    Already_Reviewed = false,
                    Last_Order_Id = null,
                    Last_Order_Item_Id = null
                };
            }

            return new ProductReviewEligibilityDto
            {
                Can_Review = (bool)(row.Can_Review ?? false),
                Has_Purchase = (bool)(row.Has_Purchase ?? false),
                Already_Reviewed = (bool)(row.Already_Reviewed ?? false),
                Last_Order_Id = (long?)(row.Last_Order_Id),
                Last_Order_Item_Id = (long?)(row.Last_Order_Item_Id)
            };
        }

        // =========== REPLY GET ===========

        public async Task<ProductReviewReplyDto?> GetReplyAsync(long reviewId, CancellationToken ct = default)
        {
            using var con = _dbFactory.Create();

            var cmd = new CommandDefinition(
                "dbo.usp_product_review_reply_get",
                new { review_id = reviewId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct);

            return await con.QueryFirstOrDefaultAsync<ProductReviewReplyDto>(cmd);
        }

        // =========== REPLY SAVE + NOTIFY ===========

        public async Task<ProductReviewReplySaveResponse> SaveReplyAsync(
            long adminUserId,
            long reviewId,
            string content,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new ProductReviewReplySaveResponse
                {
                    Success = false,
                    Code = "CONTENT_REQUIRED",
                    Message = "Nội dung phản hồi không được để trống."
                };
            }

            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var p = new DynamicParameters();
            p.Add("@review_id", reviewId, DbType.Int64);
            p.Add("@admin_user_id", adminUserId, DbType.Int64);
            p.Add("@content", content.Trim(), DbType.String);

            p.Add("@user_info_id", dbType: DbType.Int64, direction: ParameterDirection.Output);
            p.Add("@product_id", dbType: DbType.Int64, direction: ParameterDirection.Output);

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_product_review_reply_upsert",
                    p,
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct));

                var userId = p.Get<long>("@user_info_id");
                var productId = p.Get<long>("@product_id");

                // lấy lại reply để trả về UI
                var reply = await GetReplyAsync(reviewId, ct);

                // ==== Gửi thông báo cho khách ====
                try
                {
                    var dataObj = new
                    {
                        review_id = reviewId,
                        product_id = productId
                    };
                    var dataJson = System.Text.Json.JsonSerializer.Serialize(dataObj);

                    var title = "Shop đã trả lời đánh giá của bạn";
                    var body = content.Length > 120 ? content.Substring(0, 120) + "..." : content;

                    await _notifications.CreateInAppAsync(
                        userId,
                        NotificationTypes.REVIEW_REPLIED,
                        title,
                        body,
                        dataJson,
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Send notification REVIEW_REPLIED failed for review {ReviewId}", reviewId);
                }

                return new ProductReviewReplySaveResponse
                {
                    Success = true,
                    Reply = reply,
                    Message = "Đã lưu phản hồi."
                };
            }
            catch (SqlException ex) when (ex.Number == 50510) // REVIEW_NOT_FOUND
            {
                return new ProductReviewReplySaveResponse
                {
                    Success = false,
                    Code = "REVIEW_NOT_FOUND",
                    Message = "Không tìm thấy đánh giá."
                };
            }
        }
    }
}
