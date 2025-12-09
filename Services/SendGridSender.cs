using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using HAShop.Api.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HAShop.Api.Services
{
    /// <summary>
    /// Thực tế giờ là SMTP sender, nhưng giữ tên SendGridSender
    /// để không phải đổi DI / interface.
    /// </summary>
    public class SendGridSender : ISendGridSender
    {
        private readonly SmtpOptions _smtp;
        private readonly ILogger<SendGridSender> _log;

        public SendGridSender(
            IOptions<SmtpOptions> smtpOptions,
            ILogger<SendGridSender> log)
        {
            _smtp = smtpOptions.Value;
            _log = log;

            if (string.IsNullOrWhiteSpace(_smtp.Host))
                throw new InvalidOperationException("SMTP host is missing (Smtp:Host).");

            if (string.IsNullOrWhiteSpace(_smtp.FromEmail))
            {
                // fallback: dùng User làm FromEmail
                _smtp.FromEmail = _smtp.User;
            }
        }

        // ===== Interface chính: dùng cho EmailQueueWorker =====

        public Task SendTemplateAsync(
            string to,
            string templateId,
            object variables,
            CancellationToken ct = default)
            => SendTemplateCoreAsync(to, templateId, variables, ct, subjectOverride: null);

        public async Task<bool> SendTemplateEmailAsync(
            string to,
            string name,
            string otp,
            int ttlMin,
            CancellationToken ct = default)
        {
            try
            {
                await SendTemplateCoreAsync(
                    to,
                    templateId: "otp",
                    variables: new
                    {
                        NAME = name,
                        OTP = otp,
                        TTL_MIN = ttlMin,
                        Sender_Name = _smtp.FromName
                    },
                    ct,
                    subjectOverride: null);

                return true;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[SMTP] SendTemplateEmailAsync failed for {Email}", to);
                return false;
            }
        }

        // ===== Core SMTP send =====

        private async Task SendTemplateCoreAsync(
            string to,
            string templateId,
            object variables,
            CancellationToken ct,
            string? subjectOverride)
        {
            if (string.IsNullOrWhiteSpace(to))
                throw new ArgumentException("Recipient email is required", nameof(to));

            // Đọc variables (NAME, OTP, TTL_MIN, Sender_Name)
            JsonElement root;
            if (variables is JsonElement el)
                root = el;
            else
                root = JsonSerializer.SerializeToElement(variables);

            string name = TryGetString(root, "NAME") ?? "bạn";
            string otp = TryGetString(root, "OTP") ?? "";
            string ttlMinStr = TryGetString(root, "TTL_MIN") ?? "15";
            string senderName = TryGetString(root, "Sender_Name") ?? _smtp.FromName;

            if (string.IsNullOrEmpty(otp))
            {
                _log.LogWarning("SMTP email sent without OTP value. templateId={TemplateId}", templateId);
            }

            var subject = subjectOverride ?? $"[{senderName}] Mã xác thực OTP của bạn: {otp}";

            var bodyBuilder = new StringBuilder();
            bodyBuilder.AppendLine("<!DOCTYPE html>");
            bodyBuilder.AppendLine("<html><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;\">");
            bodyBuilder.AppendLine($"<p>Chào {WebUtility.HtmlEncode(name)},</p>");
            bodyBuilder.AppendLine("<p>Mã xác thực (OTP) của bạn là:</p>");
            bodyBuilder.AppendLine($"<p style=\"font-size:20px;font-weight:bold;letter-spacing:3px;\">{WebUtility.HtmlEncode(otp)}</p>");
            bodyBuilder.AppendLine($"<p>Mã có hiệu lực trong khoảng {WebUtility.HtmlEncode(ttlMinStr)} phút.</p>");
            bodyBuilder.AppendLine("<p>Nếu bạn không thực hiện yêu cầu này, vui lòng bỏ qua email.</p>");
            bodyBuilder.AppendLine($"<p>Trân trọng,<br/>{WebUtility.HtmlEncode(senderName)}</p>");
            bodyBuilder.AppendLine("</body></html>");

            using var message = new MailMessage
            {
                From = new MailAddress(_smtp.FromEmail, _smtp.FromName, Encoding.UTF8),
                Subject = subject,
                Body = bodyBuilder.ToString(),
                IsBodyHtml = true
            };

            message.To.Add(new MailAddress(to));

            using var client = new SmtpClient(_smtp.Host, _smtp.Port)
            {
                EnableSsl = _smtp.EnableSsl,
                Credentials = new NetworkCredential(_smtp.User, _smtp.Password)
            };

            try
            {
                await client.SendMailAsync(message, ct);
                _log.LogInformation("[SMTP] Email sent to {Email}", to);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[SMTP] Error sending email to {Email}", to);
                throw; // để Worker bắt & ghi vào last_error
            }
        }

        private static string? TryGetString(JsonElement root, string propName)
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(propName, out var child))
            {
                return child.ValueKind switch
                {
                    JsonValueKind.String => child.GetString(),
                    JsonValueKind.Number => child.ToString(),
                    _ => child.ToString()
                };
            }
            return null;
        }
    }
}
