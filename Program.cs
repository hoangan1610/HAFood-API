using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;
using HAShop.Api.Data;
using HAShop.Api.Services;
using System.Data;
using Microsoft.Extensions.Options;
using DotNetEnv;

var builder = WebApplication.CreateBuilder(args);

// =========================================================
// ✅ 1️⃣ Load biến môi trường (.env) – dùng khi dev local
// =========================================================
Env.Load();

// =========================================================
// ✅ 2️⃣ Cấu hình SendGrid (API key lấy từ biến môi trường)
// =========================================================
builder.Services.Configure<SendGridOptions>(opt =>
{
    // 🔒 API key đọc từ environment (hoặc file .env)
    opt.ApiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY") ?? "";

    // Các thông tin khác lấy từ appsettings.json
    opt.FromEmail = builder.Configuration["SendGrid:FromEmail"] ?? "";
    opt.FromName = builder.Configuration["SendGrid:FromName"] ?? "";
    opt.TemplateId = builder.Configuration["SendGrid:TemplateId"] ?? "";

    // Nếu có thêm cấu hình phụ:
    if (int.TryParse(builder.Configuration["SendGrid:BatchSize"], out var batch))
        opt.BatchSize = batch;

    if (int.TryParse(builder.Configuration["SendGrid:PollIntervalSeconds"], out var poll))
        opt.PollIntervalSeconds = poll;
});

// =========================================================
// ✅ 3️⃣ Đăng ký service SendGrid + Email Queue
// =========================================================
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IEmailQueueRepository, EmailQueueRepository>();
builder.Services.AddSingleton<ISendGridSender, SendGridSender>();
builder.Services.AddHostedService<EmailQueueWorker>();

// =========================================================
// ✅ 4️⃣ Cấu hình DATABASE
// =========================================================
var conn = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(conn))
{
    builder.Services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase("dev"));
    builder.Services.AddTransient<ISqlConnectionFactory>(_ => new NullSqlConnectionFactory());
}
else
{
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseSqlServer(conn, o => o.EnableRetryOnFailure()));
    builder.Services.AddTransient<ISqlConnectionFactory, SqlConnectionFactory>();
}

// =========================================================
// ✅ 5️⃣ Controller + Swagger + CORS + DI
// =========================================================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "HAShop API", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT", // hoặc "GUID" nếu bạn chỉ dùng token GUID
        In = ParameterLocation.Header,
        Description = "Nhập token theo định dạng: Bearer {token}"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("PublicCors", policy =>
        policy.WithOrigins(
                "http://localhost:3000",
                "https://hafood.id.vn",
                "https://hafood-mock-api.onrender.com"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
    );
});
//====================================================


builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IDeviceService, DeviceService>();


var app = builder.Build();

// =========================================================
// ✅ 6️⃣ Middleware pipeline
// =========================================================
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
});

app.UseHttpsRedirection();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "HAShop API v1");
    c.RoutePrefix = "swagger";
});

app.UseCors("PublicCors");
app.UseAuthorization();
app.MapControllers();

// =========================================================
// ✅ 7️⃣ Route test SendGrid
// =========================================================
app.MapGet("/test-email", async (ISendGridSender mailer) =>
{
    var ok = await mailer.SendTemplateEmailAsync(
        "ngohoangan0@gmail.com",
        "Anh Hoàng",
        "123456",
        10
    );
    return ok
        ? Results.Ok("✅ Email đã gửi thành công!")
        : Results.BadRequest("❌ Gửi email thất bại!");
}).WithDescription("Gửi email test qua SendGrid (Dynamic Template)");

app.MapGet("/test-email-verbose", async (ISendGridSender mailer, IOptions<SendGridOptions> opt, HttpRequest req, CancellationToken ct) =>
{
    var to = req.Query["to"].FirstOrDefault() ?? "your-test-email@example.com";
    try
    {
        await mailer.SendTemplateAsync(
            to,
            opt.Value.TemplateId,
            new { NAME = "Test User", OTP = "123456", TTL_MIN = 10, Sender_Name = "HAFood" },
            ct
        );
        return Results.Ok(new { ok = true, note = "SendGrid accepted (202). Check inbox." });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

// Redirect root → Swagger
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// Dev health check
app.MapGet("/healthz", () => Results.Ok(new
{
    db = string.IsNullOrWhiteSpace(conn) ? "InMemory" : "SqlServer",
    ok = true
}));

// =========================================================
// ✅ 8️⃣ Chạy app
// =========================================================
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");
app.Run();

// =========================================================
// ✅ 9️⃣ Class phụ trợ
// =========================================================
public class NullSqlConnectionFactory : ISqlConnectionFactory
{
    public IDbConnection Create() =>
        throw new InvalidOperationException("Database is not configured. Set ConnectionStrings:Default to enable SQL Server.");
}
