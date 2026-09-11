using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record BackupPackage(string PhysicalPath, string DownloadName);
public sealed record RestoreStagingResult(string PhysicalPath, string PackageName, int FileCount, long Bytes);

public interface IBackupService
{
    Task<BackupPackage> CreateAsync(CancellationToken cancellationToken = default);
    Task<RestoreStagingResult> StageAsync(IFormFile file, CancellationToken cancellationToken = default);
    Task ResetMachineSecretsAsync(CancellationToken cancellationToken = default);
    void DeleteTemporary(string? path);
}

public sealed class BackupService(IWebHostEnvironment environment, IConfiguration configuration, ApplicationDbContext db) : IBackupService
{
    private const long MaximumRestoreBytes = 512L * 1024 * 1024;
    private const long MaximumUncompressedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<BackupPackage> CreateAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(environment.ContentRootPath, "App_Data", "backups");
        Directory.CreateDirectory(root);
        var work = Path.Combine(root, $".backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var databaseCopy = Path.Combine(work, "splitbill.db");
        try
        {
            await SnapshotDatabaseAsync(databaseCopy, cancellationToken);
            var payloads = new List<BackupEntry>();
            AddPayload(payloads, databaseCopy, "database/splitbill.db");
            AddDirectoryPayload(payloads, Path.Combine(environment.ContentRootPath, "App_Data", "receipts"), "files/receipts");
            AddDirectoryPayload(payloads, Path.Combine(environment.ContentRootPath, "App_Data", "payment-proofs"), "files/payment-proofs");
            AddDirectoryPayload(payloads, Path.Combine(environment.ContentRootPath, "App_Data", "data-protection-keys"), "keys/data-protection-keys");

            var manifest = new BackupManifest
            {
                FormatVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                InstallationId = await db.InstallationStates.AsNoTracking().Where(x => x.Id == 1).Select(x => x.InstallationId).SingleOrDefaultAsync(cancellationToken) ?? string.Empty,
                Entries = payloads.Select(x => new BackupManifestEntry { Path = x.ArchivePath, Length = x.Length, Sha256 = x.Sha256 }).ToList()
            };
            var outputPath = Path.Combine(root, $"SplitBill-Backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var payload in payloads) AddArchiveEntry(archive, payload);
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);
            }
            return new BackupPackage(outputPath, Path.GetFileName(outputPath));
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    public async Task<RestoreStagingResult> StageAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length <= 0) throw new InvalidDataException("Backup file is required.");
        if (file.Length > MaximumRestoreBytes) throw new InvalidDataException("Backup package is too large.");
        var directory = Path.Combine(environment.ContentRootPath, "App_Data", "restore-staging");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.zip");
        try
        {
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                var total = 0L;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaximumRestoreBytes) throw new InvalidDataException("Backup package is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            var manifest = await ValidatePackageAsync(path, cancellationToken);
            return new RestoreStagingResult(path, Path.GetFileName(file.FileName), manifest.Entries.Count, new FileInfo(path).Length);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public async Task ResetMachineSecretsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var item in await db.AiConfigurations.ToListAsync(cancellationToken))
        {
            item.ProtectedApiKey = null;
            item.IsActive = false;
        }
        foreach (var item in await db.SharePointConfigurations.ToListAsync(cancellationToken))
        {
            item.Enabled = false;
            item.ProtectedClientSecret = string.Empty;
            item.SiteId = string.Empty;
            item.ListId = string.Empty;
            item.LastTestSucceeded = null;
            item.LastError = "Migration restore requires SharePoint credentials to be entered again.";
        }
        foreach (var item in await db.WebPushConfigurations.ToListAsync(cancellationToken))
        {
            item.Enabled = false;
            item.ProtectedPrivateKey = string.Empty;
            item.PublicKey = string.Empty;
        }
        db.WebPushSubscriptions.RemoveRange(await db.WebPushSubscriptions.ToListAsync(cancellationToken));
        db.WebPushDeliveries.RemoveRange(await db.WebPushDeliveries.ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    public void DeleteTemporary(string? path) => TryDelete(path);

    private async Task SnapshotDatabaseAsync(string destination, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=App_Data/splitbill.db";
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:")
            throw new InvalidOperationException("A file-backed SQLite database is required for backup.");
        var source = Path.IsPathRooted(builder.DataSource) ? builder.DataSource : Path.GetFullPath(Path.Combine(environment.ContentRootPath, builder.DataSource));
        if (!File.Exists(source)) throw new FileNotFoundException("SQLite database was not found.", source);
        var escaped = destination.Replace("'", "''", StringComparison.Ordinal);
        builder.DataSource = source;
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"VACUUM INTO '{escaped}';";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<BackupManifest> ValidatePackageAsync(string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Backup manifest is missing.");
        BackupManifest? manifest;
        await using (var stream = manifestEntry.Open()) manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken);
        if (manifest is null || manifest.FormatVersion != 1 || manifest.Entries is null || manifest.Entries.Count == 0) throw new InvalidDataException("Backup manifest is invalid.");
        long total = 0;
        foreach (var item in manifest.Entries)
        {
            if (!IsSafeArchivePath(item.Path) || item.Length < 0 || item.Length > MaximumUncompressedBytes || total > MaximumUncompressedBytes - item.Length)
                throw new InvalidDataException("Backup manifest contains an unsafe or oversized entry.");
            var entry = archive.GetEntry(item.Path) ?? throw new InvalidDataException($"Backup entry is missing: {item.Path}");
            if (entry.Length != item.Length) throw new InvalidDataException($"Backup entry length mismatch: {item.Path}");
            await using var stream = entry.Open();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Backup checksum mismatch: {item.Path}");
            total += item.Length;
        }
        return manifest;
    }

    private static bool IsSafeArchivePath(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) &&
        !path.Contains("..", StringComparison.Ordinal) && path is not "manifest.json";

    private static void AddDirectoryPayload(List<BackupEntry> payloads, string directory, string archivePrefix)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            AddPayload(payloads, file, $"{archivePrefix}/{Path.GetRelativePath(directory, file).Replace('\\', '/')}");
    }

    private static void AddPayload(List<BackupEntry> payloads, string physicalPath, string archivePath)
    {
        var info = new FileInfo(physicalPath);
        payloads.Add(new BackupEntry(physicalPath, archivePath, info.Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(physicalPath)))));
    }

    private static void AddArchiveEntry(ZipArchive archive, BackupEntry payload)
    {
        var entry = archive.CreateEntry(payload.ArchivePath, CompressionLevel.Fastest);
        using var source = File.OpenRead(payload.PhysicalPath);
        using var target = entry.Open();
        source.CopyTo(target);
    }

    private static void TryDelete(string? path) { try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private sealed record BackupEntry(string PhysicalPath, string ArchivePath, long Length, string Sha256);
    private sealed class BackupManifest { public int FormatVersion { get; set; } public DateTimeOffset CreatedAt { get; set; } public string InstallationId { get; set; } = string.Empty; public List<BackupManifestEntry> Entries { get; set; } = []; }
    private sealed class BackupManifestEntry { public string Path { get; set; } = string.Empty; public long Length { get; set; } public string Sha256 { get; set; } = string.Empty; }
}
