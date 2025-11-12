using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System.Text.Json;

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
    // , [FromQuery] string? token = null // nếu bạn muốn kiểm tra token/Encryptor như WebForms
    )
    {
        if (!Request.HasFormContentType)
            return BadRequest(new { code = "NO_FORM", msg = "Content-Type phải là multipart/form-data" });

        var files = Request.Form.Files;
        if (files.Count == 0)
            return BadRequest(new { code = "NO_FILE", msg = "Không có file upload" });

        // Build baseUrl (hỗ trợ behind proxy)
        var proto = Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                    ?? (Request.IsHttps ? "https" : "http");
        var host = Request.Headers["X-Forwarded-Host"].FirstOrDefault()
                    ?? Request.Host.Value;

        var publicBase = _cfg["Storage:PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(publicBase))
            publicBase = $"{proto}://{host}";

        // Thư mục lưu
        var rel = _cfg["Storage:Local:RelativeFolder"] ?? "Gallery";
        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var physicalRoot = Path.Combine(webRoot, rel);
        Directory.CreateDirectory(physicalRoot);

        var results = new List<object>();

        foreach (var f in files)
        {
            if (f.Length == 0) continue;
            if (f.Length > 5L * 1024 * 1024)
                return BadRequest(new { code = "MAX_SIZE", msg = "File vượt quá 5MB" });

            // Tên file an toàn
            var ext = Path.GetExtension(f.FileName);
            var baseName = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Path.GetFileNameWithoutExtension(f.FileName);
            baseName = string.Concat(baseName.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'));

            var fWeb = $"{baseName}_w{size_w}{ext}";
            var fTab = $"{baseName}_t{size_t}{ext}";
            var fPhone = $"{baseName}_p{size_p}{ext}";

            using var img = await Image.LoadAsync(f.OpenReadStream());

            // Resize & save WEB
            await SaveResizedAsync(img, Path.Combine(physicalRoot, fWeb), size_w);

            // Resize & save Tablet
            await SaveResizedAsync(img, Path.Combine(physicalRoot, fTab), size_t);

            // Resize & save Phone
            await SaveResizedAsync(img, Path.Combine(physicalRoot, fPhone), size_p);

            var urlWeb = $"{publicBase}/{rel}/{fWeb}";
            var urlTab = $"{publicBase}/{rel}/{fTab}";
            var urlPhone = $"{publicBase}/{rel}/{fPhone}";

            var info = new
            {
                fileName = fWeb,
                urlWeb,
                urlTablet = urlTab,
                urlPhone,
                sizeWeb = await GetSizeHumanAsync(Path.Combine(physicalRoot, fWeb)),
                sizeTablet = await GetSizeHumanAsync(Path.Combine(physicalRoot, fTab)),
                sizePhone = await GetSizeHumanAsync(Path.Combine(physicalRoot, fPhone)),
                sizeOld = ToSizeHuman(f.Length),
                serverTime = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")
            };

            // TODO: ghi lịch sử vào DB nếu muốn (dùng AppDbContext / Dapper)
            results.Add(info);
        }

        return Ok(new { status = 0, message = "SUCCESS", data = (results.Count == 1 ? results[0] : results) });
    }

    private static async Task SaveResizedAsync(Image img, string outPath, int maxWidth)
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

        var encoder = new JpegEncoder { Quality = 85 };
        await using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        await clone.SaveAsJpegAsync(fs, encoder);
    }

    private static async Task<string> GetSizeHumanAsync(string path)
    {
        var fi = new FileInfo(path);
        await Task.CompletedTask;
        return ToSizeHuman(fi.Length);
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

