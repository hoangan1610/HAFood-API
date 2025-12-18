using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Services
{
    public sealed class ReviewModerationResult
    {
        /// <summary>true = có thể auto duyệt (status = APPROVED)</summary>
        public bool IsApproved { get; init; }

        /// <summary>true = nên reject luôn</summary>
        public bool IsRejected { get; init; }

        /// <summary>true = giữ pending cho admin duyệt tay</summary>
        public bool RequiresManualReview { get; init; }

        /// <summary>Lý do / nhãn để log / hiển thị cho admin</summary>
        public string? Reason { get; init; }

        /// <summary>Các flag (VD: contains_url, contains_phone, low_quality, ...)</summary>
        public string[] Flags { get; init; } = Array.Empty<string>();
    }

    public interface IReviewModerationService
    {
        Task<ReviewModerationResult> ModerateAsync(
            long productId,
            long userId,
            int rating,
            string? title,
            string? content,
            bool hasImage,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Moderation = Rule (rẻ) + optional LLM (Qwen local).
    /// Nếu config OpenAI không có / UseLlm = false thì chỉ dùng rule.
    /// </summary>
    public sealed class SimpleReviewModerationService : IReviewModerationService
    {
        private readonly ILogger<SimpleReviewModerationService> _logger;
        private readonly HashSet<string> _bannedWords;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly bool _useLlm;
        private readonly string? _openAiBaseUrl;
        private readonly string? _openAiModel;
        private readonly string? _openAiApiKey;

        private static readonly Regex PhoneRegex =
            new(@"\b\d{9,11}\b", RegexOptions.Compiled);

        private static readonly Regex UrlRegex =
            new(@"https?://|www\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex EmailRegex =
            new(@"\b\S+@\S+\.\S+\b", RegexOptions.Compiled);

        private sealed class LlmModerationDecisionDto
        {
            public string? decision { get; set; }   // "approve" | "reject" | "manual_review"
            public string? reason { get; set; }
            public string[]? flags { get; set; }
        }

        public SimpleReviewModerationService(
            IConfiguration cfg,
            ILogger<SimpleReviewModerationService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;

            // bad words từ appsettings
            var words = cfg.GetSection("Reviews:Moderation:BannedWords").Get<string[]>()
                        ?? Array.Empty<string>();
            _bannedWords = new HashSet<string>(words, StringComparer.OrdinalIgnoreCase);

            _useLlm = cfg.GetValue<bool?>("Reviews:Moderation:UseLlm") ?? true;

            _openAiBaseUrl = cfg["OpenAI:BaseUrl"];
            _openAiModel = cfg["OpenAI:Model"];
            _openAiApiKey = cfg["OpenAI:ApiKey"];
        }

        public async Task<ReviewModerationResult> ModerateAsync(
            long productId,
            long userId,
            int rating,
            string? title,
            string? content,
            bool hasImage,
            CancellationToken ct = default)
        {
            var flags = new List<string>();
            var text = ((title ?? string.Empty) + "\n" + (content ?? string.Empty)).Trim();

            // 1) Không có nội dung gì → reject
            if (string.IsNullOrWhiteSpace(text))
            {
                return new ReviewModerationResult
                {
                    IsRejected = true,
                    RequiresManualReview = false,
                    Reason = "EMPTY_CONTENT",
                    Flags = new[] { "empty" }
                };
            }

            // 2) Nội dung quá ngắn
            if (text.Length < 5)
            {
                flags.Add("too_short");
            }

            // 3) Check phone / url / email để tránh spam
            var hasPhone = PhoneRegex.IsMatch(text);
            var hasUrl = UrlRegex.IsMatch(text);
            var hasEmail = EmailRegex.IsMatch(text);

            if (hasPhone) flags.Add("contains_phone");
            if (hasUrl) flags.Add("contains_url");
            if (hasEmail) flags.Add("contains_email");

            // 4) Check banned words
            var lowered = text.ToLowerInvariant();
            var bannedHit = _bannedWords.Any(bw =>
                !string.IsNullOrWhiteSpace(bw) &&
                lowered.Contains(bw.ToLowerInvariant()));

            if (bannedHit)
            {
                flags.Add("contains_banned_word");

                // Banned word → reject ngay, không cần LLM
                return new ReviewModerationResult
                {
                    IsApproved = false,
                    IsRejected = true,
                    RequiresManualReview = false,
                    Reason = "CONTAINS_BANNED_WORD",
                    Flags = flags.ToArray()
                };
            }

            // 5) Có phone / url / email → bắt buộc review tay
            if (hasPhone || hasUrl || hasEmail)
            {
                return new ReviewModerationResult
                {
                    IsApproved = false,
                    IsRejected = false,
                    RequiresManualReview = true,
                    Reason = "SUSPICIOUS_CONTACT_OR_LINK",
                    Flags = flags.ToArray()
                };
            }

            // 6) Case đẹp: rating cao, nội dung rõ ràng → auto approve, khỏi gọi LLM
            if (rating >= 4 && text.Length >= 20 && !flags.Contains("too_short"))
            {
                return new ReviewModerationResult
                {
                    IsApproved = true,
                    IsRejected = false,
                    RequiresManualReview = false,
                    Reason = "AUTO_APPROVED_POSITIVE",
                    Flags = flags.ToArray()
                };
            }

            // 7) Mặc định: để PENDING
            var baseResult = new ReviewModerationResult
            {
                IsApproved = false,
                IsRejected = false,
                RequiresManualReview = true,
                Reason = "REQUIRES_MANUAL_REVIEW",
                Flags = flags.ToArray()
            };

            // 8) Nếu không cấu hình LLM → trả luôn kết quả rule
            if (!_useLlm ||
                string.IsNullOrWhiteSpace(_openAiBaseUrl) ||
                string.IsNullOrWhiteSpace(_openAiModel))
            {
                return baseResult;
            }

            // 9) Quyết định có nên gọi LLM hay không
            if (!ShouldCallLlm(rating, text, flags))
            {
                return baseResult;
            }

            try
            {
                var llmResult = await CallLlmAsync(
                    productId,
                    userId,
                    rating,
                    title,
                    content,
                    hasImage,
                    flags,
                    ct);

                if (llmResult == null)
                {
                    // LLM không trả về gì tử tế → giữ baseResult
                    return baseResult;
                }

                return llmResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "LLM moderation failed for product {ProductId}, user {UserId}. Fallback to manual review.",
                    productId, userId);

                // lỗi LLM → không làm fail, vẫn cho pending
                return baseResult;
            }
        }

        private static bool ShouldCallLlm(int rating, string text, List<string> flags)
        {
            // Gợi ý:
            // - Rating <= 3
            // - Hoặc nội dung quá ngắn
            // - Hoặc có ảnh (hasImage) nhưng text ngắn
            if (rating <= 3) return true;
            if (flags.Contains("too_short")) return true;
            if (text.Length < 20) return true;

            return false;
        }

        private async Task<ReviewModerationResult?> CallLlmAsync(
            long productId,
            long userId,
            int rating,
            string? title,
            string? content,
            bool hasImage,
            List<string> flags,
            CancellationToken ct)
        {
            var client = _httpClientFactory.CreateClient();

            var endpoint = _openAiBaseUrl!.TrimEnd('/') + "/chat/completions";

            var sb = new StringBuilder();
            sb.AppendLine("Bạn là hệ thống kiểm duyệt nội dung đánh giá sản phẩm cho sàn thương mại điện tử.");
            sb.AppendLine("Nhiệm vụ:");
            sb.AppendLine("- Phát hiện nội dung xúc phạm, bạo lực, thù ghét, 18+.");
            sb.AppendLine("- Phát hiện spam, quảng cáo, lừa đảo.");
            sb.AppendLine("- Giữ lại các đánh giá trung thực, kể cả đánh giá xấu nhưng lịch sự.");
            sb.AppendLine();
            sb.AppendLine("Hãy trả về DUY NHẤT một JSON object với cấu trúc:");
            sb.AppendLine(@"{");
            sb.AppendLine(@"  ""decision"": ""approve"" | ""reject"" | ""manual_review"",");
            sb.AppendLine(@"  ""reason"": ""chuỗi lý do ngắn gọn"",");
            sb.AppendLine(@"  ""flags"": [""flag1"", ""flag2""]");
            sb.AppendLine(@"}");
            sb.AppendLine();
            sb.AppendLine("Không giải thích dài dòng, không thêm text ngoài JSON.");

            var userContent = new StringBuilder();
            userContent.AppendLine($"Rating: {rating}/5");
            userContent.AppendLine($"HasImage: {hasImage}");
            if (flags.Count > 0)
            {
                userContent.AppendLine("PreFlags: " + string.Join(", ", flags));
            }
            userContent.AppendLine();
            userContent.AppendLine("Tiêu đề:");
            userContent.AppendLine(title ?? string.Empty);
            userContent.AppendLine();
            userContent.AppendLine("Nội dung:");
            userContent.AppendLine(content ?? string.Empty);

            var requestBody = new
            {
                model = _openAiModel,
                temperature = 0.1,
                max_tokens = 128,
                messages = new[]
                {
                    new { role = "system", content = sb.ToString() },
                    new { role = "user",   content = userContent.ToString() }
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(_openAiApiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _openAiApiKey);
            }

            using var resp = await client.SendAsync(req, ct);
            var respJson = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "LLM moderation HTTP {Status}. Body={Body}",
                    resp.StatusCode, respJson);
                return null;
            }

            // parse OpenAI-style response
            using var doc = JsonDocument.Parse(respJson);
            var root = doc.RootElement;

            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return null;

            var contentStr = choices[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(contentStr))
                return null;

            // Model có thể trả “```json … ```”, nên cắt phần JSON
            contentStr = ExtractJson(contentStr);

            LlmModerationDecisionDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<LlmModerationDecisionDto>(contentStr);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Cannot parse LLM moderation JSON. Raw={Raw}",
                    contentStr);
                return null;
            }

            if (dto == null || string.IsNullOrWhiteSpace(dto.decision))
                return null;

            var allFlags = new List<string>(flags);
            if (dto.flags != null)
                allFlags.AddRange(dto.flags.Where(f => !string.IsNullOrWhiteSpace(f)));

            var decisionLower = dto.decision.Trim().ToLowerInvariant();

            return decisionLower switch
            {
                "approve" => new ReviewModerationResult
                {
                    IsApproved = true,
                    IsRejected = false,
                    RequiresManualReview = false,
                    Reason = dto.reason ?? "LLM_APPROVED",
                    Flags = allFlags.Distinct().ToArray()
                },
                "reject" => new ReviewModerationResult
                {
                    IsApproved = false,
                    IsRejected = true,
                    RequiresManualReview = false,
                    Reason = dto.reason ?? "LLM_REJECTED",
                    Flags = allFlags.Distinct().ToArray()
                },
                _ => new ReviewModerationResult
                {
                    IsApproved = false,
                    IsRejected = false,
                    RequiresManualReview = true,
                    Reason = dto.reason ?? "LLM_MANUAL_REVIEW",
                    Flags = allFlags.Distinct().ToArray()
                }
            };
        }

        private static string ExtractJson(string content)
        {
            // Nếu model trả kiểu:
            // ```json
            // { ... }
            // ```
            // thì cắt 2 đầu.
            var trimmed = content.Trim();

            if (trimmed.StartsWith("```"))
            {
                var idx = trimmed.IndexOf('{');
                var last = trimmed.LastIndexOf('}');
                if (idx >= 0 && last > idx)
                {
                    return trimmed.Substring(idx, last - idx + 1);
                }
            }

            return trimmed;
        }
    }
}
