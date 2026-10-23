using System.Text;
using System.Net;
using BonBon.API.Configuration;
using BonBon.Business.Configuration;
using BonBon.Business.Services.Implementations;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess;
using BonBon.DataAccess.Repositories.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
if(builder.Environment.IsDevelopment())
    builder.Configuration.AddJsonFile("appsettings.Local.json",optional:true,reloadOnChange:false)
        .AddEnvironmentVariables().AddCommandLine(args);
var paymentStaging=builder.Environment.IsEnvironment(PaymentStaging.EnvironmentName);
if(paymentStaging)
{
    var secretPath=Environment.GetEnvironmentVariable("BONBON_PAYMENT_STAGING_CONFIG");
    if(string.IsNullOrWhiteSpace(secretPath) || !Path.IsPathFullyQualified(secretPath))
        throw new InvalidOperationException("PaymentStaging requires an explicit absolute secret config path.");
    PaymentStaging.AddLocalConfiguration(builder.Configuration,secretPath).AddEnvironmentVariables();
}

// ─── Database ────────────────────────────────────────────────────────────────
if(paymentStaging)
{
    var source=PaymentStaging.CreateDataSource(builder.Configuration);
    builder.Services.AddSingleton(source);
    builder.Services.AddDbContext<BonBonDbContext>(options=>options.UseNpgsql(source));
}
else builder.Services.AddDbContext<BonBonDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", "bonbon")));

// ─── Repositories ────────────────────────────────────────────────────────────
builder.Services.AddScoped<IAccountRepository,      AccountRepository>();
builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
builder.Services.AddScoped<IBookingRepository,      BookingRepository>();

// ─── Services ────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IJwtService,  JwtService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IBookingService, BookingService>();
// Payment is opt-in; no startup migration or gateway/network request is made.
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection("Payment"));
builder.Services.Configure<PaymentReturnOptions>(builder.Configuration.GetSection("PaymentReturn"));
builder.Services.Configure<VnPayOptions>(builder.Configuration.GetSection("VnPay"));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IPaymentGateway, VnPayGateway>();
builder.Services.AddScoped<IPaymentService, PaymentService>();

// ─── JWT Authentication ──────────────────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be configured with at least 32 characters.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme    = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer           = true,
        ValidateAudience         = true,
        ValidateLifetime         = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer              = builder.Configuration["Jwt:Issuer"],
        ValidAudience            = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew                = TimeSpan.Zero, // Không cho phép trễ
    };
});

builder.Services.AddAuthorization();

// ─── CORS ─────────────────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];
// Configuration arrays merge by index; do not inherit extra production origins.
if(paymentStaging) allowedOrigins=[string.IsNullOrWhiteSpace(builder.Configuration["PaymentStaging:PublicOrigin"])
    ? "http://127.0.0.1:5289" : builder.Configuration["PaymentStaging:PublicOrigin"]!.TrimEnd('/')];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    if(paymentStaging)
    {
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        options.ForwardLimit=1;
        options.RequireHeaderSymmetry=true;
    }
});

// ─── Controllers ─────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ─── Swagger với Bearer support ──────────────────────────────────────────────
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title   = "BonBon API",
        Version = "v1",
    });

    // Thêm ô nhập JWT token trong Swagger UI
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = ParameterLocation.Header,
        Description  = "Nhập JWT token. Ví dụ: eyJhbG...",
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id   = "Bearer",
                },
            },
            Array.Empty<string>()
        },
    });
});

// ─── Build ───────────────────────────────────────────────────────────────────
var app = builder.Build();
if(paymentStaging)
    await PaymentStaging.VerifySchemaAsync(app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>());

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

if(paymentStaging) app.MapGet("/health", async (Npgsql.NpgsqlDataSource source,CancellationToken ct)=>
{
    try
    {
        await PaymentStaging.VerifySchemaAsync(source,ct);
        return Results.Ok(new { status="healthy",service="BonBon.API",environment=PaymentStaging.EnvironmentName,
            database=PaymentStaging.Database,databaseVerified=true,paymentEnabled=app.Configuration.GetValue<bool>("Payment:Enabled") });
    }
    catch { return Results.Problem(statusCode:503,title:"Staging readiness unavailable"); }
});
else app.MapGet("/health", () => Results.Ok(new
{
    status    = "healthy",
    service   = "BonBon.API",
    timestamp = DateTimeOffset.UtcNow,
}));

app.MapControllers();

app.Run();
