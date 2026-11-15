using System.Text;
using BonBon.API.Infrastructure;
using BonBon.API.Services;
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
using Microsoft.Extensions.FileProviders;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
if(builder.Environment.IsDevelopment())
    builder.Configuration.AddJsonFile("appsettings.Local.json",optional:true,reloadOnChange:false)
        .AddEnvironmentVariables().AddCommandLine(args);
var paymentStaging=builder.Environment.IsEnvironment(PaymentStaging.EnvironmentName);
var paymentLocal=builder.Environment.IsEnvironment(PaymentLocal.EnvironmentName);
var paymentIsolated=paymentStaging || paymentLocal;
if(paymentIsolated)
{
    var secretPath=Environment.GetEnvironmentVariable(paymentLocal ? "BONBON_PAYMENT_LOCAL_CONFIG" : "BONBON_PAYMENT_STAGING_CONFIG");
    if(string.IsNullOrWhiteSpace(secretPath) || !Path.IsPathFullyQualified(secretPath))
        throw new InvalidOperationException("PaymentStaging requires an explicit absolute secret config path.");
    PaymentStaging.AddLocalConfiguration(builder.Configuration,secretPath).AddEnvironmentVariables();
    var sepayPath=Environment.GetEnvironmentVariable("BONBON_SEPAY_CONFIG");
    if(!string.IsNullOrWhiteSpace(sepayPath)) {
        if(!Path.IsPathFullyQualified(sepayPath)) throw new InvalidOperationException("Absolute SePay secret file required.");
        using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(sepayPath));
        if(document.RootElement.EnumerateObject().Any(x=>x.Name!="SePay")) throw new InvalidOperationException("Dedicated SePay file may only contain SePay settings.");
        PaymentStaging.AddLocalConfiguration(builder.Configuration,sepayPath).AddEnvironmentVariables();
    }
}
if(builder.Configuration.GetValue<bool>("SePay:Enabled")) {
    var sepayOptions = builder.Configuration.GetSection("SePay").Get<SePayOptions>() ?? new();
    if((!paymentIsolated && !(builder.Environment.IsProduction() && sepayOptions.AllowDemoProduction)) || !sepayOptions.IsValid())
        throw new InvalidOperationException("SePay sandbox requires an isolated verified Payment environment and complete configuration.");
    builder.Configuration["PaymentLocal:IpnSimulationEnabled"]="false";
}

// ─── Database ────────────────────────────────────────────────────────────────
if(paymentLocal) builder.WebHost.UseUrls("http://127.0.0.1:5288", "https://127.0.0.1:5287");
if(paymentIsolated)
{
    var source=paymentLocal ? PaymentLocal.CreateDataSource(builder.Configuration) : PaymentStaging.CreateDataSource(builder.Configuration);
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
builder.Services.AddScoped<ITrackingRepository,     TrackingRepository>();
builder.Services.AddScoped<IVehicleRepository,      VehicleRepository>();
builder.Services.AddScoped<IAdminRepository,        AdminRepository>();

// ─── Services ────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IJwtService,  JwtService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddScoped<ITrackingService, TrackingService>();
builder.Services.AddSignalR();
builder.Services.AddScoped<IVehicleService, VehicleService>();
builder.Services.AddSingleton<VehicleImageProcessor>();
// Payment is opt-in; no startup migration or gateway/network request is made.
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection("Payment"));
builder.Services.Configure<PaymentReturnOptions>(builder.Configuration.GetSection("PaymentReturn"));
builder.Services.Configure<VnPayOptions>(builder.Configuration.GetSection("VnPay"));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IPaymentGateway, VnPayGateway>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddHostedService<InstantBookingExpiryService>();
builder.Services.Configure<SePayOptions>(builder.Configuration.GetSection("SePay"));
builder.Services.AddSingleton<ISePaySignatureService,SePaySignatureService>();
builder.Services.AddScoped<ISePayPaymentRepository,SePayPaymentRepository>();
builder.Services.AddScoped<ISePayCheckoutService,SePayCheckoutService>();
builder.Services.AddScoped<ISePayReconciliationService,SePayReconciliationService>();
builder.Services.AddScoped<ISePayIpnHandler,SePayIpnHandler>();
// Dedicated owned client: no auth/body logging, redirects, proxy or create-checkout retry.
builder.Services.AddScoped<ISePayGateway>(provider => new SePayGateway(new HttpClient(new SocketsHttpHandler {
    AllowAutoRedirect=false,UseProxy=false,ConnectTimeout=TimeSpan.FromSeconds(5) }) {Timeout=TimeSpan.FromSeconds(15)},
    provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SePayOptions>>(),provider.GetRequiredService<ISePaySignatureService>()));

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
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(accessToken) && context.Request.Path.StartsWithSegments("/hubs/tracking"))
                context.Token = accessToken;
            return Task.CompletedTask;
        },
    };
});

builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// ─── CORS ─────────────────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];
// Configuration arrays merge by index; do not inherit extra production origins.
if(paymentStaging && SePayStaging.IsSelected(builder.Configuration)) allowedOrigins=SePayStaging.FrontendOrigins(builder.Configuration);
else if(paymentIsolated) allowedOrigins=[string.IsNullOrWhiteSpace(builder.Configuration["PaymentStaging:PublicOrigin"])
    ? "http://127.0.0.1:5289" : builder.Configuration["PaymentStaging:PublicOrigin"]!.TrimEnd('/')];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    if(paymentIsolated)
    {
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        foreach(var proxy in paymentLocal ? Array.Empty<IPAddress>() : PaymentStaging.TrustedProxyAddresses(builder.Configuration))
            options.KnownProxies.Add(proxy);
        options.ForwardLimit=1;
        options.RequireHeaderSymmetry=true;
    }
});

// ─── Controllers ─────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
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
if(paymentLocal)
    await PaymentLocal.VerifySchemaAsync(app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>());
else if(paymentStaging)
    await PaymentStaging.VerifyConfiguredSchemaAsync(app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(),app.Configuration);
if(app.Configuration.GetValue<bool>("SePay:Enabled")) {
    // Read-only readiness guard checks the provider marker/indexes using the restricted runtime role.
    using var scope=app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ISePayPaymentRepository>().Payments.GetByReferenceAsync(Guid.Empty.ToString("N"));
}

app.UseForwardedHeaders();

// The existing reverse proxy routes both domains to this service.
// Serve the independent admin build only on its configured hostname.
var adminRoot = builder.Configuration["AdminPortal:Path"] ?? Path.Combine(app.Environment.ContentRootPath, "admin");
var adminHost = builder.Configuration["AdminPortal:Host"] ?? "bonbon-admin.kitsuracloud.com";
if (Directory.Exists(adminRoot))
{
    app.MapWhen(context => context.Request.Host.Host.Equals(adminHost, StringComparison.OrdinalIgnoreCase)
        && !context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/health", portal =>
    {
        portal.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(adminRoot) });
        portal.Run(async context =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            { context.Response.StatusCode = 405; return; }
            if (Path.HasExtension(context.Request.Path)) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.SendFileAsync(Path.Combine(adminRoot, "admin.html"));
        });
    });
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
// Keep CORS outside the error handler so allowed origins also receive handled 500 responses.
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if(paymentLocal) app.MapGet("/health", async (Npgsql.NpgsqlDataSource source,CancellationToken ct)=>
{
    try { await PaymentLocal.VerifySchemaAsync(source,ct); return Results.Ok(new { status="healthy", environment=PaymentLocal.EnvironmentName, database=PaymentLocal.Database, databaseVerified=true, paymentEnabled=app.Configuration.GetValue<bool>("Payment:Enabled"), publicIpnReady=false }); }
    catch { return Results.Problem(statusCode:503,title:"Local readiness unavailable"); }
});
else if(paymentStaging) app.MapGet("/health", async (Npgsql.NpgsqlDataSource source,CancellationToken ct)=>
{
    try
    {
        await PaymentStaging.VerifyConfiguredSchemaAsync(source,app.Configuration,ct);
        return Results.Ok(new { status="healthy",service="BonBon.API",environment=PaymentStaging.EnvironmentName,
            database=PaymentStaging.ConfiguredDatabase(app.Configuration),databaseVerified=true,paymentEnabled=app.Configuration.GetValue<bool>("Payment:Enabled"),sepayEnabled=app.Configuration.GetValue<bool>("SePay:Enabled") });
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
app.MapLocalVnPayIpnSimulation();
app.MapHub<TrackingHub>("/hubs/tracking");

app.Run();
