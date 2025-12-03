namespace HAShop.Api.Utils
{
    /// <summary>
    /// Mã loại thông báo (mapping trực tiếp với cột [type] trong tbl_notification).
    /// Mỗi loại phải là số duy nhất.
    /// </summary>
    public static class NotificationTypes
    {
        // ========= ĐƠN HÀNG =========
        public const byte ORDER_STATUS_CHANGED = 1;

        // ========= REVIEW / ĐÁNH GIÁ =========
        public const byte REVIEW_APPROVED = 10;
        public const byte REVIEW_REPLIED = 11;

        // ========= LOYALTY / ĐIỂM TÍCH LUỸ =========
        public const byte LOYALTY_POINTS_EARNED = 20;
        public const byte LOYALTY_TIER_CHANGED = 21;

        // ========= GAMIFICATION (CHECKIN / SPIN / MISSION) =========
        public const byte CHECKIN_SUCCESS = 30;
        public const byte CHECKIN_ALREADY_DONE = 31;
        public const byte SPIN_BIG_PRIZE = 32;

        public const byte MISSION_COMPLETED = 40; // <--- ĐỔI THÀNH BYTE

        // ========= HỆ THỐNG =========
        public const byte SYSTEM_MESSAGE = 90;
    }

}
