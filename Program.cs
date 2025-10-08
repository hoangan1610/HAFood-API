using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;
using HAShop.Api.Data;
using HAShop.Api.Services;
using System.Data;

var builder = WebApplication.CreateBuilder(args);

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
