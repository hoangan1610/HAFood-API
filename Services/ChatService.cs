using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.Data.SqlClient;
using Scriban;
using System.Text.RegularExpressions;
using System.Security.Claims;
using System.Data;
using Microsoft.Extensions.Configuration;
using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;

public interface IChatService
{
    Task<string> AskAsync(string message, ClaimsPrincipal user, CancellationToken ct = default);
}

public sealed class ChatService : IChatService
{
    private readonly IHttpClientFactory _hf;
    private readonly IConfiguration _cfg;
    private readonly IChatTools _tools; // để tương thích
    private readonly string _conn;

    // === Logging ===
    private readonly string _logDir;
    private static readonly SemaphoreSlim _logSlim = new(1, 1);
    private static readonly SemaphoreSlim _errSlim = new(1, 1);

    // Tùy chọn múi giờ VN cho tên file log
    private readonly bool _useLocalTz;
#if WINDOWS
    private static readonly string TzId = "SE Asia Standard Time"; // Windows TZ
#else
    private static readonly string TzId = "Asia/Ho_Chi_Minh"; // IANA TZ
#endif
    private static TimeZoneInfo? _tz;

    // JSON options cho NDJSON
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Rolling size cho file log (10 MB)
    private const long MaxBytesPerFile = 10 * 1024 * 1024;

    // Scriban template cache
    private static readonly Template _tplOrder =
        Template.Parse("Đơn **{{code}}** đang ở **{{status}}**, dự kiến {{eta}}. Tổng thanh toán **{{pay_total}}**.");

    // Regex options mặc định
    private const RegexOptions RX = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // Culture VI để format tiền nhất quán
    private static readonly CultureInfo Vi = new("vi-VN");

    public ChatService(IHttpClientFactory hf, IConfiguration cfg, IChatTools tools)
    {
        _hf = hf; _cfg = cfg; _tools = tools;

        _conn = cfg.GetConnectionString("Default")
              ?? cfg.GetConnectionString("Sql")
              ?? throw new InvalidOperationException("Missing ConnectionStrings:Default (or Sql).");

        _logDir = _cfg["Chat:LogDir"]
               ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "chatlogs");

        Directory.CreateDirectory(_logDir); // fail-fast quyền ghi

