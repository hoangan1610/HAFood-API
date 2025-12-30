using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System.Security.Cryptography;

[ApiController]
[Route("files")]
public class FilesController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _cfg;

    public FilesController(IWebHostEnvironment env, IConfiguration cfg)
    {
        _env = env;
        _cfg = cfg;
    }

    [HttpPost("images")]
    [RequestSizeLimit(30L * 1024 * 1024)]
    public async Task<IActionResult> UploadImages(
        [FromQuery] int size_w = 2048,
        [FromQuery] int size_t = 800,
        [FromQuery] int size_p = 300
    )
    {
        // ===== Optional API key =====
        var apiKey = (_cfg["Storage:ApiKey"] ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var got = Request.Headers["X-Api-Key"].FirstOrDefault() ?? "";
            if (!string.Equals(got, apiKey, StringComparison.Ordinal))
                return Unauthorized(new { code = "BAD_API_KEY", msg = "Invalid API key." });
        }

        // ===== Only LOCAL mode for now =====
        var mode = (_cfg["Storage:Mode"] ?? "LOCAL").Trim().ToUpperInvariant();
        if (mode != "LOCAL")
            return BadRequest(new { code = "MODE_NOT_SUPPORTED", msg = "Only Storage:Mode=LOCAL is supported." });

        if (!Request.HasFormContentType)
            return BadRequest(new { code = "NO_FORM", msg = "Content-Type phải là multipart/form-data" });

        var files = Request.Form.Files;
        if (files == null || files.Count == 0)
            return BadRequest(new { code = "NO_FILE", msg = "Không có file upload" });

        // ===== size limit per file =====
        long maxFileBytes = 5L * 1024 * 1024;
        if (long.TryParse(_cfg["Storage:MaxFileBytes"], out var cfgMax) && cfgMax > 0)
            maxFileBytes = cfgMax;

        // ===== base url =====
        var publicBase = (_cfg["Storage:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(publicBase))
        {
            var proto = Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                        ?? (Request.IsHttps ? "https" : "http");
            var host = Request.Headers["X-Forwarded-Host"].FirstOrDefault()
                        ?? Request.Host.Value;
            publicBase = $"{proto}://{host}";
        }

        // ===== folder =====
        var relRoot = (_cfg["Storage:Local:RelativeFolder"] ?? "Gallery").Trim().Trim('/').Trim('\\');
        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");

        var dateFolder = DateTime.UtcNow.ToString("yyyy/MM/dd");
        var relFolder = $"{relRoot}/{dateFolder}".Replace("\\", "/");
        var physicalFolder = Path.Combine(webRoot, relRoot, dateFolder);
        Directory.CreateDirectory(physicalFolder);

        size_w = Clamp(size_w, 200, 6000);
        size_t = Clamp(size_t, 200, 6000);
        size_p = Clamp(size_p, 120, 4000);

        var results = new List<object>();

        foreach (var f in files)
        {
            if (f == null || f.Length == 0) continue;
            if (f.Length > maxFileBytes)
                return BadRequest(new { code = "MAX_SIZE", msg = $"File vượt quá {ToSizeHuman(maxFileBytes)}" });

            var ct = (f.ContentType ?? "").ToLowerInvariant();
            if (!ct.StartsWith("image/"))
                return BadRequest(new { code = "NOT_IMAGE", msg = "Chỉ cho phép upload ảnh." });

            // always output jpg
            var ext = ".jpg";

            var safeBase = MakeSafeFileBaseName(Path.GetFileNameWithoutExtension(f.FileName));
            var uid = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + RandomToken(6);
            var baseName = $"{uid}_{safeBase}".Trim('_');

            var fWeb = $"{baseName}_w{size_w}{ext}";
            var fTab = $"{baseName}_t{size_t}{ext}";
            var fPhone = $"{baseName}_p{size_p}{ext}";

            try
            {
                using var img = await Image.LoadAsync(f.OpenReadStream());
                img.Mutate(x => x.AutoOrient()); // fix xoay iPhone

                await SaveResizedAsJpegAsync(img, Path.Combine(physicalFolder, fWeb), size_w, quality: 85);
                await SaveResizedAsJpegAsync(img, Path.Combine(physicalFolder, fTab), size_t, quality: 85);
                await SaveResizedAsJpegAsync(img, Path.Combine(physicalFolder, fPhone), size_p, quality: 85);

                var urlWeb = $"{publicBase}/{relFolder}/{fWeb}";
                var urlTab = $"{publicBase}/{relFolder}/{fTab}";
                var urlPhone = $"{publicBase}/{relFolder}/{fPhone}";

                results.Add(new
                {
                    fileName = fWeb,
                    urlWeb,
                    urlTablet = urlTab,
                    urlPhone,
                    sizeWeb = ToSizeHuman(new FileInfo(Path.Combine(physicalFolder, fWeb)).Length),
                    sizeTablet = ToSizeHuman(new FileInfo(Path.Combine(physicalFolder, fTab)).Length),
                    sizePhone = ToSizeHuman(new FileInfo(Path.Combine(physicalFolder, fPhone)).Length),
                    sizeOriginal = ToSizeHuman(f.Length),
                    folder = relFolder,
                    serverTime = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")
                });
            }
            catch
            {
                return BadRequest(new { code = "BAD_IMAGE", msg = "File ảnh không hợp lệ hoặc bị lỗi." });
            }
        }

        return Ok(new
        {
            status = 0,
            message = "SUCCESS",
            data = (results.Count == 1 ? results[0] : results)
        });
    }

    private static async Task SaveResizedAsJpegAsync(Image img, string outPath, int maxWidth, int quality)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        using var clone = img.Clone(ctx =>
        {
            if (maxWidth > 0 && img.Width > maxWidth)
            {
                var ratio = (double)maxWidth / img.Width;
                var h = (int)Math.Round(img.Height * ratio);
                ctx.Resize(maxWidth, h);
            }
        });

        var encoder = new JpegEncoder { Quality = quality };
        await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await clone.SaveAsJpegAsync(fs, encoder);
    }

    private static int Clamp(int v, int min, int max) => (v < min) ? min : (v > max) ? max : v;

    private static string MakeSafeFileBaseName(string name)
    {
        name ??= "img";
        name = name.Trim();
        if (name.Length > 50) name = name.Substring(0, 50);

        var sb = new System.Text.StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                sb.Append(ch);
        }

        var s = sb.ToString();
        return string.IsNullOrWhiteSpace(s) ? "img" : s;
    }

    private static string RandomToken(int bytesLen)
    {
        var bytes = RandomNumberGenerator.GetBytes(bytesLen);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ToSizeHuman(long bytes)
    {
        string[] unit = { "Bytes", "KB", "MB", "GB" };
        double size = bytes;
        int i = 0;
        while (size > 900 && i < unit.Length - 1) { size /= 1024; i++; }
        return Math.Round(size * 100) / 100 + unit[i];
    }
}
