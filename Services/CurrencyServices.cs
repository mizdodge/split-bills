using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record CurrencyInfo(string Code, string Name, string Symbol, int MinorUnits);

public interface ICurrencyCatalog
{
    IReadOnlyList<CurrencyInfo> GetAll();
    bool TryGet(string? code, out CurrencyInfo currency);
}

public sealed class CurrencyCatalog : ICurrencyCatalog
{
    private static readonly CurrencyInfo[] Items =
    [
        new("IDR", "Indonesian Rupiah", "Rp", 0), new("USD", "US Dollar", "USD", 2),
        new("EUR", "Euro", "EUR", 2), new("GBP", "Pound Sterling", "GBP", 2),
        new("JPY", "Japanese Yen", "JPY", 0), new("CNY", "Chinese Yuan", "CNY", 2),
        new("SGD", "Singapore Dollar", "SGD", 2), new("MYR", "Malaysian Ringgit", "MYR", 2),
        new("THB", "Thai Baht", "THB", 2), new("AUD", "Australian Dollar", "AUD", 2),
        new("CAD", "Canadian Dollar", "CAD", 2), new("CHF", "Swiss Franc", "CHF", 2),
        new("HKD", "Hong Kong Dollar", "HKD", 2), new("KRW", "South Korean Won", "KRW", 0),
        new("NZD", "New Zealand Dollar", "NZD", 2), new("PHP", "Philippine Peso", "PHP", 2),
        new("VND", "Vietnamese Dong", "VND", 0), new("INR", "Indian Rupee", "INR", 2),
        new("BRL", "Brazilian Real", "BRL", 2), new("MXN", "Mexican Peso", "MXN", 2),
        new("ZAR", "South African Rand", "ZAR", 2), new("SEK", "Swedish Krona", "SEK", 2),
        new("NOK", "Norwegian Krone", "NOK", 2), new("DKK", "Danish Krone", "DKK", 2),
        new("PLN", "Polish Zloty", "PLN", 2), new("CZK", "Czech Koruna", "CZK", 2),
        new("HUF", "Hungarian Forint", "HUF", 2), new("ILS", "Israeli New Shekel", "ILS", 2),
        new("TRY", "Turkish Lira", "TRY", 2), new("RUB", "Russian Ruble", "RUB", 2)
    ];

    public IReadOnlyList<CurrencyInfo> GetAll() => Items;

    public bool TryGet(string? code, out CurrencyInfo currency)
    {
        currency = Items.FirstOrDefault(x => string.Equals(x.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return currency is not null;
    }
}

public sealed record DashboardCurrencySelection(IReadOnlyList<string> CurrencyCodes, string PrimaryCurrencyCode);

public interface IDashboardCurrencySelectionService
{
    DashboardCurrencySelection Normalize(string? storedCodes, string? storedPrimary, string reportingCurrency);
}

public sealed class DashboardCurrencySelectionService(ICurrencyCatalog catalog) : IDashboardCurrencySelectionService
{
    public const int MaximumCurrencies = 4;
    private static readonly string[] PreferredDefaults = ["USD", "SGD", "EUR", "JPY"];

    public DashboardCurrencySelection Normalize(string? storedCodes, string? storedPrimary, string reportingCurrency)
    {
        var quote = reportingCurrency.Trim().ToUpperInvariant();
        var selected = (storedCodes ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => code.ToUpperInvariant())
            .Where(code => code != quote && catalog.TryGet(code, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumCurrencies)
            .ToList();

        if (selected.Count == 0)
        {
            selected = PreferredDefaults
                .Where(code => code != quote && catalog.TryGet(code, out _))
                .Take(MaximumCurrencies)
                .ToList();
        }

        var primary = storedPrimary?.Trim().ToUpperInvariant();
        if (primary is null || !selected.Contains(primary, StringComparer.OrdinalIgnoreCase))
            primary = selected[0];

        selected.RemoveAll(code => string.Equals(code, primary, StringComparison.OrdinalIgnoreCase));
        selected.Insert(0, primary);
        return new DashboardCurrencySelection(selected, primary);
    }
}

public interface ICurrencyConfigurationService
{
    Task<CurrencyConfiguration> GetOrCreateAsync(CancellationToken cancellationToken = default);
    Task<bool> IsDefaultCurrencyLockedAsync(CancellationToken cancellationToken = default);
    string ProtectApiKey(string secret);
    string? UnprotectApiKey(string? protectedSecret);
}

public sealed class CurrencyConfigurationService(ApplicationDbContext db, IDataProtectionProvider protection) : ICurrencyConfigurationService
{
    private const string SecretPurpose = "SplitBill.CurrencyConfiguration.ApiKey.v1";

    public async Task<CurrencyConfiguration> GetOrCreateAsync(CancellationToken cancellationToken = default) =>
        await db.CurrencyConfigurations.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken)
        ?? new CurrencyConfiguration { Id = 1, UpdatedAt = DateTimeOffset.UtcNow };

    public Task<bool> IsDefaultCurrencyLockedAsync(CancellationToken cancellationToken = default) =>
        db.Transactions.AnyAsync(x => x.Status != TransactionStatus.Draft, cancellationToken);

    public string ProtectApiKey(string secret) =>
        protection.CreateProtector(SecretPurpose).Protect(secret);

    public string? UnprotectApiKey(string? protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret)) return null;
        try { return protection.CreateProtector(SecretPurpose).Unprotect(protectedSecret); }
        catch (Exception) { return null; }
    }
}

