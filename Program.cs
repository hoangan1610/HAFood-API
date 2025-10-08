using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
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
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "HAShop API",
        Version = "v1"
    });
});

// ========== CORS ==========
builder.Services.AddCors(options =>
{
    options.AddPolicy("PublicCors", policy =>
        policy
            .WithOrigins(
                "http://localhost:3000",       // local React dev
                "https://hafood.id.vn",        // main website
                "https://api.hafood.id.vn",    // Cloudflare Tunnel (optional)
                "https://hafood-mock-api.onrender.com/" // ✅ domain Render public
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
    );
});

builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<IAuthService, AuthService>();

var app = builder.Build();

// ========== MIDDLEWARE ==========
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "HAShop API v1");
    c.RoutePrefix = "swagger";
});

app.UseCors("PublicCors");

app.UseAuthorization();
app.MapControllers();

// Redirect root "/" → "/swagger"
app.MapGet("/", () => Results.Redirect("/swagger"))
   .ExcludeFromDescription();

app.Run();
