using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;
using HAShop.Api.Data;
using HAShop.Api.Services;
using System.Data;
using Microsoft.Extensions.Options;


var builder = WebApplication.CreateBuilder(args);


builder.Services.Configure<SendGridOptions>(builder.Configuration.GetSection("SendGrid"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IEmailQueueRepository, EmailQueueRepository>();
builder.Services.AddSingleton<ISendGridSender, SendGridSender>();
builder.Services.AddHostedService<EmailQueueWorker>();

// ========== DATABASE ==========
var conn = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(conn))
{
    // Chưa có DB → chạy tạm InMemory để mở Swagger/route
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseInMemoryDatabase("dev"));

    // Nếu code nào lỡ cần ISqlConnectionFactory, ném lỗi rõ ràng
    builder.Services.AddTransient<ISqlConnectionFactory>(_ => new NullSqlConnectionFactory());
}
else
{
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseSqlServer(conn, o => o.EnableRetryOnFailure()));
    builder.Services.AddTransient<ISqlConnectionFactory, SqlConnectionFactory>();

}

// ========== CONTROLLERS + SWAGGER ==========
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "HAShop API", Version = "v1" });
});

// ========== CORS ==========
builder.Services.AddCors(options =>
{
    options.AddPolicy("PublicCors", policy =>
        policy.WithOrigins(
                "http://localhost:3000",
                "https://hafood.id.vn",
                "https://hafood-mock-api.onrender.com" // không có dấu "/" cuối
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
    );
});

// ========== DI ==========
builder.Services.AddScoped<IAuthService, AuthService>();

var app = builder.Build();

// (Tuỳ chọn) nhận biết HTTPS/host từ reverse proxy của App Service
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
});

// (Tuỳ chọn) ép HTTPS
app.UseHttpsRedirection();

// ========== SWAGGER ==========
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "HAShop API v1");
    c.RoutePrefix = "swagger";
});

// ========== PIPELINE ==========
app.UseCors("PublicCors");
app.UseAuthorization();
app.MapControllers();

app.MapGet("/test-email-verbose", async (ISendGridSender mailer, IOptions<SendGridOptions> opt, HttpRequest req, CancellationToken ct) =>
{
    // cho phép đổi người nhận bằng query ?to=
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

// 👇 THÊM ROUTE TEST GỬI EMAIL Ở ĐÂY
app.MapGet("/test-email", async (ISendGridSender mailer) =>
{
    var ok = await mailer.SendTemplateEmailAsync(
        "ngohoangan0@gmail.com", // người nhận test
        "Anh Hoàng",                   // NAME
        "123456",                      // OTP
        10                             // TTL_MIN
    );
    return ok ? Results.Ok("✅ Email đã gửi thành công!")
              : Results.BadRequest("❌ Gửi email thất bại!");
}).WithDescription("Gửi email test qua SendGrid (Dynamic Template)");


// Redirect root "/" → "/swagger"
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// (Dev) Health check nhanh
app.MapGet("/healthz", () => Results.Ok(new
{
    db = string.IsNullOrWhiteSpace(conn) ? "InMemory" : "SqlServer",
    ok = true
}));

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");
app.Run();

public class NullSqlConnectionFactory : ISqlConnectionFactory
{
    public IDbConnection Create() =>
        throw new InvalidOperationException("Database is not configured. Set ConnectionStrings:Default to enable SQL Server.");
}

