using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Tests;

public sealed class PasswordChangeTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private readonly UserManager<ApplicationUser> users;

    public PasswordChangeTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        users = new UserManager<ApplicationUser>(new UserStore<ApplicationUser>(db),
            Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(), [], [],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!, NullLogger<UserManager<ApplicationUser>>.Instance);
    }

    [Fact]
    public async Task ChangePassword_RejectsWrongCurrentPassword()
    {
        var user = await CreateUser("member", "old123");
        var result = await users.ChangePasswordAsync(user, "wrong123", "new123");

        Assert.False(result.Succeeded);
        Assert.True((await users.CheckPasswordAsync(user, "old123")));
        Assert.False(await users.CheckPasswordAsync(user, "new123"));
    }

    [Fact]
    public async Task ChangePassword_ReplacesHashAndKeepsUser()
    {
        var user = await CreateUser("member", "old123");
        var result = await users.ChangePasswordAsync(user, "old123", "new123");

        Assert.True(result.Succeeded);
        Assert.True(await users.CheckPasswordAsync(user, "new123"));
        Assert.False(await users.CheckPasswordAsync(user, "old123"));
        Assert.NotNull(await users.FindByNameAsync("member"));
    }

    private async Task<ApplicationUser> CreateUser(string username, string password)
    {
        var user = new ApplicationUser { UserName = username, DisplayName = username, EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));
        return user;
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
