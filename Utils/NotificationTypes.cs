namespace HAShop.Api.Utils
{
    /// <summary>
    /// Mã loại thông báo (mapping trực tiếp với cột [type] trong tbl_notification).
    /// Mỗi loại phải là số duy nhất.
    /// </summary>
    public static class NotificationTypes
    {
        // ========= ĐƠN HÀNG =========
        public const byte ORDER_STATUS_CHANGED = 1;   // đã dùng ở đâu đó cho luồng order

        // ========= REVIEW / ĐÁNH GIÁ =========
        public const byte REVIEW_APPROVED = 10;       // Đánh giá được duyệt
        public const byte REVIEW_REPLIED = 11;       // Shop phản hồi đánh giá

        // ========= LOYALTY / ĐIỂM TÍCH LUỸ =========
        public const byte LOYALTY_POINTS_EARNED = 20; // cộng điểm từ order / spin / checkin
        public const byte LOYALTY_TIER_CHANGED = 21; // lên / xuống hạng

        // ========= GAMIFICATION (CHECKIN / SPIN) =========
        public const byte CHECKIN_SUCCESS = 30; // điểm danh thành công
        public const byte CHECKIN_ALREADY_DONE = 31; // (tuỳ có dùng hay không)
        public const byte SPIN_BIG_PRIZE = 32; // trúng thưởng lớn từ vòng quay

        // ========= HỆ THỐNG =========
        public const byte SYSTEM_MESSAGE = 90; // broadcast chung
    }
}
