namespace HAShop.Api.Services
{
    public interface IIntentService
    {
        (string intent, float confidence) PredictIntent(string q, string? a = null);
        void Reload();
    }
}
