using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public interface IInstallationSetupService
{
    Task<InstallationState> EnsureStateAsync(ApplicationDbContext db, CancellationToken cancellationToken = default);
    bool VerifyBootstrapCode(InstallationState state, string? code);
    string? RevealBootstrapCode(InstallationState state);
}

/// <summary>
/// Owns first-run bootstrap state. The verifier is hashed; the protected copy is
/// only there for the explicit local setup command to print once.
/// </summary>
public sealed class InstallationSetupService(IDataProtectionProvider protectionProvider) : IInstallationSetupService
{
    private const string ProtectorPurpose = "SplitBill.InstallationBootstrapCode.v1";
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly IDataProtector protector = protectionProvider.CreateProtector(ProtectorPurpose);

    public async Task<InstallationState> EnsureStateAsync(ApplicationDbContext db, CancellationToken cancellationToken = default)
    {
        var state = await db.InstallationStates.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        var hasUsers = await db.Users.AnyAsync(cancellationToken);
        if (state is null)
        {
            state = new InstallationState { Id = 1, InstallationId = Guid.NewGuid().ToString("N") };
            db.InstallationStates.Add(state);
        }

        if (hasUsers && state.SetupCompletedAt is null)
        {
            state.SetupCompletedAt = DateTimeOffset.UtcNow;
            state.BootstrapCodeHash = null;
            state.ProtectedBootstrapCode = null;
            state.BootstrapCodeExpiresAt = null;
        }
        else if (!hasUsers && string.IsNullOrWhiteSpace(state.BootstrapCodeHash))
        {
            var code = GenerateCode();
            state.BootstrapCodeHash = Hash(code);
            state.ProtectedBootstrapCode = protector.Protect(code);
            state.BootstrapCodeExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        }

        await db.SaveChangesAsync(cancellationToken);
        return state;
    }

    public bool VerifyBootstrapCode(InstallationState state, string? code)
    {
        if (state.SetupCompletedAt is not null || string.IsNullOrWhiteSpace(code) ||
            state.BootstrapCodeExpiresAt is null || state.BootstrapCodeExpiresAt <= DateTimeOffset.UtcNow ||
            string.IsNullOrWhiteSpace(state.BootstrapCodeHash)) return false;
        var actual = Convert.FromHexString(Hash(code.Trim().ToUpperInvariant()));
        var expected = Convert.FromHexString(state.BootstrapCodeHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public string? RevealBootstrapCode(InstallationState state)
    {
        if (state.SetupCompletedAt is not null || string.IsNullOrWhiteSpace(state.ProtectedBootstrapCode)) return null;
        try { return protector.Unprotect(state.ProtectedBootstrapCode); }
        catch (CryptographicException) { return null; }
    }

    private static string GenerateCode()
    {
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        var chars = new char[random.Length];
        for (var index = 0; index < chars.Length; index++) chars[index] = Alphabet[random[index] % Alphabet.Length];
        return new string(chars);
    }

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant())));
}
