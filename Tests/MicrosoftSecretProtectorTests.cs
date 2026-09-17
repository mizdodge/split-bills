using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Tests;

public sealed class MicrosoftSecretProtectorTests
{
    [Fact]
    public void DedicatedPurposeRoundTripsAndRejectsOtherPurpose()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new MicrosoftSecretProtector(provider);
        var ciphertext = protector.Protect("secret-value");
        Assert.NotEqual("secret-value", ciphertext);
        Assert.Equal("secret-value", protector.Unprotect(ciphertext));
        var other = provider.CreateProtector("other-purpose").Protect("secret-value");
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(other));
    }

    [Fact]
    public async Task LegacySharePointCredentialMigratesOnceAndEnablesSharedMode()
    {
        var provider = new EphemeralDataProtectionProvider();
        var legacyProtector = new SharePointSecretProtector(provider);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options);
        db.MicrosoftIntegrationConfigurations.Add(new MicrosoftIntegrationConfiguration());
        db.MicrosoftLoginConfigurations.Add(new MicrosoftLoginConfiguration());
        db.SharePointConfigurations.Add(new SharePointConfiguration
        {
            Id = 1, TenantId = Guid.NewGuid().ToString(), ClientId = Guid.NewGuid().ToString(),
            ProtectedClientSecret = legacyProtector.Protect("legacy-secret")
        });
        await db.SaveChangesAsync();

        var service = new MicrosoftIntegrationService(
            db, new MicrosoftSecretProtector(provider),
            new StubMicrosoftGraphIdentityService(),
            new HttpContextAccessor(), provider, NullLogger<MicrosoftIntegrationService>.Instance);
        var result = await service.MigrateLegacySharePointAsync();

        Assert.True(result.Succeeded);
        var shared = await db.MicrosoftIntegrationConfigurations.SingleAsync();
        var sharePoint = await db.SharePointConfigurations.SingleAsync();
        Assert.Equal("legacy-secret", new MicrosoftSecretProtector(provider).Unprotect(shared.ProtectedClientSecret));
        Assert.True(sharePoint.UseSharedMicrosoftCredentials);
        Assert.True(shared.LegacyMigrationCompleted);
    }

    private sealed class StubMicrosoftGraphIdentityService : IMicrosoftGraphIdentityService
    {
        public Task<MicrosoftConnectionResult> TestCredentialsAsync(string tenantId, string clientId, string clientSecret, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MicrosoftConnectionResult(true, null));

        public Task<MicrosoftGraphUser?> GetUserAsync(string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<MicrosoftGraphUser?>(null);
    }
}
