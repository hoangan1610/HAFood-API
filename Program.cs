// =========================================================
// using
// =========================================================
using DotNetEnv;
using HaFood.IntentClassifier; // ModelInput, ModelOutput, RegexFeatsFactory
using HAShop.Api.Data;
using HAShop.Api.Middleware;
using HAShop.Api.Options;            // JwtOptions, PaymentsFlags, VnPayOptions, ZaloPayOptions, SendGridOptions
using HAShop.Api.Payments;           // VnPayService, IZaloPayGateway
using HAShop.Api.Services;           // I*Service DI
using HAShop.Api.Utils;              // IApiProblemWriter, ApiProblemWriter
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;            // ModelInput, ModelOutput, RegexFeatsFactory
               // PredictionEnginePool
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Data;
using System.Text;
using HAShop.Api.Sockets;
using HAShop.Api.Realtime;
// ...



// =========================================================
// builder
// =========================================================
var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------
// [A] ENV & CONFIG
// ---------------------------------------------------------
Env.Load(); // Load biến môi trường từ .env (nếu có)

// ---------------------------------------------------------
// [B] HTTP CLIENTS (OpenAI-compatible local LLM, SendGrid)
// ---------------------------------------------------------
builder.Services.AddHttpClient("OpenAI", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["OpenAI:BaseUrl"]!); // vd: http://localhost:5001/v1
    c.DefaultRequestHeaders.Add("Authorization", "Bearer " + builder.Configuration["OpenAI:ApiKey"]);
    c.Timeout = TimeSpan.FromSeconds(60); // KoboldCpp CPU có thể chậm → tăng timeout
});

// Client chung cho các dịch vụ khác (SendGridSender, …)
builder.Services.AddHttpClient();

// ---------------------------------------------------------
// [C] CORE MVC + ProblemDetails + ModelState
// ---------------------------------------------------------
builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(opt =>
    {
        opt.InvalidModelStateResponseFactory = context =>
        {
            // Bạn có ErrorCatalog.Friendly ⇒ tái sử dụng nếu muốn
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

// ProblemDetails (bổ sung traceId toàn cục)
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
    };
});

// ---------------------------------------------------------
// [D] FORM / UPLOAD
// ---------------------------------------------------------
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 30L * 1024 * 1024; // 30MB
});

// ---------------------------------------------------------
// [E] CORS (DUY NHẤT 1 POLICY) 
// ---------------------------------------------------------
builder.Services.AddCors(options =>
{
    options.AddPolicy("AppCors", policy =>
        policy.WithOrigins(
                // FE + Swagger local
                "https://localhost:44336",
                "http://localhost:44336",
                "http://localhost:3000",
                // CMS local
                "http://localhost:51572",
                "http://127.0.0.1:51572",
                // Prod
                "https://cms.hafood.id.vn",
                "https://hafood.id.vn",
                "https://www.hafood.id.vn",
                // Mock (nếu dùng)
                "https://hafood-mock-api.onrender.com"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()                 // nếu dùng cookie auth
            .SetPreflightMaxAge(TimeSpan.FromDays(1))
    );
});

// ---------------------------------------------------------
// [F] SWAGGER + JWT SecurityScheme
// ---------------------------------------------------------
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
    c.SupportNonNullableReferenceTypes();
});

// ---------------------------------------------------------
// [G] DATABASE
// ---------------------------------------------------------
var conn = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(conn))
{
    // Dev fallback
    builder.Services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase("dev"));
    builder.Services.AddTransient<ISqlConnectionFactory>(_ => new NullSqlConnectionFactory());
}
else
{
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseSqlServer(conn, o => o.EnableRetryOnFailure()));
    builder.Services.AddTransient<ISqlConnectionFactory, SqlConnectionFactory>();
}

