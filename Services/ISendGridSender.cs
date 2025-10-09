namespace HAShop.Api.Services
{
    public interface ISendGridSender
    {
        Task SendTemplateAsync(string to, string templateId, object variables, CancellationToken ct = default);
        Task<bool> SendTemplateEmailAsync(string to, string name, string otp, int ttlMin, CancellationToken ct = default);
    }
}
