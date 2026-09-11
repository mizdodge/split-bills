using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using System.Net;
using System.Globalization;

var printBootstrapCode = args.Any(arg => string.Equals(arg, "--print-bootstrap-code", StringComparison.OrdinalIgnoreCase));
var resetMachineSecrets = args.Any(arg => string.Equals(arg, "--reset-machine-secrets", StringComparison.OrdinalIgnoreCase));
var printInstallationId = args.Any(arg => string.Equals(arg, "--print-installation-id", StringComparison.OrdinalIgnoreCase));
var applicationArgs = args.Where(arg => !string.Equals(arg, "--print-bootstrap-code", StringComparison.OrdinalIgnoreCase) &&
                                         !string.Equals(arg, "--reset-machine-secrets", StringComparison.OrdinalIgnoreCase) &&
                                         !string.Equals(arg, "--print-installation-id", StringComparison.OrdinalIgnoreCase)).ToArray();

CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("id-ID");
CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("id-ID");

var builder = WebApplication.CreateBuilder(applicationArgs);
builder.Services.AddLocalization();
var appDataPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
var dataProtectionPath = Path.Combine(appDataPath, "data-protection-keys");
Directory.CreateDirectory(appDataPath);
Directory.CreateDirectory(dataProtectionPath);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                       ?? "Data Source=App_Data/splitbill.db";
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connectionString));

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = false;
        options.Password.RequiredLength = 6;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/account/access-denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
});
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromMinutes(1));

// Tailscale Serve terminates HTTPS before forwarding to loopback IIS. Trust
// forwarded proto/host metadata only from the local proxy so HTTPS redirects
// do not loop, while direct LAN/Tailscale HTTP requests remain unchanged.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Loopback);
});

builder.Services.AddControllersWithViews(options =>
    options.ValueProviderFactories.Insert(0, new InvariantFormValueProviderFactory()))
    .AddViewLocalization()
    .AddDataAnnotationsLocalization();
builder.Services.AddHttpContextAccessor();
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("SplitBill")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("SplitBill encrypted AI settings require Windows DPAPI.");
dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddHttpClient(nameof(AiReceiptService), client => client.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddHttpClient(nameof(AiModelCatalogService), client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("SharePointGraph", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SplitBill/1.0");
});
builder.Services.AddHttpClient(nameof(LibNetWebPushTransport), client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<IAiReceiptService, AiReceiptService>();
builder.Services.AddScoped<IAiModelCatalogService, AiModelCatalogService>();
builder.Services.AddScoped<ISharePointGraphService, SharePointGraphService>();
builder.Services.AddScoped<ISharePointNotificationOutboxService, SharePointNotificationOutboxService>();
builder.Services.AddScoped<IFoodPickupRotationService, FoodPickupRotationService>();
builder.Services.AddSingleton<IFoodPickupRandomSource, SecureFoodPickupRandomSource>();
builder.Services.AddScoped<SharePointNotificationProcessor>();
builder.Services.AddHostedService<SharePointNotificationDispatcher>();
builder.Services.AddSingleton<SharePointSecretProtector>();
builder.Services.AddSingleton<ISharePointTestStateProtector, SharePointTestStateProtector>();
builder.Services.AddScoped<IAdminUserService, AdminUserService>();
builder.Services.AddSingleton<WebPushSecretProtector>();
builder.Services.AddScoped<IWebPushKeyService, WebPushKeyService>();
builder.Services.AddScoped<IWebPushSubscriptionService, WebPushSubscriptionService>();
builder.Services.AddScoped<IWebPushOutboxService, WebPushOutboxService>();
builder.Services.AddScoped<IWebPushTransport>(services =>
    new LibNetWebPushTransport(
        services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(LibNetWebPushTransport)),
        services.GetRequiredService<WebPushSecretProtector>()));
builder.Services.AddScoped<WebPushDeliveryProcessor>();
builder.Services.AddHostedService<WebPushDispatcher>();
builder.Services.AddScoped<IPaymentWorkflowService, PaymentWorkflowService>();
builder.Services.AddScoped<IPaymentProofStorageService, PaymentProofStorageService>();
builder.Services.AddScoped<IUploadedImageProcessor, UploadedImageProcessor>();
builder.Services.AddScoped<IInstallationSetupService, InstallationSetupService>();
builder.Services.AddScoped<IBackupService, BackupService>();
builder.Services.AddScoped<IReportQueryService, ReportQueryService>();
builder.Services.AddScoped<IExcelReportService, ExcelReportService>();
builder.Services.AddSingleton<ISplitBillCalculator, SplitBillCalculator>();
builder.Services.AddSingleton<IDashboardAnalyticsService, DashboardAnalyticsService>();

var app = builder.Build();

Directory.CreateDirectory(Path.Combine(appDataPath, "receipts"));
Directory.CreateDirectory(Path.Combine(appDataPath, "payment-proofs"));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("id-ID"),
    SupportedCultures = new[] { new CultureInfo("id-ID"), new CultureInfo("en-US") },
    SupportedUICultures = new[] { new CultureInfo("id-ID"), new CultureInfo("en-US") }
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

await DatabaseSeeder.SeedAsync(app.Services);
if (printBootstrapCode)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var state = await db.InstallationStates.SingleAsync(x => x.Id == 1);
    var setup = scope.ServiceProvider.GetRequiredService<IInstallationSetupService>();
    Console.WriteLine(setup.RevealBootstrapCode(state) is { } code
        ? $"SPLITBILL_BOOTSTRAP_CODE={code}"
        : "SPLITBILL_BOOTSTRAP_CODE=SETUP_ALREADY_COMPLETED");
    return;
}
if (resetMachineSecrets)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IBackupService>().ResetMachineSecretsAsync();
    Console.WriteLine("SPLITBILL_MACHINE_SECRETS_RESET=COMPLETE");
    return;
}
if (printInstallationId)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    Console.WriteLine($"SPLITBILL_INSTALLATION_ID={await db.InstallationStates.Where(x => x.Id == 1).Select(x => x.InstallationId).SingleOrDefaultAsync() ?? string.Empty}");
    return;
}
await app.RunAsync();

public partial class Program;
