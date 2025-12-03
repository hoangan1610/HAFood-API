namespace HAShop.Api.DTOs
{
    public sealed class LoyaltyOrderPointsResult
    {
        public long User_Info_Id { get; init; }
        public long Order_Id { get; init; }

        public int Points_Added { get; init; }
        public int New_Total_Points { get; init; }

        public byte Old_Tier { get; init; }
        public byte New_Tier { get; init; }
        public bool Tier_Changed { get; init; }
    }


    public sealed record LoyaltyRedeemRequestDto(
       long RewardId,
       int Quantity
   );

    public sealed record LoyaltyRedeemResponseDto(
        bool Success,
        string? Error_Code,
        string? Error_Message,
        int? Points_Spent,
        int? Total_Points,
        int? Spins_Created,
        string? Promotion_Code,
        long? Promotion_Issue_Id
    );

    public sealed record LoyaltyRewardDto(
        long Id,
        string Name,
        string? Description,
        int Points_Cost,
        byte Reward_Type,
        int? Spins_Created,
        long? Promotion_Id
    );
}
