// File: Api/DTOs/PromotionDtos.cs
using System.Collections.Generic;

namespace HAShop.Api.DTOs
{
    public record CartItemReq(long? ProductId, long? VariantId, decimal Qty, decimal UnitPrice);

    public record PromoListRequest(
        List<CartItemReq> Items,
        decimal Subtotal,
        decimal ShippingFee,
        byte? Channel
    );

    public record PromoCandidateDto(
        long PromotionId,
        string Code,
        string Name,
        byte Type,
        decimal Value,
        decimal? MaxDiscount,
        decimal? MinOrderAmount,
        byte ApplyScope,
        bool IsExclusive,
        bool IsStackable,
        byte Priority,
        decimal EstimatedDiscount,
        string StatusText,
        string Reason
    );

    public record PromoListResponse(List<PromoCandidateDto> Promotions);

    public record PromoQuoteRequest(
        string? Code,
        List<CartItemReq> Items,
        decimal Subtotal,
        decimal ShippingFee,
        byte? Channel
    );

    public record PromoBestDto(long? ChosenPromotionId, decimal TotalDiscount, byte? ApplyScope);

    public record PromoQuoteResponse(List<PromoCandidateDto> Candidates, PromoBestDto? Best);

    public record PromoReserveRequest(
        long OrderId,
        long? PromotionId,
        string? Code,
        long? UserInfoId,
        string? DeviceUuid,
        byte? Channel,
        decimal OrderSubtotal,
        decimal ShippingFee,
        decimal DiscountAmount,
        List<CartItemReq> Items,
        string? Ip
    );

    public record PromoReserveResponse(bool Success, int Code, string Message);

    public record PromoReleaseRequest(long OrderId, string? Reason);

    public record PromoReleaseResponse(bool Success, int ReleasedCount, int Code, string Message);
}
