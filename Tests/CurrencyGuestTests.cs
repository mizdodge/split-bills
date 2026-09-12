using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class CurrencyGuestTests
{
    [Fact]
    public void CurrencyCatalog_ContainsDefaultAndRejectsThreeMinorUnitCurrencies()
    {
        var catalog = new CurrencyCatalog();
        Assert.True(catalog.TryGet("idr", out var idr));
        Assert.Equal(0, idr.MinorUnits);
        Assert.False(catalog.TryGet("KWD", out _));
        Assert.True(catalog.TryGet("USD", out var usd));
        Assert.Equal(2, usd.MinorUnits);
    }

    [Fact]
    public void GuestToken_IsUrlSafeAndProtectedRoundTrips()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "splitbill-token-" + Guid.NewGuid().ToString("N"))));
        var service = new GuestAccessTokenService(provider);
        var created = service.Create();
        Assert.Equal(64, created.Hash.Length);
        Assert.InRange(created.Token.Length, 40, 60);
        Assert.DoesNotContain("+", created.Token);
        Assert.DoesNotContain("/", created.Token);
        Assert.Equal(created.Token, service.Unprotect(created.Protected));
        Assert.NotEqual(created.Hash, service.Create().Hash);
    }

    [Fact]
    public void CurrencyConversion_ReconcilesMinorUnitRounding()
    {
        var service = new CurrencyConversionService();
        var values = service.Reconcile([10.01m, 10.01m, 10.01m], 1.2345m);

        Assert.Equal(3, values.Count);
        Assert.Equal(Math.Round(30.03m * 1.2345m, 2, MidpointRounding.AwayFromZero), values.Sum());
        Assert.All(values, value => Assert.Equal(2, decimal.Round(value, 2).Scale));
    }

    [Fact]
    public void CurrencyConfiguration_ProtectsApiKeyWithoutReturningPlaintextStorage()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "splitbill-config-" + Guid.NewGuid().ToString("N"))));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var db = new ApplicationDbContext(options);
        var service = new CurrencyConfigurationService(db, provider);
        var protectedValue = service.ProtectApiKey("secret-value");

        Assert.NotEqual("secret-value", protectedValue);
        Assert.Equal("secret-value", service.UnprotectApiKey(protectedValue));
        Assert.Null(service.UnprotectApiKey("not-a-valid-protected-value"));
    }

    [Fact]
    public async Task FrankfurterProvider_ParsesCurrentV2CurrencyAndRateShapes()
    {
        using var client = new HttpClient(new StubHttpHandler(request =>
        {
            var json = request.RequestUri?.AbsolutePath == "/v2/currencies"
                ? """[{"iso_code":"IDR","name":"Indonesian Rupiah","symbol":"Rp"},{"iso_code":"USD","name":"US Dollar","symbol":"$"}]"""
                : """[{"date":"2026-09-12","base":"USD","quote":"IDR","rate":17570}]""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }));
        var provider = new FrankfurterRateProvider(new StubHttpClientFactory(client));

        var currencies = await provider.GetCurrenciesAsync("https://api.frankfurter.dev", CurrencyAuthenticationMode.None, null);
        var rate = await provider.GetRateAsync("https://api.frankfurter.dev", CurrencyAuthenticationMode.None, null, "USD", "IDR", new DateOnly(2026, 9, 12));

        Assert.Contains(currencies, currency => currency.Code == "IDR" && currency.Symbol == "Rp");
        Assert.Contains(currencies, currency => currency.Code == "USD");
        Assert.Equal(17570m, rate.Rate);
        Assert.Equal(new DateOnly(2026, 9, 12), rate.EffectiveDate);
    }

    [Fact]
    public void CurrencyTrend_UsesLatestSevenRatesAndComputesPreviousChangeInCSharp()
    {
        var now = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        var rows = Enumerable.Range(0, 8).Select(index => new CurrencyExchangeRate
        {
            BaseCurrencyCode = "USD",
            QuoteCurrencyCode = "IDR",
            Rate = 100m + index,
            EffectiveDate = new DateOnly(2026, 9, 4).AddDays(index),
            RetrievedAt = now.AddHours(-index)
        }).ToList();

        var result = new CurrencyTrendService().Build(rows, new Dictionary<string, int> { ["USD"] = 3 }, now).Single();

        Assert.Equal(7, result.PointCount);
        Assert.Equal(new DateOnly(2026, 9, 5), result.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 11), result.LatestDate);
        Assert.Equal(0.94m, result.ChangeFromPreviousPercent);
        Assert.Contains("100,", result.SvgPoints);
    }

    [Fact]
    public void DashboardCurrencySelection_EnforcesLimitAndKeepsPrimaryFirst()
    {
        var service = new DashboardCurrencySelectionService(new CurrencyCatalog());

        var result = service.Normalize("JPY,USD,EUR,SGD,GBP,XXX,IDR", "EUR", "IDR");

        Assert.Equal(4, result.CurrencyCodes.Count);
        Assert.Equal("EUR", result.PrimaryCurrencyCode);
        Assert.Equal("EUR", result.CurrencyCodes[0]);
        Assert.DoesNotContain("GBP", result.CurrencyCodes);
        Assert.DoesNotContain("IDR", result.CurrencyCodes);
        Assert.DoesNotContain("XXX", result.CurrencyCodes);
    }

    [Fact]
    public async Task CurrencyRateService_LoadsDashboardRateWithoutAnyTransactions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.CurrencyConfigurations.Add(new CurrencyConfiguration
        {
            Id = 1,
            DefaultCurrencyCode = "IDR",
            BaseUrl = "https://rates.example",
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        using var client = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"date":"2026-09-12","base":"USD","quote":"IDR","rate":17570}]""", Encoding.UTF8, "application/json")
        }));
        var provider = new FrankfurterRateProvider(new StubHttpClientFactory(client));
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "splitbill-rate-" + Guid.NewGuid().ToString("N"))));
        var service = new CurrencyRateService(db, provider, new CurrencyCatalog(), protection);

        var result = await service.GetRateAsync("USD", "IDR", new DateOnly(2026, 9, 12));

        Assert.NotNull(result);
        Assert.Equal(17570m, result.Rate);
        Assert.Empty(db.Transactions);
        Assert.Single(db.CurrencyExchangeRates);
    }

    [Fact]
    public async Task GuestAccessService_CreatesCopiesAndRevokesLink()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new ApplicationUser { Id = "u", UserName = "u" });
        await db.SaveChangesAsync();
        var tx = new BillTransaction { TransactionNumber = "TRX-GUEST", MerchantName = "Cafe", UploadedByUserId = "u", GrandTotal = 10, Subtotal = 10, CurrencyCode = "IDR", ReportingCurrencyCode = "IDR", Participants = [new TransactionParticipant { Name = "Guest", Amount = 10 }] };
        db.Transactions.Add(tx); await db.SaveChangesAsync();
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "splitbill-guest-" + Guid.NewGuid().ToString("N"))));
        var tokenService = new GuestAccessTokenService(provider);
        var service = new GuestAccessService(db, tokenService);
        await service.EnsureLinksAsync(tx, "u");
        var participantId = tx.Participants.Single().Id;
        var token = await service.GetCopyTokenAsync(participantId);
        Assert.NotNull(token);
        Assert.NotNull(await service.ResolveAsync(token!));
        Assert.True(await service.RevokeAsync(tx.Id, participantId));
        Assert.Null(await service.ResolveAsync(token!));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
