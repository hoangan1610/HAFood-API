// File: DTOs/ShippingQuoteRequest.cs
namespace HAShop.Api.DTOs
{
    public sealed class ShippingQuoteRequest
    {
        // Mã tỉnh/thành (ví dụ "79" cho TP.HCM)
        public string? CityCode { get; set; }

        // Mã phường/xã (ví dụ "26882"), có thể null nếu anh mới tính theo tỉnh
        public string? WardCode { get; set; }

        // Tổng tiền hàng (chưa VAT, chưa ship)
        public decimal Subtotal { get; set; }

        // Tổng khối lượng gram (BE/C# đang pass int)
        public int TotalWeightGram { get; set; }

        // Kênh (web = 1, app = 2...), default 1
        public byte Channel { get; set; } = 1;
    }
}
