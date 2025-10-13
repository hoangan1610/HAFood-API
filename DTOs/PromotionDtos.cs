namespace HAShop.Api.DTOs;

// FE gọi để xem trước giảm giá
public record PromotionPreviewRequest(
    string Code,
    long? CartId,          // nếu có, BE tự tính subtotal từ giỏ
    decimal? SubTotal      // hoặc FE truyền sẵn (ưu tiên CartId nếu cả 2 cùng có)
);

public record PromotionPreviewResponse(
    bool Valid,
    decimal Discount,
    long? PromotionId,
    string? Code,
    string? Message
);
