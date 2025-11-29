using System.Security.Claims;
using HAShop.Api.DTOs;
using HAShop.Api.Services;
using HAShop.Api.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers
{
    [ApiController]
    [Route("api")]
    public sealed class ReviewsController : ControllerBase
    {
        private readonly IReviewService _reviews;
        private readonly IWebHostEnvironment _env;

        public ReviewsController(IReviewService reviews, IWebHostEnvironment env)
        {
            _reviews = reviews;
            _env = env;
        }

        // ===== Tạo review (user đã đăng nhập, có thể kèm ảnh) =====
        [HttpPost("reviews")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [Consumes("multipart/form-data")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ProductReviewCreateResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<ProductReviewCreateResponse>> CreateReview(
            [FromForm] ProductReviewCreateWithImagesRequest form,
            CancellationToken ct)
        {
            if (!(User?.Identity?.IsAuthenticated ?? false))
                throw new AppException("UNAUTHENTICATED");

            var userId = GetUserIdFromClaims(User);
            if (userId is null)
                throw new AppException("TOKEN_INVALID_OR_NO_USERID");

            // Chuẩn hoá: 0 => null
            if (form.Order_Id.HasValue && form.Order_Id <= 0) form.Order_Id = null;
            if (form.Order_Item_Id.HasValue && form.Order_Item_Id <= 0) form.Order_Item_Id = null;
            if (form.Variant_Id.HasValue && form.Variant_Id <= 0) form.Variant_Id = null;

            var hasImages = form.Images != null && form.Images.Count > 0;

            var baseReq = new ProductReviewCreateRequest
            {
                Product_Id = form.Product_Id,
                Variant_Id = form.Variant_Id,
                Order_Id = form.Order_Id,
                Order_Item_Id = form.Order_Item_Id,
                Rating = form.Rating,
                Title = form.Title,
                Content = form.Content,
                Has_Image = hasImages
            };

            // 1) Tạo review trong DB
            var res = await _reviews.CreateAsync(userId.Value, baseReq, ct);

            if (!res.Success || res.Review_Id is null || res.Review_Id <= 0)
                throw new AppException(res.Code ?? "ERROR", res.Message ?? "Tạo đánh giá thất bại.");

            var reviewId = res.Review_Id.Value;

            // 2) Nếu có ảnh: lưu file + insert tbl_product_review_image
            if (hasImages)
            {
                var urls = new List<string>();

                foreach (var file in form.Images)
                {
                    if (file == null || file.Length == 0) continue;

                    // validate MIME đơn giản
                    if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // giới hạn 2MB / ảnh
                    if (file.Length > 2 * 1024 * 1024)
                        continue;

                    var ext = Path.GetExtension(file.FileName);
                    if (string.IsNullOrWhiteSpace(ext))
                        ext = ".jpg";

                    var fileName = $"{Guid.NewGuid():N}{ext}";

                    // uploads/reviews/{reviewId}/file.jpg
                    var relativeFolder = Path.Combine("uploads", "reviews", reviewId.ToString());
                    var rootPath = _env.WebRootPath ?? _env.ContentRootPath;
                    var physicalFolder = Path.Combine(rootPath, relativeFolder);

                    Directory.CreateDirectory(physicalFolder);

                    var physicalPath = Path.Combine(physicalFolder, fileName);
                    using (var stream = System.IO.File.Create(physicalPath))
                    {
                        await file.CopyToAsync(stream, ct);
                    }

                    var url = "/" + Path.Combine(relativeFolder, fileName).Replace("\\", "/");
                    urls.Add(url);
                }

                if (urls.Count > 0)
                {
                    await _reviews.AddImagesAsync(reviewId, urls, ct);
                }
            }

            return Ok(res);
        }

        // ===== Admin: duyệt / từ chối review =====
        // Có thể thêm Roles="Admin" nếu anh đã khai báo.
        [HttpPost("admin/reviews/{id:long}/status")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ProductReviewStatusUpdateResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<ProductReviewStatusUpdateResponse>> UpdateReviewStatus(
            long id,
            [FromBody] ProductReviewStatusUpdateRequest body,
            CancellationToken ct)
        {
            if (!(User?.Identity?.IsAuthenticated ?? false))
                throw new AppException("UNAUTHENTICATED");

            var adminId = GetUserIdFromClaims(User);
            if (adminId is null)
                throw new AppException("TOKEN_INVALID_OR_NO_USERID");

            var res = await _reviews.SetStatusAsync(
                adminId.Value,
                id,
                body.New_Status,
                body.Rejected_Reason,
                ct);

            if (!res.Success)
                throw new AppException(res.Code ?? "ERROR", res.Message ?? "Cập nhật thất bại.");

            return Ok(res);
        }

        // ===== Summary theo product / variant =====
        [HttpGet("products/{productId:long}/reviews/summary")]
        [AllowAnonymous]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ProductReviewSummaryDto), StatusCodes.Status200OK)]
        public async Task<ActionResult<ProductReviewSummaryDto>> GetSummary(
            long productId,
            [FromQuery] long? variantId,
            CancellationToken ct)
        {
            var dto = await _reviews.GetSummaryAsync(productId, variantId, ct);
            return Ok(dto);
        }

        // ===== Danh sách review (public) =====
        [HttpGet("products/{productId:long}/reviews")]
        [AllowAnonymous]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ProductReviewListResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<ProductReviewListResponse>> GetReviews(
            long productId,
            [FromQuery] long? variantId,
            [FromQuery] int page = 1,
            [FromQuery(Name = "page_size")] int pageSize = 20,
            [FromQuery(Name = "star")] byte? starFilter = null,
            [FromQuery(Name = "has_image")] bool? onlyHasImage = null,
            CancellationToken ct = default)
        {
            var res = await _reviews.ListAsync(
                productId,
                variantId,
                page,
                pageSize,
                starFilter,
                onlyHasImage,
                ct);

            return Ok(res);
        }

        // ===== Check quyền review =====
        [HttpGet("products/{productId:long}/reviews/eligibility")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ProductReviewEligibilityDto), StatusCodes.Status200OK)]
        public async Task<ActionResult<ProductReviewEligibilityDto>> GetEligibility(
            long productId,
            [FromQuery] long? variantId,
            CancellationToken ct)
        {
            if (!(User?.Identity?.IsAuthenticated ?? false))
                throw new AppException("UNAUTHENTICATED");

            var userId = GetUserIdFromClaims(User);
            if (userId is null)
                throw new AppException("TOKEN_INVALID_OR_NO_USERID");

            var dto = await _reviews.CheckEligibilityAsync(
                userId.Value,
                productId,
                variantId,
                ct);

            return Ok(dto);
        }

        // ===== Admin: lấy reply =====
        [HttpGet("admin/reviews/{id:long}/reply")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ProductReviewReplyDto), StatusCodes.Status200OK)]
        public async Task<ActionResult<ProductReviewReplyDto?>> GetReviewReply(
            long id,
            CancellationToken ct)
        {
            var adminId = GetUserIdFromClaims(User);
            if (adminId is null)
                return Unauthorized();

            var dto = await _reviews.GetReplyAsync(id, ct);
            return Ok(dto);
        }

        // ===== Admin: lưu reply =====
        [HttpPost("admin/reviews/{id:long}/reply")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ProductReviewReplySaveResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<ProductReviewReplySaveResponse>> SaveReviewReply(
            long id,
            [FromBody] ProductReviewReplySaveRequest body,
            CancellationToken ct)
        {
            var adminId = GetUserIdFromClaims(User);
            if (adminId is null)
                return Unauthorized();

            var res = await _reviews.SaveReplyAsync(adminId.Value, id, body.Content ?? "", ct);

            if (!res.Success)
                return BadRequest(res);

            return Ok(res);
        }

        // ===== Helper =====
        private static long? GetUserIdFromClaims(ClaimsPrincipal user)
        {
            string raw =
                user.FindFirstValue("sub") ??
                user.FindFirstValue("user_id") ??
                user.FindFirstValue(ClaimTypes.NameIdentifier) ??
                user.FindFirstValue("uid");

            return long.TryParse(raw, out var id) ? id : (long?)null;
        }
    }
}
