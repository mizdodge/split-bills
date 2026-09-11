using System.IO.Compression;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Splitbill.Data;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Splitbill-backup-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;

    public BackupServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "App_Data", "receipts"));
        Directory.CreateDirectory(Path.Combine(root, "App_Data", "payment-proofs"));
        Directory.CreateDirectory(Path.Combine(root, "App_Data", "data-protection-keys"));
        connection = new SqliteConnection($"Data Source={Path.Combine(root, "App_Data", "splitbill.db")}");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.InstallationStates.Add(new Models.InstallationState { Id = 1, InstallationId = "test-installation" });
        db.SaveChanges();
        File.WriteAllBytes(Path.Combine(root, "App_Data", "receipts", "receipt.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "App_Data", "payment-proofs", "proof.jpg"), [4, 5, 6]);
        File.WriteAllText(Path.Combine(root, "App_Data", "data-protection-keys", "key.xml"), "key");
    }

    [Fact]
    public async Task BackupContainsManifestDatabaseProtectedFilesAndCanBeStaged()
    {
        var service = Service();
        var package = await service.CreateAsync();
        Assert.True(File.Exists(package.PhysicalPath));
        using (var archive = ZipFile.OpenRead(package.PhysicalPath))
        {
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("database/splitbill.db"));
            Assert.NotNull(archive.GetEntry("files/receipts/receipt.jpg"));
            Assert.NotNull(archive.GetEntry("files/payment-proofs/proof.jpg"));
            Assert.NotNull(archive.GetEntry("keys/data-protection-keys/key.xml"));
        }

        await using var stream = File.OpenRead(package.PhysicalPath);
        var upload = new FormFile(stream, 0, stream.Length, "backup", "backup.zip") { Headers = new HeaderDictionary(), ContentType = "application/zip" };
        var staged = await service.StageAsync(upload);
        Assert.True(File.Exists(staged.PhysicalPath));
        service.DeleteTemporary(package.PhysicalPath);
        service.DeleteTemporary(staged.PhysicalPath);
    }

    [Fact]
    public async Task MigrationResetClearsMachineBoundConfigurationAndPushMaterial()
    {
        db.AiConfigurations.Add(new Models.AiConfiguration { ProtectedApiKey = "cipher", Model = "model" });
        db.SharePointConfigurations.Add(new Models.SharePointConfiguration { ProtectedClientSecret = "cipher", Enabled = true });
        db.WebPushConfigurations.Add(new Models.WebPushConfiguration { Id = 1, ProtectedPrivateKey = "cipher", PublicKey = "key" });
        await db.SaveChangesAsync();
        var service = Service();

        await service.ResetMachineSecretsAsync();

        Assert.Null((await db.AiConfigurations.SingleAsync()).ProtectedApiKey);
        Assert.False((await db.AiConfigurations.SingleAsync()).IsActive);
        var sharePoint = await db.SharePointConfigurations.SingleAsync();
        Assert.False(sharePoint.Enabled);
        Assert.Empty(sharePoint.ProtectedClientSecret);
        var push = await db.WebPushConfigurations.SingleAsync();
        Assert.False(push.Enabled);
        Assert.Empty(push.ProtectedPrivateKey);
        Assert.Empty(await db.WebPushSubscriptions.ToListAsync());
    }

    private BackupService Service()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={Path.Combine(root, "App_Data", "splitbill.db")}"
        }).Build();
        return new BackupService(new TestEnvironment { ContentRootPath = root }, config, db);
    }

    public void Dispose()
    {
        db.Database.CloseConnection();
        db.Dispose();
        connection.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Splitbill";
        public string EnvironmentName { get; set; } = "Testing";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
