namespace HAShop.Api.DTOs;

// Tóm tắt điểm thành viên
public sealed record LoyaltySummaryDto(
    long User_Info_Id,
    int Total_Points,
    int Lifetime_Points,
    byte Tier,
    int Streak_Days,
    int Max_Streak_Days,
    DateTime? Last_Checkin_Date
);

// Kết quả gọi SP usp_gam_checkin
public sealed record GamCheckinResponseDto(
    bool Success,
    string? Error_Code,
    string? Error_Message,
    int? Streak_Days,
    int? Total_Points,
    int? Spins_Created
);

// 1 lượt quay (chưa/quá/sử dụng)
public sealed record GamSpinTurnDto(
    long Id,
    long Spin_Config_Id,
    byte Status,
    DateTime Earned_At,
    DateTime? Used_At,
    DateTime? Expire_At
);

// Kết quả quay – SP usp_gam_spin_roll
public sealed record GamSpinRollResponseDto(
    bool Success,
    string? Error_Code,
    string? Error_Message,
    long? Spin_Result_Id,
    long? Config_Item_Id,
    byte? Reward_Type,
    decimal? Reward_Value,
    long? Promotion_Id,
    string? Promotion_Code,      // ✅ mới
    decimal? Min_Order_Amount,
    int? Total_Points,
    int? Points_Added,
    int? Extra_Spins_Created
);

public sealed record GamSpinConfigItemDto(
    long Id,
    int Display_Order,
    string Label,
    byte Reward_Type,          // 1=promotion,2=points,3=extra_spin,4=blank
    decimal? Reward_Value,
    long? Promotion_Id,
    decimal? Min_Order_Amount,
    int Weight,
    string? Icon_Key
);

public sealed record GamSpinConfigDto(
    long Id,
    string Name,
    string? Description,
    byte Status,
    byte? Channel,
    DateTime? Start_At,
    DateTime? End_At,
    int? Max_Spins_Per_Day,
    IReadOnlyList<GamSpinConfigItemDto> Items
);

public sealed record GamStatusDto(
       bool Has_Checked_In_Today,
       int Remaining_Spins,
       int? Total_Points,
       int? Streak_Days
   );