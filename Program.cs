using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.HttpOverrides;
using HAShop.Api.Data;
using HAShop.Api.Services;
using System.Data;
using Microsoft.Extensions.Options;
using DotNetEnv;

// NEW
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using HAShop.Api.Options; // <-- JwtOptions

var builder = WebApplication.CreateBuilder(args);

// =========================================================
// 1) Env (.env)
// =========================================================
Env.Load();

// =========================================================
// 2) SendGrid
// =========================================================
builder.Services.Configure<SendGridOptions>(opt =>
{
    opt.ApiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY") ?? "";
    opt.FromEmail = builder.Configuration["SendGrid:FromEmail"] ?? "";
    opt.FromName = builder.Configuration["SendGrid:FromName"] ?? "";
    opt.TemplateId = builder.Configuration["SendGrid:TemplateId"] ?? "";

    if (int.TryParse(builder.Configuration["SendGrid:BatchSize"], out var batch))
        opt.BatchSize = batch;
    if (int.TryParse(builder.Configuration["SendGrid:PollIntervalSeconds"], out var poll))
        opt.PollIntervalSeconds = poll;
});

// =========================================================
// 3) SendGrid services
// =========================================================
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IEmailQueueRepository, EmailQueueRepository>();
builder.Services.AddSingleton<ISendGridSender, SendGridSender>();
builder.Services.AddHostedService<EmailQueueWorker>();

// =========================================================
// 4) DATABASE
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
// 5) Controllers + Swagger + CORS + DI + ProblemDetails + Auth
// =========================================================
builder.Services.AddControllers();

// ProblemDetails + ModelState
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        // thêm traceId cho mọi lỗi
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
    };
});
builder.Services.Configure<ApiBehaviorOptions>(opt =>
{
    opt.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "VALIDATION_FAILED",
            Detail = "Dữ liệu gửi lên không hợp lệ.",
            Instance = context.HttpContext.Request.Path
        };
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        return new BadRequestObjectResult(problem);
    };
});

// Swagger (có Bearer)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "HAShop API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Nhập token: Bearer {token}"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// CORS
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

// DI
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IDeviceService, DeviceService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IPromotionService, PromotionService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ITrackingService, TrackingService>();



// ===== JWT Options (bind từ appsettings.json:Jwt) =====
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtIssuer = jwtSection.GetValue<string>("Issuer") ?? "";
var jwtAudience = jwtSection.GetValue<string>("Audience") ?? "";
var jwtKey = jwtSection.GetValue<string>("Key") ?? "";

// Authentication + Authorization (JWT)
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = true;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(jwtKey) ? "change-this-dev-key" : jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        // 401/403 thân thiện
        options.Events = new JwtBearerEvents
        {
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "application/problem+json; charset=utf-8";
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "about:blank",
                    title = "UNAUTHENTICATED",
                    status = 401,
                    detail = "Thiếu hoặc token không hợp lệ.",
                    traceId = ctx.HttpContext.TraceIdentifier
                });
                await ctx.Response.WriteAsync(json);
            },
            OnForbidden = async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                ctx.Response.ContentType = "application/problem+json; charset=utf-8";
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "about:blank",
                    title = "FORBIDDEN",
                    status = 403,
                    detail = "Bạn không có quyền truy cập tài nguyên này.",
                    traceId = ctx.HttpContext.TraceIdentifier
                });
                await ctx.Response.WriteAsync(json);
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// =========================================================
// 6) Middleware pipeline
// =========================================================

// Exception handler -> ProblemDetails 500
app.UseExceptionHandler(errApp =>
{
    errApp.Run(async context =>
    {
        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "about:blank",
            title = "INTERNAL_SERVER_ERROR",
            status = 500,
            detail = "Đã có lỗi không mong muốn.",
            traceId = context.TraceIdentifier
        });
        await context.Response.WriteAsync(json);
    });
});

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

app.UseStaticFiles();

app.UseCors("PublicCors");

// >>> Quan trọng: Authentication trước Authorization
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// =========================================================
// 7) Routes test SendGrid
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
// 8) Run
// =========================================================
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");
app.Run();

// =========================================================
// 9) Helpers
// =========================================================
public class NullSqlConnectionFactory : ISqlConnectionFactory
{
    public IDbConnection Create() =>
        throw new InvalidOperationException("Database is not configured. Set ConnectionStrings:Default to enable SQL Server.");
}
