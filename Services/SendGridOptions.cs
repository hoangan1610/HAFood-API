namespace HAShop.Api.Services
{
    public class SendGridOptions
    {
        public string ApiKey { get; set; } = "";
        public string FromEmail { get; set; } = "";
        public string FromName { get; set; } = "HAFood";
        public string TemplateId { get; set; } = "";
        public int BatchSize { get; set; } = 50;
        public int PollIntervalSeconds { get; set; } = 2;
    }
}