public sealed record CurrencyRateResult(string BaseCurrency, string QuoteCurrency, decimal Rate, DateOnly EffectiveDate, string Source);

public interface ICurrencyRateProvider
{
    Task<IReadOnlyList<CurrencyInfo>> GetCurrenciesAsync(string baseUrl, CurrencyAuthenticationMode mode, string? secret, CancellationToken cancellationToken = default);
    Task<CurrencyRateResult> GetRateAsync(string baseUrl, CurrencyAuthenticationMode mode, string? secret, string baseCurrency, string quoteCurrency, DateOnly date, CancellationToken cancellationToken = default);
}

public sealed class FrankfurterRateProvider(IHttpClientFactory httpClientFactory) : ICurrencyRateProvider
{
    private const int MaxResponseBytes = 2_000_000;

    public async Task<IReadOnlyList<CurrencyInfo>> GetCurrenciesAsync(string baseUrl, CurrencyAuthenticationMode mode, string? secret, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(baseUrl, "/v2/currencies", mode, secret, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        List<CurrencyInfo> currencies;
        if (root.ValueKind == JsonValueKind.Array)
        {
            currencies = root.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x =>
                {
                    var code = x.TryGetProperty("iso_code", out var isoCode) ? isoCode.GetString() : null;
                    var name = x.TryGetProperty("name", out var currencyName) ? currencyName.GetString() : null;
                    var symbol = x.TryGetProperty("symbol", out var currencySymbol) ? currencySymbol.GetString() : null;
                    return new CurrencyInfo((code ?? string.Empty).ToUpperInvariant(), name ?? code ?? string.Empty, symbol ?? code ?? string.Empty, 2);
                })
                .Where(x => x.Code.Length == 3)
                .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            currencies = root.EnumerateObject()
                .Where(x => x.Name.Length == 3 && x.Value.ValueKind == JsonValueKind.String)
                .Select(x => new CurrencyInfo(x.Name.ToUpperInvariant(), x.Value.GetString() ?? x.Name, x.Name, 2))
                .ToList();
        }
        else
        {
            throw new InvalidOperationException("Currency catalog response is invalid.");
        }

        if (currencies.Count == 0) throw new InvalidOperationException("Currency catalog response contains no supported currency codes.");
        return currencies;
    }

