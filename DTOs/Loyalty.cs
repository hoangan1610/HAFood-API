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
}
