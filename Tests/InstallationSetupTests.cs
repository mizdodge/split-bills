using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class InstallationSetupTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private readonly InstallationSetupService setup = new(new EphemeralDataProtectionProvider());

    public InstallationSetupTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task FreshInstallationGetsOneTimeBootstrapCode()
    {
        var state = await setup.EnsureStateAsync(db);

        Assert.NotEqual(Guid.Empty.ToString("N"), state.InstallationId);
        Assert.Null(state.SetupCompletedAt);
        Assert.NotNull(setup.RevealBootstrapCode(state));
        Assert.True(setup.VerifyBootstrapCode(state, setup.RevealBootstrapCode(state)));
        Assert.False(setup.VerifyBootstrapCode(state, "WRONG-CODE"));
    }

    [Fact]
    public async Task ExistingUsersCompleteSetupStateWithoutRegeneratingCode()
    {
        db.Users.Add(new Models.ApplicationUser { Id = "existing", UserName = "existing" });
        await db.SaveChangesAsync();

        var state = await setup.EnsureStateAsync(db);

        Assert.NotNull(state.SetupCompletedAt);
        Assert.Null(state.BootstrapCodeHash);
        Assert.Null(setup.RevealBootstrapCode(state));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