    public async Task<CurrencyRateResult> GetRateAsync(string baseUrl, CurrencyAuthenticationMode mode, string? secret, string baseCurrency, string quoteCurrency, DateOnly date, CancellationToken cancellationToken = default)
    {
        var path = $"/v2/rates?base={Uri.EscapeDataString(baseCurrency)}&quotes={Uri.EscapeDataString(quoteCurrency)}&date={date:yyyy-MM-dd}";
        using var response = await SendAsync(baseUrl, path, mode, secret, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        JsonElement row = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().FirstOrDefault() : root;
        if (row.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Rate response is invalid.");
        var baseCode = row.TryGetProperty("base", out var b) ? b.GetString() : baseCurrency;
        var quoteCode = row.TryGetProperty("quote", out var q) ? q.GetString() : quoteCurrency;
        var rateElement = row.TryGetProperty("rate", out var rate) ? rate : default;
        if (rateElement.ValueKind != JsonValueKind.Number || !rateElement.TryGetDecimal(out var parsed) || parsed <= 0 || parsed > 1_000_000_000_000m)
            throw new InvalidOperationException("Rate response contains an invalid rate.");
        var effective = row.TryGetProperty("date", out var d) && DateOnly.TryParse(d.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
            ? parsedDate : date;
        return new CurrencyRateResult((baseCode ?? baseCurrency).ToUpperInvariant(), (quoteCode ?? quoteCurrency).ToUpperInvariant(), parsed, effective, "FrankfurterV2");
    }

    private async Task<HttpResponseMessage> SendAsync(string baseUrl, string path, CurrencyAuthenticationMode mode, string? secret, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) || root.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment))
            throw new InvalidOperationException("Provider URL must be an absolute HTTP(S) URL without query or fragment.");
        var uri = new Uri(root, path);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (mode == CurrencyAuthenticationMode.Bearer && !string.IsNullOrWhiteSpace(secret)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (mode == CurrencyAuthenticationMode.XApiKey && !string.IsNullOrWhiteSpace(secret)) request.Headers.Add("X-Api-Key", secret);
        var client = httpClientFactory.CreateClient("CurrencyRateProvider");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) { response.Dispose(); throw new HttpRequestException($"Currency provider returned {(int)response.StatusCode} {response.StatusCode}."); }
        return response;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920]; int total;
        while ((total = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + total > MaxResponseBytes) throw new InvalidOperationException("Currency provider response is too large.");
            await memory.WriteAsync(buffer.AsMemory(0, total), cancellationToken);
        }
        memory.Position = 0;
        return await JsonDocument.ParseAsync(memory, cancellationToken: cancellationToken);
    }
}

public interface ICurrencyRateService
{
    Task<CurrencyRateResult?> GetRateAsync(string baseCurrency, string quoteCurrency, DateOnly date, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CurrencyExchangeRate>> RefreshAsync(CancellationToken cancellationToken = default);
    decimal Convert(decimal amount, decimal rate) => Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Owns all conversion and minor-unit reconciliation. Callers provide values
/// that are already in the transaction's original currency; presentation code
/// must never multiply an exchange rate itself.
/// </summary>
public interface ICurrencyConversionService
{
    decimal Convert(decimal amount, decimal rate);
    IReadOnlyList<decimal> Reconcile(IReadOnlyList<decimal> amounts, decimal rate);
}

public sealed class CurrencyConversionService : ICurrencyConversionService
{
    public decimal Convert(decimal amount, decimal rate)
    {
        if (rate <= 0m || decimal.Round(rate, 12) != rate)
            throw new ArgumentOutOfRangeException(nameof(rate), "Exchange rate must be positive and finite.");
        return Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
    }

    public IReadOnlyList<decimal> Reconcile(IReadOnlyList<decimal> amounts, decimal rate)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        if (amounts.Count == 0) return [];
        if (rate <= 0m) throw new ArgumentOutOfRangeException(nameof(rate));

        var exact = amounts.Select(amount => amount * rate).ToArray();
        var floors = exact.Select(value => decimal.Floor(value * 100m) / 100m).ToArray();
        var target = Math.Round(exact.Sum(), 2, MidpointRounding.AwayFromZero);
        var centsToDistribute = (int)decimal.Round((target - floors.Sum()) * 100m, 0, MidpointRounding.AwayFromZero);
        var order = exact.Select((value, index) => new
            {
                index,
                remainder = value - floors[index]
            })
            .OrderByDescending(x => x.remainder)
            .ThenBy(x => x.index)
            .ToList();
        for (var i = 0; i < centsToDistribute && i < order.Count; i++)
            floors[order[i].index] += 0.01m;
        return floors;
    }
}

public sealed class CurrencyRateService(ApplicationDbContext db, ICurrencyRateProvider provider, ICurrencyCatalog catalog, IDataProtectionProvider dataProtectionProvider) : ICurrencyRateService
{
    public async Task<CurrencyRateResult?> GetRateAsync(string baseCurrency, string quoteCurrency, DateOnly date, CancellationToken cancellationToken = default)
    {
        baseCurrency = baseCurrency.Trim().ToUpperInvariant(); quoteCurrency = quoteCurrency.Trim().ToUpperInvariant();
        if (baseCurrency == quoteCurrency) return new CurrencyRateResult(baseCurrency, quoteCurrency, 1m, date, "Identity");
        var configuration = await db.CurrencyConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (configuration is null) return null;
        var candidates = await db.CurrencyExchangeRates.AsNoTracking()
            .Where(x => x.BaseCurrencyCode == baseCurrency && x.QuoteCurrencyCode == quoteCurrency)
            .ToListAsync(cancellationToken);
        var cached = candidates.Where(x => x.EffectiveDate <= date).OrderByDescending(x => x.EffectiveDate).FirstOrDefault();
        if (cached is not null) return new CurrencyRateResult(baseCurrency, quoteCurrency, cached.Rate, cached.EffectiveDate, cached.ProviderKind.ToString());
        try
        {
            var secret = Unprotect(configuration.ProtectedApiKey);
            var remote = await provider.GetRateAsync(configuration.BaseUrl, configuration.AuthenticationMode, secret, baseCurrency, quoteCurrency, date, cancellationToken);
            db.CurrencyExchangeRates.Add(new CurrencyExchangeRate
            {
                BaseCurrencyCode = remote.BaseCurrency, QuoteCurrencyCode = remote.QuoteCurrency, Rate = remote.Rate,
                EffectiveDate = remote.EffectiveDate, RetrievedAt = DateTimeOffset.UtcNow, ProviderKind = configuration.ProviderKind,
                ProviderBaseUrlFingerprint = Fingerprint(configuration.BaseUrl)
            });
            await db.SaveChangesAsync(cancellationToken);
            return remote;
        }
        catch (HttpRequestException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public async Task<IReadOnlyList<CurrencyExchangeRate>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await db.CurrencyConfigurations.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken) ?? throw new InvalidOperationException("Currency configuration is missing.");
        var secret = Unprotect(configuration.ProtectedApiKey);
        var available = await provider.GetCurrenciesAsync(configuration.BaseUrl, configuration.AuthenticationMode, secret, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = new List<CurrencyExchangeRate>();
        foreach (var currency in available.Where(x => catalog.TryGet(x.Code, out _) && !string.Equals(x.Code, configuration.DefaultCurrencyCode, StringComparison.OrdinalIgnoreCase)).Take(80))
        {
            var result = await provider.GetRateAsync(configuration.BaseUrl, configuration.AuthenticationMode, secret, currency.Code, configuration.DefaultCurrencyCode, today, cancellationToken);
            rows.Add(new CurrencyExchangeRate { BaseCurrencyCode = result.BaseCurrency, QuoteCurrencyCode = result.QuoteCurrency, Rate = result.Rate, EffectiveDate = result.EffectiveDate, RetrievedAt = DateTimeOffset.UtcNow, ProviderKind = configuration.ProviderKind, ProviderBaseUrlFingerprint = Fingerprint(configuration.BaseUrl) });
        }
        foreach (var row in rows)
        {
            var existing = await db.CurrencyExchangeRates.SingleOrDefaultAsync(x => x.BaseCurrencyCode == row.BaseCurrencyCode && x.QuoteCurrencyCode == row.QuoteCurrencyCode && x.EffectiveDate == row.EffectiveDate && x.ProviderBaseUrlFingerprint == row.ProviderBaseUrlFingerprint, cancellationToken);
            if (existing is null) db.CurrencyExchangeRates.Add(row); else existing.Rate = row.Rate;
        }
        configuration.LastRefreshAt = DateTimeOffset.UtcNow; configuration.LastRefreshSucceeded = true; configuration.LastRefreshError = null;
        await db.SaveChangesAsync(cancellationToken);
        return rows;
    }

    private string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return dataProtectionProvider.CreateProtector("SplitBill.CurrencyConfiguration.ApiKey.v1").Unprotect(value); } catch { return null; }
    }