        _useLocalTz = _cfg.GetValue<bool>("Chat:UseLocalTz");
        if (_useLocalTz)
        {
            try { _tz = TimeZoneInfo.FindSystemTimeZoneById(TzId); }
            catch
            {
                try
                {
#if WINDOWS
                    _tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
#else
                    _tz = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
#endif
                }
                catch { _tz = null; }
            }
        }
    }

    private DateTime GetNowForFileName()
    {
        var nowUtc = DateTime.UtcNow;
        if (_useLocalTz && _tz != null) return TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _tz);
        return nowUtc;
    }

    private string GetQaBasePath()
        => Path.Combine(_logDir, $"chat-{GetNowForFileName():yyyyMMdd}.ndjson");

    private string GetErrBasePath()
        => Path.Combine(_logDir, $"errors-{GetNowForFileName():yyyyMMdd}.ndjson");

    // Rolling theo kích thước: chat-yyyymmdd.ndjson, chat-yyyymmdd.1.ndjson, ...
    private static string NextWritablePath(string basePath)
    {
        var dir = Path.GetDirectoryName(basePath)!;
        var file = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);

        int idx = 0;
        while (true)
        {
            var candidate = idx == 0
                ? Path.Combine(dir, $"{file}{ext}")
                : Path.Combine(dir, $"{file}.{idx}{ext}");

            if (!File.Exists(candidate)) return candidate;
            var len = new FileInfo(candidate).Length;
            if (len < MaxBytesPerFile) return candidate;
            idx++;
        }
    }

    // == Ghi NDJSON trong critical section để tránh race & overflow ==
    private static async Task AppendLineRollingAsync(string basePath, string line, SemaphoreSlim gate)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            var path = NextWritablePath(basePath); // quyết định trong lock

            var fi = new FileInfo(path);
            var bytes = Encoding.UTF8.GetByteCount(line) + 1; // + \n

            if (fi.Exists && fi.Length + bytes > MaxBytesPerFile)
            {
                var dir = Path.GetDirectoryName(basePath)!;
                var file = Path.GetFileNameWithoutExtension(basePath);
                var ext = Path.GetExtension(basePath);

                int idx = 1;
                string cand;
                do { cand = Path.Combine(dir, $"{file}.{idx}{ext}"); idx++; }
                while (File.Exists(cand) && new FileInfo(cand).Length >= MaxBytesPerFile);

                path = cand;
            }

            await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
            await using var sw = new StreamWriter(fs, new UTF8Encoding(false));
            await sw.WriteLineAsync(line).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // --- Redact dữ liệu nhạy cảm (điện thoại/email/chuỗi số dài), có whitelist ---
    private static string Redact(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var t = s;

        // Whitelist: HA*** (mã đơn), SKU-***
        t = Regex.Replace(t,
            @"(?<!HA)(?<!SKU-)(?<!SKU)(?<!#[A-Z]*)\b\d{9,}\b",
            m => new string('*', Math.Min(m.Value.Length, 6)) + "…",
            RX);

        // Ẩn số điện thoại
        t = Regex.Replace(t, @"\b(\d{2,3})(?:[\s\.-]?\d){4,}(\d{2})\b", "$1***$2", RX);

        // Ẩn email
        t = Regex.Replace(
            t,
            @"([A-Za-z0-9][A-Za-z0-9._%+\-]{0,})@([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+)",
            m => $"{m.Groups[1].Value[0]}***@{m.Groups[2].Value}",
            RX
        );

        return t;
    }

    // --- Ghi Q&A với meta optional ---
    private async Task AppendLogAsync(string question, string answer, ClaimsPrincipal user, object? meta = null, CancellationToken ct = default)
    {
        try
        {
            var uid = GetUserId(user);
            var entry = new
            {
                ts = DateTimeOffset.UtcNow,
                type = "qa",
                userId = uid,
                q = Redact(question ?? ""),
                a = answer ?? "",
                meta
            };
            var json = JsonSerializer.Serialize(entry, _jsonOpts);
            await AppendLineRollingAsync(GetQaBasePath(), json, _logSlim).ConfigureAwait(false);
        }
        catch { /* nuốt lỗi log */ }
    }

    private async Task AppendErrorAsync(Exception ex, string question, ClaimsPrincipal? user, string stage, object? extra = null, CancellationToken ct = default)
    {
        try
        {
            var uid = GetUserId(user);
            var entry = new
            {
                ts = DateTimeOffset.UtcNow,
                type = "error",
                stage,
                userId = uid,
                q = Redact(question ?? ""),
                exType = ex.GetType().FullName,
                exMsg = ex.Message,
                exStack = ex.StackTrace,
                extra
            };
            var json = JsonSerializer.Serialize(entry, _jsonOpts);
            await AppendLineRollingAsync(GetErrBasePath(), json, _errSlim).ConfigureAwait(false);
        }
        catch { /* nuốt lỗi log */ }
    }

    /// <summary>Entry point chính cho /api/chat/ask</summary>
    public async Task<string> AskAsync(string message, ClaimsPrincipal user, CancellationToken ct = default)
    {
        message = (message ?? "").Trim();

        // Local helper
        async Task<string> Finish(string ans, object? meta = null)
        {
            await AppendLogAsync(message, ans, user, meta, ct).ConfigureAwait(false);
            return ans;
        }

        try
        {
            var strict = _cfg.GetValue<bool>("Chat:StrictScope");
            var offTopicStrategy = (_cfg["Chat:OffTopicStrategy"] ?? "BLOCK").Trim().ToUpperInvariant(); // BLOCK | REDIRECT

            // ====== GATEKEEPER khi StrictScope bật ======
            if (strict)
            {
                if (IsSensitiveOffTopic(message))
                    return await Finish(
                        "Xin lỗi, chatbot HAFood chỉ hỗ trợ tìm món & tra cứu đơn. Bạn cho mình biết tên món hoặc tầm giá nhé.",
                        new { gate = "sensitive_offtopic" }
                    ).ConfigureAwait(false);

                if (IsGeneralKnowledgeOffTopic(message) || IsEitherOrOpinion(message, out _, out _))
                {
                    if (offTopicStrategy == "REDIRECT")
                    {
                        var ans = await QuickOffTopicRedirectAsync(message, ct).ConfigureAwait(false);
                        return await Finish(ans, new { gate = "offtopic_redirect" }).ConfigureAwait(false);
                    }
                    return await Finish(
                        "Câu này nằm ngoài phạm vi hỗ trợ của chatbot HAFood 😊. Mình chỉ hỗ trợ **tìm món & tra cứu đơn**. Bạn cho mình **tên món** hoặc **khoảng giá** (vd: 20k–50k) nhé!",
                        new { gate = "offtopic_block" }
                    ).ConfigureAwait(false);
                }

                // Cho phép “chào/hỏi” nếu không có scope mà vẫn là small-talk
                if (!IsInScope(message) && (IsGreeting(message) || IsSmallTalkOrHelp(message)))
                    return await Finish(PickGreeting(GetUserId(user)),
                        new { gate = "greeting_help_redirect" }).ConfigureAwait(false);

                if (!IsInScope(message))
                {
                    if (offTopicStrategy == "REDIRECT")
                    {
                        var ans = await QuickOffTopicRedirectAsync(message, ct).ConfigureAwait(false);
                        return await Finish(ans, new { gate = "not_in_scope_redirect" }).ConfigureAwait(false);
                    }
                    return await Finish(
                        "Mình chỉ hỗ trợ **tìm món & tra cứu đơn**. Bạn cho mình từ khóa món hoặc tầm giá (vd: 15k–30k) nhé!",
                        new { gate = "not_in_scope_block" }
                    ).ConfigureAwait(false);
                }
            }

            // ====== Các nhánh xử lý chính trong phạm vi ======

            // 0) Câu quá chung nhưng trong domain (vd: “sản phẩm”, “sp”, “món”, “danh mục”)
            if (IsBareInDomain(message))
            {
                var head = "Bạn muốn xem **danh mục** hay **lọc theo tầm giá**?";
                var cats = await AnswerTopCategoriesAsync(includeEmpty: false, take: 10, ct).ConfigureAwait(false);
                var tips = "Ví dụ nhanh: **“khô bò 50k–100k”**, **“trái cây sấy ≤ 50k”**, **“mì ăn liền ≥ 20k”**.";
                var ans = $"{head}\n\n{cats}\n\n{tips}";
                return await Finish(ans, new { intent = "bare_in_domain" }).ConfigureAwait(false);
            }

            // A.2) “xem món / chi tiết / items …”
            if (LooksLikeOrderItemsQuery(message, out var oc1))
            {
                var ans = await AnswerOrderItemsAsync(oc1, ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "order_items", order_code = oc1 }).ConfigureAwait(false);
            }

            // A) có mã đơn -> tra theo mã 
            if (LooksLikeOrderCode(message))
            {
                var ans = await AnswerOrderFromDbAsync(message, GetUserId(user), ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "order_by_code" }).ConfigureAwait(false);
            }

            // A.1) “đơn gần nhất”
            if (LooksLikeLatestOrderQuery(message))
            {
                var ans = await AnswerLatestOrderFromDbAsync(GetUserId(user), ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "latest_order" }).ConfigureAwait(false);
            }

            if (TryExtractSubcategoryQuery(message, out var parentQ))
            {
                var ans = await AnswerChildCategoriesAsync(parentQ, includeEmpty: false, take: 20, ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "categories_children", parent = parentQ }).ConfigureAwait(false);
            }

            // ====== Danh mục (top-level / all) ======
            if (IsAllCategoriesQuery(message))
            {
                var ans = await AnswerAllCategoriesAsync(includeEmpty: false, ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "categories_all" }).ConfigureAwait(false);
            }

            if (IsCategoryListingQuery(message))
            {
                var ans = await AnswerTopCategoriesAsync(includeEmpty: false, take: 20, ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "categories_top" }).ConfigureAwait(false);
            }

            // ====== Sản phẩm theo intent ======
            if (IsLatestProductsQuery(message))
            {
                var ans = await AnswerProductFromDbAsync(message, forceSortNewest: true, ct: ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "products_latest" }).ConfigureAwait(false);
            }

            if (IsCheapestProductsQuery(message))
            {
                var ans = await AnswerProductFromDbAsync(message, sortOverride: 2, ct: ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "products_cheapest" }).ConfigureAwait(false);
            }

            if (IsMostExpensiveProductsQuery(message))
            {
                var ans = await AnswerProductFromDbAsync(message, sortOverride: 3, ct: ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "products_expensive" }).ConfigureAwait(false);
            }

            // “có ... không?”
            if (IsYesNoAvailability(message))
            {
                var ans = await AnswerProductFromDbAsync(message, yesNoAvailability: true, ct: ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "availability_yesno" }).ConfigureAwait(false);
            }

            // B) tìm sản phẩm tự do
            if (IsProductSearch(message))
            {
                var ans = await AnswerProductFromDbAsync(message, ct: ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "product_search" }).ConfigureAwait(false);
            }

            // C) FAQ / Identity
            if (MatchesFaq(message, out var faq))
                return await Finish(faq, new { intent = "faq" }).ConfigureAwait(false);

            if (IsIdentityQuery(message))
                return await Finish(IdentityAnswer, new { intent = "identity" }).ConfigureAwait(false);

            // D) Fallback LLM
            {
                var ans = await AnswerByLLMAsync(message, ct).ConfigureAwait(false);
                return await Finish(ans, new { intent = "llm_fallback" }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await AppendErrorAsync(ex, message, user, "AskAsync", ct: ct).ConfigureAwait(false);
            return "Xin lỗi, hệ thống đang bận. Bạn thử lại giúp mình một chút nhé!";
        }
    }

    // =====================================================================
    // A) TRA CỨU ĐƠN
    // =====================================================================

    private async Task<string> AnswerOrderFromDbAsync(string msg, long? userId, CancellationToken ct = default)
    {
        try
        {
            var code = ExtractOrderCode(msg);
            if (string.IsNullOrWhiteSpace(code))
                return "Bạn cung cấp giúp mình mã đơn (ví dụ: HA123456 hoặc 251101000003).";

            using var con = new SqlConnection(_conn);

            var rows = await con.QueryAsync(
                "dbo.usp_chat_get_order_by_code",
                new { orderCode = code, userId = (object?)userId ?? DBNull.Value },
                commandType: CommandType.StoredProcedure,
                commandTimeout: 10
            ).ConfigureAwait(false);

            var o = rows.FirstOrDefault();
            if (o is null) return $"Không tìm thấy đơn {code}.";

            var statusText =
                (o.status_text as string) ??
                (((int?)o.status) switch
                {
                    0 => "mới tạo",
                    1 => "đã xác nhận",
                    2 => "đang giao",
                    3 => "đã giao",
                    4 => "đã hủy",
                    _ => $"trạng thái #{o.status}"
                });

            var total = (decimal)(o.pay_total ?? 0m);
            var eta = (o.ETA as DateTime?) ?? ((DateTime?)o.created_at)?.AddDays(2);

            return _tplOrder.Render(new
            {
                code = (o.order_code as string) ?? code,
                status = statusText,
                eta = eta?.ToString("dd/MM/yyyy") ?? "N/A",
                pay_total = string.Format(Vi, "{0:#,0} đ", total)
            });
        }
        catch (Exception ex)
        {
            await AppendErrorAsync(ex, msg, null, "AnswerOrderFromDbAsync", ct: ct).ConfigureAwait(false);
            return "Xin lỗi, chưa tra cứu được đơn này.";
        }
    }

    private async Task<string> AnswerLatestOrderFromDbAsync(long? userId, CancellationToken ct = default)
    {
        try
        {
            if (userId is null)
                return "Bạn cần đăng nhập (JWT) để xem **đơn hàng gần nhất của chính bạn** hoặc cung cấp mã đơn.";

            using var con = new SqlConnection(_conn);
            var row = await con.QueryFirstOrDefaultAsync(
                "dbo.usp_chat_get_latest_order_by_user",
                new { userId },
                commandType: CommandType.StoredProcedure,
                commandTimeout: 10
            ).ConfigureAwait(false);

            if (row is null) return "Bạn chưa có đơn hàng nào.";

            var statusText =
                (row.status_text as string) ??
                (((int?)row.status) switch
                {
                    0 => "mới tạo",
                    1 => "đã xác nhận",
                    2 => "đang giao",
                    3 => "đã giao",
                    4 => "đã hủy",
                    _ => $"trạng thái #{row.status}"
                });

            var total = (decimal)(row.pay_total ?? 0m);
            var created = (DateTime?)row.created_at;

            return $"Đơn gần nhất của bạn là **{(row.order_code as string) ?? "N/A"}** (tạo lúc {created:dd/MM/yyyy HH:mm}), trạng thái **{statusText}**, tổng **{total.ToString("#,0 đ", Vi)}**.";
        }
        catch (Exception ex)
        {
            await AppendErrorAsync(ex, "latest_order", null, "AnswerLatestOrderFromDbAsync", ct: ct).ConfigureAwait(false);
            return "Xin lỗi, chưa lấy được đơn gần nhất.";
        }
    }

    private async Task<string> AnswerOrderItemsAsync(string orderCode, CancellationToken ct = default)
    {
        try
        {
            using var con = new SqlConnection(_conn);
            var items = (await con.QueryAsync(
                "dbo.usp_chat_get_order_items_brief",
                new { orderCode },
                commandType: CommandType.StoredProcedure,
                commandTimeout: 10
            ).ConfigureAwait(false)).ToList();

            if (items.Count == 0)
                return $"Không tìm thấy chi tiết cho đơn **{orderCode}**.";

            var lines = new List<string>();
            int i = 0;
            foreach (var it in items)
            {
                var sku = it.sku as string ?? "";
                var name = it.name_variant as string ?? sku;
                var qty = (int)(it.quantity ?? 0);
                var line = (decimal)(it.line_subtotal ?? 0m);
                lines.Add($"{++i}) {name} (SKU {sku}) ×{qty} — {line.ToString("#,0 đ", Vi)}");
                if (i >= 8) break;
            }

            return $"Món trong đơn **{orderCode}**:\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            await AppendErrorAsync(ex, orderCode, null, "AnswerOrderItemsAsync", ct: ct).ConfigureAwait(false);
            return "Xin lỗi, chưa lấy được chi tiết món trong đơn.";
        }
    }

    private async Task<string> AnswerAllCategoriesAsync(bool includeEmpty = false, CancellationToken ct = default)
    {
        try
        {
            using var con = new SqlConnection(_conn);
            var rows = (await con.QueryAsync<(long id, long? parent_id, string name, string category_code, string image_url, int depth, long direct_cnt, long total_cnt)>(
                "dbo.usp_chat_list_categories_all",
                new { include_empty = includeEmpty ? 1 : 0 },
                commandType: CommandType.StoredProcedure,
                commandTimeout: 10
            ).ConfigureAwait(false)).ToList();

            if (rows.Count == 0) return "Hiện chưa có danh mục để hiển thị.";

            var sb = new StringBuilder("Tất cả danh mục:\n");
            foreach (var r in rows)
            {
                var indent = new string(' ', Math.Clamp(r.depth, 0, 6) * 2);
                var code = string.IsNullOrWhiteSpace(r.category_code) ? "" : $" — {r.category_code}";
                var cnt = $" [{r.direct_cnt}/{r.total_cnt}]";
                sb.AppendLine($"{indent}- {r.name}{code}{cnt}");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            await AppendErrorAsync(ex, "categories_all", null, "AnswerAllCategoriesAsync", ct: ct).ConfigureAwait(false);
            return "Xin lỗi, chưa lấy được danh mục.";
        }
    }

    private async Task<string> AnswerChildCategoriesAsync(string parentQuery, bool includeEmpty = false, int take = 20, CancellationToken ct = default)
    {
        try
        {
            using var con = new SqlConnection(_conn);
            var rows = (await con.QueryAsync<(string parent_name, string parent_code, long id, string name, string category_code, string image_url, long product_cnt)>(
                "dbo.usp_chat_list_categories_children_of",
                new { q = parentQuery, limit = Math.Clamp(take, 3, 50), include_empty = includeEmpty ? 1 : 0 },
                commandType: CommandType.StoredProcedure,
                commandTimeout: 10
            ).ConfigureAwait(false)).ToList();

            if (rows.Count == 0)
                return $"Không tìm thấy **danh mục con** của “{parentQuery}”. Bạn thử nhập **tên** hoặc **mã danh mục** chính xác hơn nhé.";

            var parentName = rows.First().parent_name;
            var parentCode = rows.First().parent_code;

            var head = string.IsNullOrWhiteSpace(parentName)
                ? $"Danh mục con của “{parentQuery}”:"
                : $"Danh mục con của **{parentName}**{(string.IsNullOrWhiteSpace(parentCode) ? "" : $" (`{parentCode}`)")}:";

            var lines = rows.Select(r =>
            {
                var countText = r.product_cnt > 0 ? $" ({r.product_cnt} món)" : "";
                var codeText = string.IsNullOrWhiteSpace(r.category_code) ? "" : $" — {r.category_code}";
                return $"- {r.name}{countText}{codeText}";
            });

            return head + "\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            await AppendErrorAsync(ex, parentQuery, null, "AnswerChildCategoriesAsync", ct: ct).ConfigureAwait(false);
            return "Xin lỗi, chưa lấy được danh mục con.";
        }
    }

    // =====================================================================
    // B) TÌM SẢN PHẨM
    // =====================================================================

    private sealed record ProductRow(
        long product_id,
        string product_code,
        string product_name,
        string brand_name,
        string image_product,
        decimal? price,
        decimal? price_sale
    );

    private async Task<string> AnswerTopCategoriesAsync(bool includeEmpty = false, int take = 20, CancellationToken ct = default)
    {
        try
        {
            using var con = new SqlConnection(_conn);
            var rows = (await con.QueryAsync<(long id, string name, string category_code, string image_url, long product_cnt)>(
                "dbo.usp_chat_list_categories_top",
                new { limit = Math.Clamp(take, 3, 50), include_empty = includeEmpty ? 1 : 0 },
                commandType: CommandType.StoredProcedure,
                commandTimeout: 10
            ).ConfigureAwait(false)).ToList();

            if (rows.Count == 0)
                return "Hiện chưa có danh mục phù hợp để hiển thị.";

            var head = "Các danh mục chính:";
            var lines = rows.Select(r =>
            {
                var cnt = r.product_cnt > 0 ? $" ({r.product_cnt} món)" : "";
                var code = string.IsNullOrWhiteSpace(r.category_code) ? "" : $" — {r.category_code}";
                return $"- {r.name}{cnt}{code}";
            });

            return head + "\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            await AppendErrorAsync(ex, "categories_top", null, "AnswerTopCategoriesAsync", ct: ct).ConfigureAwait(false);
            return "Xin lỗi, chưa lấy được danh mục.";
        }
    }

    private async Task<string> AnswerProductFromDbAsync(
        string originalMsg,
        bool forceSortNewest = false,
        int? sortOverride = null,   // 2=asc, 3=desc
        bool yesNoAvailability = false,
        CancellationToken ct = default
    )
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var genericList = IsGenericListIntent(originalMsg);

            // Guard prompty-like câu nhắc
            if (LooksLikePrompty(originalMsg))
                return "Bạn nhập giúp mình **tên món** hoặc **tầm giá** (vd: 20k–50k) để mình lọc nhanh nhé.";

            var (minP, maxP) = ParsePriceRange(originalMsg);

            string? q = null;
            if (!IsPriceOnlyQuery(originalMsg) && !LooksLikePrompty(originalMsg))
            {
                q = ExtractMeaningfulQuery(originalMsg);

                static bool IsGenericQuery(string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return true;
                    var plain = RemoveDiacritics(s.Trim().ToLowerInvariant());
                    var genericSingles = new HashSet<string> { "san", "pham", "hang", "cac", "do", "cho", "loc", "gia", "mon" };
                    if (plain.Length <= 2) return true;
                    if (genericSingles.Contains(plain)) return true;
                    return false;
                }
                if (string.IsNullOrWhiteSpace(q) || IsGenericQuery(q)) q = null;
            }

            using var con = new SqlConnection(_conn);

            int? sort = sortOverride ?? (forceSortNewest ? 1 : (int?)null);
            if (genericList && sort is null) sort = 1;
            if ((minP.HasValue || maxP.HasValue) && sort is null) sort = 2;

            bool showDebug = _cfg.GetValue<bool>("Chat:ShowDebug");
            string dbg = showDebug ? $"[q={(q ?? "∅")} min={(minP?.ToString("#,0", Vi) ?? "∅")} max={(maxP?.ToString("#,0", Vi) ?? "∅")} sort={(sort?.ToString() ?? "∅")}] " : string.Empty;

            int lim = yesNoAvailability ? 3 : 8;
            int fetch = Math.Max(lim * 2, 12);

            var rows = (await con.QueryAsync<ProductRow>(
                "dbo.usp_chat_search_products",
                new { q, limit = fetch, sort, min_price = minP, max_price = maxP },
                commandType: CommandType.StoredProcedure,
                commandTimeout: 10
            ).ConfigureAwait(false)).ToList();

            // Khử trùng lặp theo product_id
            rows = rows
                .GroupBy(r => r.product_id)
                .Select(g => g.First())
                .ToList();

            // Ẩn “Liên hệ” tuỳ ngữ cảnh
            bool hideContact = ShouldHideContactPrice(yesNoAvailability, sortOverride, minP, maxP)
                               || genericList
                               || (!minP.HasValue && !maxP.HasValue && sort is null && q is null);

            if (hideContact)
                rows = rows.Where(r => EffectivePrice(r.price, r.price_sale) > 0).ToList();

            // Ưu tiên nhẹ theo domain
            rows = rows
                .OrderByDescending(r =>
                {
                    var name = RemoveDiacritics((r.product_name ?? "").ToLowerInvariant());
                    return Regex.IsMatch(name, @"\b(kho|say|mut|hat|mi|gia vi|hanh phi|ot say|rong bien|yen mach|granola)\b", RX) ? 1 : 0;
                })
                .ThenBy(r => EffectivePrice(r.price, r.price_sale) == 0 ? 1 : 0)
                .Take(lim)
                .ToList();

            // Trường hợp yes/no “có ... không?”
            if (yesNoAvailability)
            {
                if (rows.Count == 0)
                {
                    int fbSort = sort ?? ((minP.HasValue || maxP.HasValue) ? 2 : 1);
                    var fallback = (await con.QueryAsync<ProductRow>(
                        "dbo.usp_chat_search_products",
                        new { q = (string?)null, limit = fetch, sort = fbSort, min_price = (decimal?)null, max_price = (decimal?)null },
                        commandType: CommandType.StoredProcedure,
                        commandTimeout: 10
                    ).ConfigureAwait(false)).ToList();

                    fallback = fallback
                        .GroupBy(r => r.product_id)
                        .Select(g => g.First())
                        .Where(r => EffectivePrice(r.price, r.price_sale) > 0)
                        .Take(lim)
                        .ToList();

                    if (fallback.Count == 0)
                    {
                        int fbSort2 = sort ?? 2; // ưu tiên rẻ nhất
                        var fbAll = (await con.QueryAsync<ProductRow>(
                            "dbo.usp_chat_search_products",
                            new { q = (string?)null, limit = fetch, sort = fbSort2, min_price = (decimal?)null, max_price = (decimal?)null },
                            commandType: CommandType.StoredProcedure,
                            commandTimeout: 10
                        ).ConfigureAwait(false)).ToList();

                        fbAll = fbAll
                            .GroupBy(r => r.product_id)
                            .Select(g => g.First())
                            .OrderBy(r => EffectivePrice(r.price, r.price_sale) == 0 ? 1 : 0)
                            .ThenBy(r => EffectivePrice(r.price, r.price_sale))
                            .Take(lim)
                            .ToList();

                        if (fbAll.Count == 0)
                            return dbg + "Hiện chưa có sản phẩm phù hợp để gợi ý.";

                        var headLastResort = "Chưa có món có giá niêm yết. Dưới đây là vài gợi ý (có thể cần liên hệ giá):";
                        return dbg + headLastResort + "\n" + string.Join("\n", FormatProductLines(fbAll));
                    }

                    var headFb = (minP.HasValue || maxP.HasValue)
                        ? "Không tìm thấy đúng tầm giá. Dưới đây là vài gợi ý gần nhất:"
                        : (fbSort == 2 ? "Sản phẩm rẻ nhất:" : fbSort == 3 ? "Sản phẩm đắt nhất:" : "Sản phẩm mới nhất:");
                    return dbg + headFb + "\n" + string.Join("\n", FormatProductLines(fallback));
                }

                var headYN = q is null ? "Shop **có** sản phẩm bạn cần. Một vài gợi ý:"
                                       : $"**Có**, shop đang có “{q}”. Một vài gợi ý gần nhất:";
                return dbg + headYN + "\n" + string.Join("\n", FormatProductLines(rows));
            }

            // Không có kết quả → fallback
            if (rows.Count == 0)
            {
                if (genericList && !minP.HasValue && !maxP.HasValue)
                    return "Danh sách hiện có nhiều món chưa niêm yết giá. Bạn chọn **tầm giá** (vd: 20k–50k) để mình lọc nhanh nhé.";

                int fbSort = sort ?? ((minP.HasValue || maxP.HasValue) ? 2 : 1);
                var fallback = (await con.QueryAsync<ProductRow>(
                    "dbo.usp_chat_search_products",
                    new { q = (string?)null, limit = Math.Max(lim * 2, 12), sort = fbSort, min_price = (decimal?)null, max_price = (decimal?)null },
                    commandType: CommandType.StoredProcedure,
                    commandTimeout: 10
                ).ConfigureAwait(false)).ToList();

                fallback = fallback
                    .GroupBy(r => r.product_id)
                    .Select(g => g.First())
                    .Where(r => !hideContact || EffectivePrice(r.price, r.price_sale) > 0)
                    .Take(lim)
                    .ToList();

                if (fallback.Count == 0) return dbg + "Hiện chưa có sản phẩm phù hợp để gợi ý.";

                string HeadRange2(decimal? a, decimal? b)
                {
                    string fmt(decimal v) => string.Format(Vi, "{0:#,0} đ", v);
                    return (a, b) switch
                    {
                        (not null, not null) => $"Không tìm thấy món trong tầm giá {fmt(a.Value)}–{fmt(b.Value)}.",
                        (not null, null) => $"Không tìm thấy món ≥ {fmt(a.Value)}.",
                        (null, not null) => $"Không tìm thấy món ≤ {fmt(b.Value)}.",
                        _ => ""
                    };
                }

                var headFb2 =
                    (minP.HasValue || maxP.HasValue) ? HeadRange2(minP, maxP) + "\nDưới đây là vài gợi ý rẻ nhất:"
                  : fbSort == 2 ? "Sản phẩm rẻ nhất:"
                  : fbSort == 3 ? "Sản phẩm đắt nhất:"
                  : (forceSortNewest || q is null) ? "Sản phẩm mới nhất:"
                  : "Mình chưa tìm thấy đúng ý bạn. Dưới đây là vài gợi ý mới nhất:";

                return dbg + headFb2 + "\n" + string.Join("\n", FormatProductLines(fallback));
            }

            // Có kết quả
            string head;
            if (minP.HasValue || maxP.HasValue)
            {
                string fmt(decimal v) => string.Format(Vi, "{0:#,0} đ", v);
                var rangeText = (minP, maxP) switch
                {
                    (not null, not null) => $"{fmt(minP.Value)}–{fmt(maxP.Value)}",
                    (not null, null) => $"≥ {fmt(minP.Value)}",
                    (null, not null) => $"≤ {fmt(maxP.Value)}",
                    _ => ""
                };
                head = $"Các món {rangeText}:";
            }
            else if (sort == 2) head = "Sản phẩm rẻ nhất:";
            else if (sort == 3) head = "Sản phẩm đắt nhất:";
            else if (genericList) head = "Danh sách sản phẩm (mới nhất):";
            else if (forceSortNewest || q is null) head = "Sản phẩm mới nhất:";
            else head = $"Kết quả cho “{q}”:";

            var ansText = head + "\n" + string.Join("\n", FormatProductLines(rows));
            sw.Stop();
            return ansText;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await AppendErrorAsync(ex, originalMsg, null, "AnswerProductFromDbAsync", new { elapsed_ms = sw.ElapsedMilliseconds }, ct).ConfigureAwait(false);
            return "Xin lỗi, chưa tìm được danh sách sản phẩm phù hợp.";
        }
    }

    private static IEnumerable<string> FormatProductLines(IEnumerable<ProductRow> rows)
    {
        int i = 0;
        foreach (var r in rows)
        {
            var skuPart = string.IsNullOrWhiteSpace(r.product_code) ? "" : $" (SKU {r.product_code})";
            yield return $"{++i}. {r.product_name}{skuPart} — {FormatPrice(r.price, r.price_sale)}";
        }
    }

    // =====================================================================
    // C) FAQ
    // =====================================================================

    private static readonly (string key, string answer)[] FAQ = new[]
    {
        ("đổi trả", "Bạn có thể đổi trả trong 7 ngày nếu sản phẩm lỗi do nhà sản xuất, vui lòng giữ hoá đơn."),
        ("giờ mở cửa", "Shop mở cửa 8:00–21:00 mỗi ngày."),
        ("phí ship", "Nội thành từ 15k–25k, miễn phí đơn từ 300k.")
    };

    private static bool MatchesFaq(string msg, out string answer)
    {
        var m = (msg ?? "").ToLowerInvariant();
        foreach (var (k, a) in FAQ)
            if (m.Contains(k)) { answer = a; return true; }
        answer = ""; return false;
    }

    // =====================================================================
    // D) Fallback LLM (trong phạm vi)
    // =====================================================================

    private async Task<string> AnswerByLLMAsync(string userMsg, CancellationToken ct = default)
    {
        try
        {
            var http = _hf.CreateClient("OpenAI");
            var model = _cfg["OpenAI:Model"];

            if (string.IsNullOrWhiteSpace(model))
                return "Xin lỗi, chưa cấu hình model trả lời tự nhiên.";

            var sys =
                "Bạn là trợ lý CSKH cho HAFood. Trả lời NGẮN GỌN bằng **tiếng Việt có dấu**. " +
                "Tuyệt đối KHÔNG dùng tiếng Anh. " +
                "Không bịa dữ liệu đơn hàng/sản phẩm (những cái đó do DB/FAQ cung cấp). " +
                "Nếu thiếu dữ kiện, hãy hỏi lại đúng trọng tâm. " +
                "Phong cách: thân thiện, súc tích, xưng 'mình'/'bạn'.";

            async Task<string?> CallAsync(string extraUser = null)
            {
                var messages = new List<object>
                {
                    new { role = "system", content = sys },
                    new { role = "user",   content = userMsg }
                };
                if (!string.IsNullOrEmpty(extraUser))
                    messages.Add(new { role = "user", content = extraUser });

                var req = new
                {
                    model,
                    temperature = 0.3,
                    max_tokens = 256,
                    messages = messages.ToArray()
                };

                var resp = await http.PostAsJsonAsync("chat/completions", req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
                if (!json.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    return null;

                var msg = choices[0].GetProperty("message");
                if (!msg.TryGetProperty("content", out var contentEl))
                    return null;

                return contentEl.GetString();
            }

            static bool LooksEnglish(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return true;
                var noDiac = RemoveDiacritics(text);
                var hasVN = Regex.IsMatch(text, "[àáạảãăắằẵặẳâầấẫậẩèéẹẻẽêềếễệểìíịỉĩòóọỏõôồốỗộổơờớỡợởùúụủũưừứữựửỳýỵỷỹđĐ]");
                if (hasVN) return false;
                int enHits = Regex.Matches(noDiac.ToLowerInvariant(), @"\b(hello|hi|how|can|help|the|you|your|please|today)\b", RX).Count;
                return enHits >= 2;
            }

            var content = await CallAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                return "Xin lỗi, máy chủ sinh ngôn ngữ hiện không phản hồi.";

            if (LooksEnglish(content))
            {
                var content2 = await CallAsync("Bạn vừa trả lời bằng tiếng Anh. Hãy trả lời lại **hoàn toàn bằng tiếng Việt có dấu**.").ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content2) && !LooksEnglish(content2))
                    return content2!;
            }

            return content ?? "Xin lỗi, chưa có câu trả lời.";
        }
        catch (Exception ex)
        {
            await AppendErrorAsync(ex, userMsg, null, "AnswerByLLMAsync", ct: ct).ConfigureAwait(false);
            return "Xin lỗi, hiện chưa trả lời được câu này.";
        }
    }

    // =====================================================================
    // D2) QUICK OFF-TOPIC REDIRECT
    // =====================================================================

    private async Task<string> QuickOffTopicRedirectAsync(string userMsg, CancellationToken ct = default)
    {
        try
        {
            var http = _hf.CreateClient("OpenAI");
            var model = _cfg["OpenAI:Model"];
            if (string.IsNullOrWhiteSpace(model))
                return "Câu này ngoài phạm vi hỗ trợ của HAFood. Nếu bạn cần, mình có thể gợi ý món theo tầm giá (vd: 20k–50k) và tra cứu đơn hàng ngay.";

            var sys =
                "Bạn là trợ lý AI cho HAFood. Nếu câu hỏi KHÔNG thuộc phạm vi (món/giá/đơn hàng), " +
                "hãy trả lời NGẮN 1 câu: 'Câu này ngoài phạm vi hỗ trợ của HAFood.' " +
                "Sau đó LUÔN thêm 1 câu điều hướng có ví dụ truy vấn có số (vd: Tìm kiếm sản phẩm từ 20k–50k). " +
                "Tuyệt đối KHÔNG khẳng định hay mô tả kiến thức chung/nhân vật.";

            var messages = new List<object>
            {
                new { role = "system", content = sys },
                new { role = "user",   content = userMsg }
            };

            var req = new
            {
                model,
                temperature = 0.1,
                max_tokens = 120,
                messages = messages.ToArray()
            };

            var resp = await http.PostAsJsonAsync("chat/completions", req, ct).ConfigureAwait(false);
            var fallback = "Câu này ngoài phạm vi hỗ trợ của HAFood. Nếu bạn cần, mình có thể gợi ý món theo tầm giá (vd: khô bò 50k–100k, trái cây sấy ≤ 50k) và tra cứu đơn hàng ngay.";

            if (!resp.IsSuccessStatusCode)
                return fallback;

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
            if (!json.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return fallback;

            var content = choices[0].GetProperty("message").GetProperty("content").GetString() ?? fallback;

            var low = RemoveDiacritics(content).ToLowerInvariant();
            if (Regex.IsMatch(low, @"\b(la|sinh|o|nam|co|thuoc|dien|vien|nghe|si)\b", RX))
                content = fallback;
            if (Regex.IsMatch(content, @"\b(the|is|are|and|or|with)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                content = fallback;

            return content;
        }
        catch (Exception ex)
        {
            await AppendErrorAsync(ex, userMsg, null, "QuickOffTopicRedirectAsync", ct: ct).ConfigureAwait(false);
            return "Câu này ngoài phạm vi hỗ trợ của HAFood. Nếu bạn cần, mình có thể gợi ý món theo tầm giá (vd: 20k–50k) và tra cứu đơn hàng ngay.";
        }
    }

    // =====================================================================
    // GATEKEEPER (WHITELIST) + DETECTORS
    // =====================================================================

    private static decimal EffectivePrice(decimal? price, decimal? sale)
        => (sale.HasValue && sale.Value > 0) ? sale.Value : (price ?? 0m);

    private static bool ShouldHideContactPrice(bool yesNoAvailability, int? sortOverride, decimal? minP, decimal? maxP)
        => yesNoAvailability || sortOverride == 2 || sortOverride == 3 || minP.HasValue || maxP.HasValue;

    private static bool IsInScope(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;

        if (LooksLikeOrderCode(s) || LooksLikeLatestOrderQuery(s) || LooksLikeOrderItemsQuery(s, out _))
            return true;

        if (IsAllCategoriesQuery(s) || IsCategoryListingQuery(s) || IsBareInDomain(s)) return true;
        if (TryExtractSubcategoryQuery(s, out _)) return true;

        if (IsProductSearchStrict(s) || IsCheapestProductsQuery(s) || IsMostExpensiveProductsQuery(s) || IsLatestProductsQuery(s))
            return true;

        if (MatchesFaq(s, out _) || IsIdentityQuery(s))
            return true;

        return false;
    }

    // ===== Word utils =====
    private static readonly Regex WordSplitter = new(@"[^\p{L}\p{N}]+", RX);
    private static string[] TokenizeWords(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        var plain = RemoveDiacritics(s.ToLowerInvariant());
        return WordSplitter.Split(plain).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
    }
    private static bool HasAnyWord(string text, IEnumerable<string> words)
    {
        var toks = new HashSet<string>(TokenizeWords(text));
        foreach (var w in words)
            if (toks.Contains(RemoveDiacritics(w.ToLowerInvariant()))) return true;
        return false;
    }

    private static bool IsGenericListIntent(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var norm = NormalizeForNLP(s);

        bool listy = Regex.IsMatch(norm, @"\b(danh\s*sach|liet\s*ke|tat\s*ca|toan\s*bo|list)\b", RX);
        bool productish = Regex.IsMatch(norm, @"\b(san\s*pham|hang\s*hoa|sp|mon)\b", RX);
        bool storeish = Regex.IsMatch(norm, @"\b(cua\s*hang|shop)\b", RX);

        return listy && (productish || storeish);
    }

    private static bool HasBigram(IReadOnlyList<string> toks, string a, string b)
    {
        for (int i = 0; i + 1 < toks.Count; i++)
            if (toks[i] == a && toks[i + 1] == b) return true;
        return false;
    }

    private static bool HasPhrase(string norm, string phrase)
    {
        var p = RemoveDiacritics(phrase.ToLowerInvariant()).Trim();
        return Regex.IsMatch(norm, $@"\b{Regex.Escape(p)}\b", RX);
    }

    private static string NormalizeForNLP(string s)
    {
        var p = RemoveDiacritics((s ?? "").ToLowerInvariant());
        p = Regex.Replace(p, @"\btra\s+loi\b", " __verb_traloi__ ", RX);
        p = Regex.Replace(p, @"\bhoi\s+dap\b", " __verb_hoidap__ ", RX);
        p = Regex.Replace(p, @"\btra\s+cuu\b", " __verb_tracuu__ ", RX);
        p = Regex.Replace(p, @"\bhoi\s+giup\b", " __verb_hoigiup__ ", RX);
        return p;
    }

    // Alias ngành hàng chính
    private static readonly HashSet<string> ALIAS_WORDS = new(new[]
    {
        "do kho", "kho", "trai cay say", "say gion", "say deo",
        "mut", "mut tet", "banh mut", "banh mut tet",
        "hai san kho", "muc kho", "ca kho", "tom kho", "ca chi",
        "hat", "hat dinh duong", "hat dieu", "hat huong duong", "hat bi",
        "ngu coc", "yen mach", "granola",
        "mi", "mi an lien", "mi goi", "pho kho", "bun kho", "chao an lien",
        "gia vi", "ot say", "toi say", "hanh phi", "rong bien",
        "combo kho", "set qua", "qua tet", "gio qua",
        "mon", "sku", "san pham", "hang hoa", "danh muc"
    }.Select(RemoveDiacritics));

    private static readonly string[] PHRASE_STOPWORDS = new[]
    {
        "tra loi", "hoi dap", "hoi giup", "tu van", "xin loi", "xin chao"
    };

    private static readonly HashSet<string> CHAT_STOPWORDS = new(new[]
    {
        "toi","minh","em","anh","chi","ban","oi","nha","nhe","voi","ad",
        "vui","long","xin","on","lam","cam","giup","co","the","khong","ko","k","duoc",
        "hoi","ve","thong","tin","la","duoc","tra","loi","hoi","dap",
        "gi","bao","nhieu","the","nao","vi","sao","tai","sao"
    }.Select(RemoveDiacritics));

    // Câu cực ngắn nhưng đúng domain (để hiện gợi ý danh mục/tầm giá)
    private static bool IsBareInDomain(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var toks = TokenizeWords(s);
        if (toks.Length == 0 || toks.Length > 3) return false;

        // Nếu chỉ toàn alias/keyword ngành
        if (toks.All(t => ALIAS_WORDS.Contains(t) || t == "sp" || t == "sku"))
            return true;

        // Các mẫu rất ngắn phổ biến
        var joined = string.Join(' ', toks);
        return joined is "sp" or "san pham" or "mon" or "hang" or "danh muc";
    }

    private static bool IsProductSearchStrict(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (Regex.IsMatch(s, @"\b(đơn|order|mã\s*đơn|order\s*code)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        if (LooksLikePrompty(s)) return false;

        if (IsGenericListIntent(s)) return true;

        var norm = NormalizeForNLP(s);
        var toks = TokenizeWords(norm);

        var (minP, maxP) = ParsePriceRange(norm);
        if (minP.HasValue || maxP.HasValue) return true;

        bool hasSanPham = HasBigram(toks, "san", "pham") || HasPhrase(norm, "san pham");
        bool hasHangHoa = HasPhrase(norm, "hang hoa");
        bool hasCatalog = HasPhrase(norm, "catalog") || HasPhrase(norm, "menu");
        bool hasDanhSach = HasPhrase(norm, "danh sach") || HasPhrase(norm, "liet ke") || HasPhrase(norm, "tat ca") || HasPhrase(norm, "toan bo") || HasPhrase(norm, "list");

        bool hasAliasWord = toks.Any(t => ALIAS_WORDS.Contains(t));
        bool hasDomainHard = Regex.IsMatch(norm, @"\b(kho|mut|trai cay say|hai san kho|hat dinh duong|mi an lien|gia vi|hanh phi|ot say|rong bien|yen mach|granola)\b", RX);

        bool hasThing = hasAliasWord || hasSanPham || hasHangHoa || hasDomainHard;

        bool hasIntent = HasAnyWord(norm, new[] { "tim", "kiem", "mua", "xem", "goi y", "loc" })
                         || hasDanhSach || hasCatalog;

        if (hasThing && (hasIntent || hasDanhSach)) return true;

        bool mentionsStore = (toks.Contains("cua") && toks.Contains("hang")) || HasPhrase(norm, "trong shop") || HasPhrase(norm, "trong cua hang");
        if (hasSanPham && mentionsStore) return true;

        bool allStopOrNum = toks.All(t => CHAT_STOPWORDS.Contains(t) || IsPriceToken(t));
        if (!hasThing && allStopOrNum) return false;

        return hasAliasWord;
    }

    private static bool IsGeneralKnowledgeOffTopic(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (IsInScope(s)) return false;

        var t = RemoveDiacritics(s.Trim().ToLowerInvariant());
        bool hasWh = Regex.IsMatch(t, @"\b(ai|cai gi|gi|tai sao|vi sao|o dau|khi nao|bao gio|the nao|nhu the nao|nuoc nao|quoc gia nao|bien nao|song nao|top nao)\b", RX);
        bool hasGenericTopics = Regex.IsMatch(t, @"\b(gdp|giau nhat|ngheo nhat|dan so|lich su|the thao|world cup|ronaldo|messi|khoa hoc|toan hoc|vat ly|cong nghe|dia ly)\b", RX);

        bool hasDomain = Regex.IsMatch(t, @"\b(san pham|sp|mon|gia|don|order|ma don|danh muc|hang|shop|sku)\b", RX)
                         || Regex.IsMatch(t, @"\b(kho|mut|trai cay say|say gion|say deo|hai san kho|hat|hat dinh duong|mi|mi an lien|gia vi|hanh phi|ot say|rong bien|yen mach|granola)\b", RX);

        return (hasWh || hasGenericTopics) && !hasDomain;
    }

    private static bool IsSensitiveOffTopic(string s)
    {
        var t = RemoveDiacritics((s ?? "").ToLowerInvariant());
        return Regex.IsMatch(t, @"\b(chinh tri|bieu tinh|18\+|tinh duc|y te|chan doan|thuoc|tiem|vaccine)\b", RX);
    }

    private static bool IsEitherOrOpinion(string s, out string a, out string b)
    {
        a = b = "";
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = Regex.Match(s, @"(?i)\b(.{1,40}?)\s+hay\s+(.{1,40}?)\b", RegexOptions.CultureInvariant);
        if (!m.Success) return false;

        var plain = RemoveDiacritics(s.ToLowerInvariant());
        var productish = Regex.IsMatch(plain, @"\b(sp|san\s*pham|mon|gia|banh|keo|mut|hat|tra|bot|mua)\b", RX);
        if (productish) return false;

        a = m.Groups[1].Value.Trim(' ', '?', '.', ',', '!', '"', '\'');
        b = m.Groups[2].Value.Trim(' ', '?', '.', ',', '!', '"', '\'');
        return a.Length > 0 && b.Length > 0;
    }

    // Greeting variants + quick suggestions
    private static readonly string[] GREET_VARIANTS = new[]
    {
        "Chào bạn 👋 Mình hỗ trợ *tìm món theo tên/giá* và *tra cứu đơn*. Ví dụ: **“khô bò 50k–100k”** hoặc **“trái cây sấy ≤ 50k”**.\n• Gợi ý: 20k–50k • Đơn gần nhất • Nhập mã đơn",
        "Hello! Bạn có thể gõ **“mứt Tết 100k–200k”** hoặc **mã đơn HA123456** để mình kiểm tra.\n• Gợi ý: 50k–80k • Đơn gần nhất • Nhập mã đơn",
        "Xin chào! Bạn muốn xem **danh mục đồ khô** hay **lọc theo tầm giá**?\n• Gợi ý: 30k–60k • Đơn gần nhất • Nhập mã đơn"
    };

    private static string PickGreeting(long? uid)
    {
        if (uid is null) return GREET_VARIANTS[0];
        var idx = (int)(Math.Abs(uid.Value) % GREET_VARIANTS.Length);
        return GREET_VARIANTS[idx];
    }

    private static bool IsSmallTalkOrHelp(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = RemoveDiacritics(s.Trim().ToLowerInvariant());

        var helpPhrase = Regex.IsMatch(t,
            @"\b(ban\s*co\s*the\s*giup\s*(toi|minh)|co\s*the\s*(giup|tu\s*van|ho\s*tro)|" +
            @"(giup|tu\s*van|ho\s*tro)\s*(toi|minh|duoc\s*khong|voi)?)\b",
            RX);

        if (!helpPhrase) return false;

        var hasProductish = Regex.IsMatch(t, @"\b(sp|san\s*pham|hang|mua|banh|keo|mut|hat|tra|bot|sku|gia|vnd|k|tr)\b", RX);
        return !hasProductish;
    }

    private static readonly Regex AfterKeywordOrderCodeRx =
        new(@"(?i)(m[ãa]\s*đơn|chi\s*tiet|chi\s*tiết|items?|món|order(?:\s*code)?)\D*([A-Z]*\d{6,20})", RX);

    private static readonly Regex OrderCodeGeneralRx =
        new(@"(?i)\b(?:HA)?\d{6,20}\b", RX);

    private static bool LooksLikeOrderCode(string s)
        => !string.IsNullOrWhiteSpace(ExtractOrderCode(s));

    private static string ExtractOrderCode(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        var m1 = AfterKeywordOrderCodeRx.Match(s);
        if (m1.Success && m1.Groups.Count >= 3)
            return m1.Groups[2].Value.Trim().ToUpperInvariant();

        var m2 = OrderCodeGeneralRx.Matches(s);
        if (m2.Count > 0)
            return m2.OrderByDescending(x => x.Value.Length).First().Value.Trim().ToUpperInvariant();

        return "";
    }

    private static bool LooksLikeLatestOrderQuery(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.ToLowerInvariant();
        return Regex.IsMatch(t, @"\b(đơn|order)\b", RX)
            && Regex.IsMatch(RemoveDiacritics(t), @"\b(gan nhat|moi nhat|gan day|vua dat|latest|recent)\b", RX);
    }

    private static bool LooksLikeOrderItemsQuery(string s, out string orderCode)
    {
        orderCode = "";
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.ToLowerInvariant();

        var mentionItems = Regex.IsMatch(t, @"\b(chi\s*tiết|items?|món|trong\s*đơn|xem\s*món)\b", RX);
        if (!mentionItems) return false;

        orderCode = ExtractOrderCode(s);
        return !string.IsNullOrWhiteSpace(orderCode);
    }

    // Prompty-like blacklist
    private static readonly string[] PROMPTY_PREFIXES = new[]
    {
        "ban muon tim kiem san pham nao",
        "ban co muon kiem tra don hang khong",
        "ban co muon minh goi y",
        "hay nhap ten giup minh",
        "hay nhap ma don",
        "vui long nhap"
    };
    private static bool LooksLikePrompty(string s)
    {
        var t = RemoveDiacritics((s ?? "").Trim().ToLowerInvariant());
        foreach (var px in PROMPTY_PREFIXES)
            if (t.StartsWith(px)) return true;
        return Regex.IsMatch(t, @"\b(hay|vui long|nhap|tra loi|hoi dap)\b", RX);
    }

    private static bool IsProductSearch(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (Regex.IsMatch(s, @"\b(đơn|order|mã\s*đơn|order\s*code)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        if (LooksLikePrompty(s)) return false;

        var norm = NormalizeForNLP(s);
        var toks = TokenizeWords(norm);

        var (minP, maxP) = ParsePriceRange(norm);
        if (minP.HasValue || maxP.HasValue) return true;

        if (IsPriceOnlyQuery(norm)) return true;
        if (toks.Contains("mon")) return true;
        if (toks.Contains("sp") || (toks.Contains("san") && toks.Contains("pham"))) return true;
        if (toks.Any(t => ALIAS_WORDS.Contains(t))) return true;

        bool hasIntent = HasAnyWord(norm, new[] { "tim", "kiem", "gia", "mua", "xem", "goi y", "loc" });
        bool hasThing = toks.Any(t => ALIAS_WORDS.Contains(t));
        return hasIntent && hasThing;
    }

    private static string ExtractMeaningfulQuery(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        if (LooksLikePrompty(message)) return string.Empty;

        if (IsGenericListIntent(message))
            return string.Empty;

        var norm = NormalizeForNLP(message);
        var tokens = TokenizeWords(norm).ToList();

        foreach (var ph in PHRASE_STOPWORDS)
        {
            var parts = TokenizeWords(ph);
            if (parts.Length == 2)
            {
                for (int i = 0; i + 1 < tokens.Count;)
                {
                    if (tokens[i] == parts[0] && tokens[i + 1] == parts[1])
                        tokens.RemoveRange(i, 2);
                    else i++;
                }
            }
        }

        var kept = new List<string>();
        foreach (var t in tokens)
        {
            if (t.Length < 2) continue;
            if (CHAT_STOPWORDS.Contains(t)) continue;
            if (IsPriceToken(t)) continue;
            kept.Add(t);
        }
        if (kept.Count == 0) return string.Empty;

        var aliasOnly = kept.Where(k => ALIAS_WORDS.Contains(k)).ToList();
        if (aliasOnly.Count > 0)
            return string.Join(' ', aliasOnly.Take(3));

        return string.Join(' ', kept.Take(5));
    }

    private static string FormatPrice(decimal? price, decimal? sale)
    {
        var eff = EffectivePrice(price, sale);
        if (eff <= 0) return "Liên hệ";
        return string.Format(Vi, "{0:#,0} đ", eff);
    }

    private static long? GetUserId(ClaimsPrincipal? u)
    {
        if (u is null) return null;
        var uid = u.FindFirst("uid")?.Value ?? u.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(uid, out var id) ? id : (long?)null;
    }

    private static bool IsGreeting(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = RemoveDiacritics(s.Trim().ToLowerInvariant());
        return Regex.IsMatch(t, @"\b(xin\s*chao|chao|hi|hello|alo|ban\s*oi|ad\s*oi|chao\s*ban|chao\s*ad)\b", RX);
    }

    private static bool IsIdentityQuery(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.ToLowerInvariant();
        return Regex.IsMatch(t, @"\b(bạn là ai|ban la ai|cậu là ai|gioi thieu|giới thiệu|bạn làm gì|ban lam gi)\b", RX)
            || Regex.IsMatch(t, @"\b(who\s+are\s+you|what\s+are\s+you)\b", RX);
    }

    private static readonly string IdentityAnswer =
        "Tôi là trợ lý CSKH cho HAShop. Tôi hỗ trợ tra cứu đơn, tìm sản phẩm và giải đáp thắc mắc. Bạn cần gì hôm nay?";

    private static bool IsAllCategoriesQuery(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = RemoveDiacritics(s.Trim().ToLowerInvariant());
        return Regex.IsMatch(t, @"\b(tat\s*ca\s*danh\s*muc|toan\s*bo\s*danh\s*muc|all\s*categories)\b", RX);
    }

    private static bool TryExtractSubcategoryQuery(string s, out string parentQ)
    {
        parentQ = "";
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = RemoveDiacritics(s.Trim().ToLowerInvariant());

        var patterns = new[]
        {
            @"danh\s*muc\s*con\s*(?:cua|thuoc)\s+(.+)",
            @"nhom\s*con\s*(?:cua|thuoc)\s+(.+)",
            @"subcategory\s*of\s+(.+)",
            @"sub\s*category\s*of\s+(.+)",
            @"children\s*of\s+(.+)"
        };

        foreach (var p in patterns)
        {
            var m = Regex.Match(t, p, RX);
            if (m.Success && m.Groups.Count >= 2)
            {
                parentQ = m.Groups[1].Value.Trim();
                parentQ = Regex.Replace(parentQ, @"[?!.]+$", "", RX).Trim();
                return parentQ.Length >= 2;
            }
        }

        var m2 = Regex.Match(t, @"\bcon\s*(?:cua|thuoc)\s+(.+)", RX);
        if (m2.Success)
        {
            parentQ = Regex.Replace(m2.Groups[1].Value.Trim(), @"[?!.]+$", "", RX);
            return parentQ.Length >= 2;
        }
        return false;
    }

    private static bool IsCategoryListingQuery(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = RemoveDiacritics(s.Trim().ToLowerInvariant());

        if (Regex.IsMatch(t, @"\b(tat\s*ca\s*danh\s*muc|toan\s*bo\s*danh\s*muc|all\s*categories)\b", RX))
            return false;

        if (Regex.IsMatch(t, @"\b(danh\s*muc|danhmuc|loai\s*hang|phan\s*loai|category|categories|nhom\s*hang)\b", RX))
            return true;

        if (Regex.IsMatch(t, @"\b(danh\s*muc\s*nao|nhung\s*danh\s*muc\s*nao|co\s*nhung\s*danh\s*muc\s*nao)\b", RX))
            return true;

        if (t == "danh muc" || t == "danh muc nao" || t == "danh muc?" || t == "loai hang")
            return true;

        return false;
    }

    private static bool IsLatestProductsQuery(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = RemoveDiacritics(s.ToLowerInvariant());
        return Any(t, "sp moi nhat", "san pham moi nhat", "hang moi", "moi nhat", "latest", "newest");
    }

    private static bool IsCheapestProductsQuery(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = RemoveDiacritics(s.ToLowerInvariant());
        return Any(t,
            "re nhat", "re nhat di", "re nhat nha",
            "gia re", "gia thap", "thap nhat",
            "min price", "cheapest",
            "re nhut", "re nhut di", "re nhut nha",
            "re nhat nhe", "re nhat voi", "re nhat ha"
        );
    }

    private static bool IsMostExpensiveProductsQuery(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = RemoveDiacritics(s.ToLowerInvariant());

        if (Regex.IsMatch(t, @"\b((mac|dat)\s*nhat|gia\s*cao(\s*nhat)?|cao\s*nhat|cao\s*cap|dat\s*do|hang\s*hieu|thuong\s*hieu\s*lon|premium|flagship|luxury|sang\s*chanh|xin(\s*xo)?)\b", RX))
            return true;

        return Any(t,
            "mac nhat", "dat nhat", "gia cao", "cao nhat",
            "cao cap", "dat do", "hang hieu", "thuong hieu lon",
            "premium", "flagship", "luxury", "sang chanh",
            "most expensive", "expensive",
            "sp mac nhat", "sp dat nhat", "gia cao nhat", "hang dat nhat",
            "xin", "xin xo"
        );
    }

    private static bool IsYesNoAvailability(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = RemoveDiacritics(s.Trim().ToLowerInvariant());

        var m = Regex.Match(t, @"\bco\s+(.+?)\s+(khong|ko|\bk)\b", RX);
        if (!m.Success) return false;

        var core = m.Groups[1].Value.Trim();

        if (Regex.IsMatch(core, @"\b(co\s*the\s*)?(giup|tu\s*van|ho\s*tro)\b", RX))
            return false;

        core = Regex.Replace(core, @"\b\d[\d\.,]*(k|nghin|ngan|ng|tr|trieu|m)?\b", " ", RX);
        var stop = new HashSet<string>(new[]{
            "toi","minh","em","anh","chi","ban","oi","nha","nhe","voi",
            "vui","long","xin","on","lam","cam","on","giup","co","the",
            "khong","ko","k","duoc","hoi","ve","thong","tin","tu","den","toi"
        }.Select(RemoveDiacritics));
        var toks = Regex.Split(core, @"[\s,.\?!:;_\-\/\\\|\(\)\[\]\{\}]+", RX)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(RemoveDiacritics)
                        .Select(x => x.ToLowerInvariant())
                        .Where(x => x.Length >= 2 && !stop.Contains(x))
                        .ToList();

        if (toks.Count == 0) return false;
        if (!toks.Any(x => Regex.IsMatch(x, @"[a-z]", RX))) return false;

        return true;
    }

    private static string RemoveDiacritics(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        string formD = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in formD)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        var s = sb.ToString().Normalize(NormalizationForm.FormC);
        s = s.Replace('đ', 'd').Replace('Đ', 'D');
        return s;
    }

    private static bool Any(string text, params string[] keys)
    {
        foreach (var k in keys)
            if (text.Contains(k)) return true;
        return false;
    }

    private static bool TryParseVnd(string token, out decimal vnd)
    {
        vnd = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var s = RemoveDiacritics(token.Trim().ToLowerInvariant());

        var m = Regex.Match(s, @"(?<num>\d+(?:[.,]\d+)?)\s*(?<unit>k|nghin|ngan|ng|tr|trieu|m|trd|trieud|vnd|d)?", RX);
        if (!m.Success) return false;

        if (!decimal.TryParse(
                m.Groups["num"].Value.Replace(",", "."),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var num))
            return false;

        var unit = m.Groups["unit"].Value;

        decimal mul = 1m;
        switch (unit)
        {
            case "k":
            case "nghin":
            case "ngan":
            case "ng":
                mul = 1_000m; break;
            case "tr":
            case "trieu":
            case "m":
            case "trd":
            case "trieud":
                mul = 1_000_000m; break;
            default:
                mul = 1m; break;
        }
        vnd = Math.Round(num * mul, 0);
        return true;
    }

    private static (decimal? minP, decimal? maxP) ParsePriceRange(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var s = RemoveDiacritics(text.ToLowerInvariant());

        var mRange = Regex.Match(s, @"(?:tu\s+)?(?<a>\d[\d\.,]*\s*(?:k|nghin|ngan|tr|trieu|m)?)\s*(?:\-|den|toi|t?->|—|–)\s*(?<b>\d[\d\.,]*\s*(?:k|nghin|ngan|tr|trieu|m)?)", RX);
        if (mRange.Success && TryParseVnd(mRange.Groups["a"].Value, out var a) && TryParseVnd(mRange.Groups["b"].Value, out var b))
        {
            if (a > b) (a, b) = (b, a);
            return (a, b);
        }

        var mMin = Regex.Match(s, @"(?:>=|>\s*=|>|tren|lon\s*hon|cao\s*hon|tu)\s*(?<x>\d[\d\.,]*\s*(?:k|nghin|ngan|tr|trieu|m)?)", RX);
        if (mMin.Success && TryParseVnd(mMin.Groups["x"].Value, out var minOnly))
            return (minOnly, null);

        var mMax = Regex.Match(s, @"(?:<=|<\s*=|<|duoi|nho\s*hon|it\s*hon)\s*(?<y>\d[\d\.,]*\s*(?:k|nghin|ngan|tr|trieu|m)?)", RX);
        if (mMax.Success && TryParseVnd(mMax.Groups["y"].Value, out var maxOnly))
            return (null, maxOnly);

        return (null, null);
    }

    private static bool IsPriceToken(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;
        t = RemoveDiacritics(t.Trim().ToLowerInvariant());
        return Regex.IsMatch(t, @"^\d[\d\.,]*(k|nghin|ngan|tr|trieu|m)?$", RX);
    }

    private static bool IsPriceOnlyQuery(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var plain = RemoveDiacritics(s.ToLowerInvariant());

        var (minP, maxP) = ParsePriceRange(plain);
        if (minP.HasValue || maxP.HasValue) return true;

        var tokens = Regex.Split(plain, @"[\s,.\?!:;_\-\/\\\|\(\)\[\]\{\}]+", RX)
                          .Where(x => !string.IsNullOrWhiteSpace(x))
                          .ToList();
        if (tokens.Count == 0) return false;

        var joins = new HashSet<string> { "tu", "den", "toi", ">=", "<=", "-", "--", "—", "–" };
        return tokens.All(t => IsPriceToken(t) || joins.Contains(t));
    }
}
