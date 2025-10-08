using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;
using HAShop.Api.Data;
using HAShop.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ========== DATABASE ==========
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

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
                // Origin FE thực sự; nếu Cloudflare Tunnel dùng cho FE thì để domain FE, 
                // không cần thêm domain API của chính bạn
                "https://hafood-mock-api.onrender.com" // <-- bỏ dấu "/" ở cuối
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
    );
});

// ========== DI ==========
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<IAuthService, AuthService>();

var app = builder.Build();

// (Tuỳ chọn nhưng nên bật) nhận biết HTTPS/host từ reverse proxy của App Service
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

// ❌ KHÔNG cần tự bind cổng khi chạy trên App Service (Code deploy)
// var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
// app.Urls.Add($"http://*:{port}");

app.Run();