    public static string Fingerprint(string baseUrl) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseUrl.Trim())))[..16];
}

public interface ICurrencyFormatter
{
    string Format(decimal amount, string currencyCode, CultureInfo? culture = null);
}

public sealed class CurrencyFormatter(ICurrencyCatalog catalog) : ICurrencyFormatter
{
    public string Format(decimal amount, string currencyCode, CultureInfo? culture = null)
    {
        var cultureInfo = culture ?? CultureInfo.CurrentCulture;
        var code = currencyCode.Trim().ToUpperInvariant();
        var precision = catalog.TryGet(code, out var info) ? info.MinorUnits : 2;
        var format = precision == 0 ? "N0" : "N2";
        return code == "IDR" && cultureInfo.Name.StartsWith("id", StringComparison.OrdinalIgnoreCase)
            ? $"Rp {amount.ToString(format, cultureInfo)}" : $"{code} {amount.ToString(format, cultureInfo)}";
    }
}

public sealed class CurrencyRateRefreshService(IServiceScopeFactory scopeFactory, ILogger<CurrencyRateRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var config = await db.CurrencyConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, stoppingToken);
                if (config?.AutoRefreshEnabled == true) await scope.ServiceProvider.GetRequiredService<ICurrencyRateService>().RefreshAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogWarning("Currency refresh failed: {Message}", exception.Message); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
