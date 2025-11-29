namespace HAShop.Api.DTOs
{
    // ===== CREATE REVIEW =====
    public sealed class ProductReviewCreateRequest
    {
        public long Product_Id { get; set; }
        public long? Variant_Id { get; set; }
        public long? Order_Id { get; set; }
        public long? Order_Item_Id { get; set; }


        public byte Rating { get; set; }            // 1..5
        public string Title { get; set; }           // có thể null/empty
        public string Content { get; set; }         // có thể null/empty
        public bool Has_Image { get; set; }         // FE gửi true nếu có upload ảnh kèm
    }

    public sealed class ProductReviewCreateResponse
    {
        public bool Success { get; set; }
        public string Code { get; set; }            // nullable
        public string Message { get; set; }         // nullable

        public long? Review_Id { get; set; }
        public bool? Is_Verified_Purchase { get; set; }
    }

    // ===== UPDATE STATUS (ADMIN) =====
    public sealed class ProductReviewStatusUpdateRequest
    {
        public byte New_Status { get; set; }        // 0=pending, 1=approved, 2=rejected
        public string Rejected_Reason { get; set; } // nullable
    }

    public sealed class ProductReviewStatusUpdateResponse
    {
        public bool Success { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
    }

    // ===== SUMMARY =====
    public sealed class ProductReviewSummaryDto
    {
        public long Product_Id { get; set; }
        public long? Variant_Id { get; set; }

        public int Total_Reviews { get; set; }
        public decimal Avg_Rating { get; set; }

        public int Star_1_Count { get; set; }
        public int Star_2_Count { get; set; }
        public int Star_3_Count { get; set; }
        public int Star_4_Count { get; set; }
        public int Star_5_Count { get; set; }

        public int Verified_Count { get; set; }
        public int Has_Image_Count { get; set; }

        public DateTime? Last_Review_At { get; set; }
    }

    // ===== LIST =====
    public sealed class ProductReviewListItemDto
    {
        public long Id { get; set; }
        public long Product_Id { get; set; }

        // 🔹 nên sửa thành nullable cho khớp DB:
        public long? Variant_Id { get; set; }

        public long User_Info_Id { get; set; }
        public string User_Name { get; set; }
        public string User_Avatar { get; set; }

        public byte Rating { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }

        public bool Has_Image { get; set; }
        public int Like_Count { get; set; }
        public int Reply_Count { get; set; }

        public bool Is_Verified_Purchase { get; set; }

        public byte Status { get; set; }
        public DateTime Created_At { get; set; }

        // ảnh
        public string First_Image_Url { get; set; } = string.Empty;
        public int Image_Count { get; set; }

        // reply
        public bool Has_Reply { get; set; }
        public string? Reply_Content { get; set; }
        public DateTime? Reply_Created_At { get; set; }

        public string? Reply_Admin_Name { get; set; }
    }




    public sealed class ProductReviewListResponse
    {
        public ProductReviewListResponse(
            ProductReviewListItemDto[] items,
            int totalCount,
            int page,
            int pageSize)
        {
            Items = items;
            Total_Count = totalCount;
            Page = page;
            Page_Size = pageSize;
        }

        public ProductReviewListItemDto[] Items { get; }
        public int Total_Count { get; }
        public int Page { get; }
        public int Page_Size { get; }
    }

    public sealed class ProductReviewEligibilityDto
    {
        public bool Can_Review { get; set; }
        public bool Has_Purchase { get; set; }
        public bool Already_Reviewed { get; set; }

        public long? Last_Order_Id { get; set; }
        public long? Last_Order_Item_Id { get; set; }
    }

    public sealed class ProductReviewCreateWithImagesRequest
    {
        public long Product_Id { get; set; }
        public long? Variant_Id { get; set; }
        public long? Order_Id { get; set; }
        public long? Order_Item_Id { get; set; }
        public byte Rating { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }

        // FE vẫn có thể gửi, nhưng BE sẽ override theo Images
        public bool Has_Image { get; set; }

        // 🔹 file input: name="images"
        public List<IFormFile> Images { get; set; } = new();
    }

    public sealed class ProductReviewReplyDto
    {
        public long Id { get; set; }
        public long Review_Id { get; set; }
        public long Admin_User_Id { get; set; }
        public string Content { get; set; } = "";
        public DateTime Created_At { get; set; }
    }

    public sealed class ProductReviewReplySaveRequest
    {
        public string Content { get; set; } = "";
    }

    public sealed class ProductReviewReplySaveResponse
    {
        public bool Success { get; set; }
        public string? Code { get; set; }
        public string? Message { get; set; }

        public ProductReviewReplyDto? Reply { get; set; }
    }

}