// ---------------------------------------------------------
// [H] AUTHENTICATION (JWT) + AUTHZ
// ---------------------------------------------------------
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtIssuer = jwtSection.GetValue<string>("Issuer") ?? "";
var jwtAudience = jwtSection.GetValue<string>("Audience") ?? "";
var jwtKey = jwtSection.GetValue<string>("Key") ?? "";

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
           IssuerSigningKey = new SymmetricSecurityKey(
               Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(jwtKey) ? "change-this-dev-key" : jwtKey)),
           ClockSkew = TimeSpan.FromSeconds(30)
       };

       // Đọc token từ cookie nếu thiếu header Authorization
       options.Events = new JwtBearerEvents
       {
           OnMessageReceived = ctx =>
           {
               if (string.IsNullOrEmpty(ctx.Token))
                   ctx.Token = ctx.Request.Cookies["AuthToken"];
               return Task.CompletedTask;
           },
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

// ---------------------------------------------------------
// [I] BIZ OPTIONS + SERVICES (Payments, SendGrid, …)
// ---------------------------------------------------------
builder.Services.Configure<VnPayOptions>(builder.Configuration.GetSection("VnPay"));
builder.Services.AddSingleton<VnPayService>();

builder.Services.Configure<ZaloPayOptions>(builder.Configuration.GetSection("Payments:ZaloPay"));
builder.Services.AddSingleton<IZaloPayGateway, ZaloPayService>();

builder.Services.Configure<PaymentsFlags>(builder.Configuration.GetSection("Payments"));
builder.Services.Configure<FrontendOptions>(builder.Configuration.GetSection("Frontend"));

// SendGrid options
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

// SendGrid services
builder.Services.AddSingleton<IEmailQueueRepository, EmailQueueRepository>();
builder.Services.AddSingleton<ISendGridSender, SendGridSender>();
builder.Services.AddHostedService<EmailQueueWorker>();

// ---------------------------------------------------------
// [J] CHAT (Hybrid Router + Tools)
// ---------------------------------------------------------

// ---------------------------------------------------------
// [ML] INTENT CLASSIFIER (PredictionEnginePool)
// ---------------------------------------------------------
builder.Services.AddSingleton<IIntentService>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();

    // Đường dẫn model
    var modelPath = cfg["ML:IntentModelPath"]
        ?? Path.Combine(env.ContentRootPath, "Models", "hafood_intent_model.zip");

    // Tạo service
    var svc = new IntentService(modelPath);

    // Watch file để tự reload khi model.zip thay đổi
    var dir = Path.GetDirectoryName(modelPath)!;
    var file = Path.GetFileName(modelPath);
    var fp = new PhysicalFileProvider(dir);

    ChangeToken.OnChange(
        () => fp.Watch(file),
        () =>
        {
            try { svc.Reload(); } catch { /* nuốt lỗi reload */ }
        });

    return svc;
});



builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IChatTools, ChatTools>();

// ---------------------------------------------------------
// [K] DOMAIN SERVICES (Auth, Device, User, Product, Cart, …)
// ---------------------------------------------------------
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IDeviceService, DeviceService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IPromotionService, PromotionService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ITrackingService, TrackingService>();
builder.Services.AddScoped<IAddressService, AddressService>();
builder.Services.AddScoped<IFlashSaleService, FlashSaleService>();
builder.Services.AddScoped<IGamificationService, GamificationService>();




builder.Services.AddSingleton<FlashSaleBroadcaster>(); // concrete
builder.Services.AddSingleton<IFlashSaleBroadcaster>(sp =>
    sp.GetRequiredService<FlashSaleBroadcaster>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<FlashSaleBroadcaster>());



// ---------------------------------------------------------
// [L] MIDDLEWARE TUỲ BIẾN (ProblemDetails writer & middleware)
// ---------------------------------------------------------
builder.Services.AddTransient<ProblemDetailsExceptionMiddleware>();
builder.Services.AddSingleton<IApiProblemWriter, ApiProblemWriter>();

// =========================================================
// app
// =========================================================
var app = builder.Build();

// ---------------------------------------------------------
// [M] EXCEPTION HANDLER (GLOBAL 500 → ProblemDetails)
// ---------------------------------------------------------
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

// ---------------------------------------------------------
// [N] FORWARDED HEADERS, HTTPS, STATIC
// ---------------------------------------------------------
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
});

app.UseHttpsRedirection();
app.UseStaticFiles();

// ---------------------------------------------------------
// [O] SWAGGER
// ---------------------------------------------------------
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "HAShop API v1");
    c.RoutePrefix = "swagger";
});

// ---------------------------------------------------------
// [P] CORS + AUTHN/AUTHZ + CUSTOM MIDDLEWARE
// ---------------------------------------------------------
app.UseCors("AppCors");                       // ✅ chỉ 1 policy
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ProblemDetailsExceptionMiddleware>();

// ---------------------------------------------------------
// [Q] ROUTES (Controllers + Utility endpoints)
// ---------------------------------------------------------
app.MapControllers();
app.MapFlashSaleSse();

// SendGrid test (giữ nguyên)
app.MapGet("/test-email", async (ISendGridSender mailer) =>
{
    var ok = await mailer.SendTemplateEmailAsync(
        "ngohoangan0@gmail.com",
        "Anh Hoàng",
        "123456",
        10
    );
    return ok ? Results.Ok("✅ Email đã gửi thành công!") : Results.BadRequest("❌ Gửi email thất bại!");
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

// Healthcheck đơn giản
app.MapGet("/healthz", () => Results.Ok(new
{
    db = string.IsNullOrWhiteSpace(conn) ? "InMemory" : "SqlServer",
    ok = true
}));

// ---------------------------------------------------------
// [R] RUN
// ---------------------------------------------------------
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");
app.Run();

// =========================================================
// Helpers
// =========================================================
public class NullSqlConnectionFactory : ISqlConnectionFactory
{
    public IDbConnection Create() =>
        throw new InvalidOperationException("Database is not configured. Set ConnectionStrings:Default to enable SQL Server.");
}
