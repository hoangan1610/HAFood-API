namespace HAShop.Api.DTOs
{
    public record CheckoutResponseDto(long Order_Id, string Order_Code, string? Payment_Url);

}
