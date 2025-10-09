using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HAShop.Api.Services
{
    public class SendGridSender : ISendGridSender
    {
        private readonly HttpClient _http;
        private readonly SendGridOptions _opt;
        private static readonly Uri _uri = new("https://api.sendgrid.com/v3/mail/send");

        public SendGridSender(IHttpClientFactory f, IOptions<SendGridOptions> opt)
        {
            _http = f.CreateClient(nameof(SendGridSender));
            _opt = opt.Value;

            if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            {
                throw new InvalidOperationException("⚠️ SendGrid API key is missing. Please set SENDGRID_API_KEY in your environment variables or .env file.");
            }

            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        }

        // ✅ Implement interface
        public Task SendTemplateAsync(string to, string templateId, object variables, CancellationToken ct = default)
            => SendTemplateCoreAsync(to, templateId, variables, ct, subjectOverride: null);

        // 🔧 Core có thêm subjectOverride
        private async Task SendTemplateCoreAsync(
            string to,
            string templateId,
            object variables,
            CancellationToken ct,
            string? subjectOverride)
        {
            // ⚙️ Tạo personalization object
            var personalization = new
            {
                to = new[] { new { email = to } },
                dynamic_template_data = variables,
                subject = subjectOverride // null thì JSON bỏ qua
            };

            var payload = new
            {
                personalizations = new[] { personalization },
                from = new { email = _opt.FromEmail, name = _opt.FromName },
                template_id = string.IsNullOrWhiteSpace(templateId) ? _opt.TemplateId : templateId,

                categories = new[] { "transactional-otp" },
                tracking_settings = new
                {
                    click_tracking = new { enable = false, enable_text = false },
                    open_tracking = new { enable = false }
                }
            };

            var opts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                DictionaryKeyPolicy = null
            };

            using var content = JsonContent.Create(payload, options: opts);
            using var resp = await _http.PostAsync(_uri, content, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"❌ SendGrid error {(int)resp.StatusCode}: {body}"
                );
            }
        }

        public async Task<bool> SendTemplateEmailAsync(
            string to,
            string name,
            string otp,
            int ttlMin,
            CancellationToken ct = default)
        {
            try
            {
                await SendTemplateAsync(
                    to,
                    _opt.TemplateId,
                    new { NAME = name, OTP = otp, TTL_MIN = ttlMin, Sender_Name = "HAFood" },
                    ct
                );
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SendGridSender] ❌ Error: {ex.Message}");
                return false;
            }
        }
    }
}
