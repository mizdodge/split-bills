using Microsoft.AspNetCore.Http;

namespace Splitbill.Services;

public sealed record StoredPaymentProof(string FileName, string OriginalFileName, string ContentType);

public sealed class PaymentProofException(string message, string messageKey = "InvalidPaymentProof") : Exception(message)
{
    public string MessageKey { get; } = messageKey;
}

public interface IPaymentProofStorageService
{
    Task<StoredPaymentProof> StoreAsync(IFormFile file, CancellationToken cancellationToken = default);
    string GetPath(string storedFileName);
    void Delete(string? storedFileName);
}

public sealed class PaymentProofStorageService(IWebHostEnvironment environment, IUploadedImageProcessor? imageProcessor = null) : IPaymentProofStorageService
{
    public const long MaximumBytes = 10 * 1024 * 1024;
    public async Task<StoredPaymentProof> StoreAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        ProcessedImage processed;
        try
        {
            processed = await (imageProcessor ?? new UploadedImageProcessor()).ProcessAsync(file, cancellationToken);
        }
        catch (UploadedImageException exception)
        {
            var key = exception.MessageKey switch
            {
                "ImageRequired" => "InvalidPaymentProof",
                "ImageTooLarge" => "PaymentProofTooLarge",
                "ImageDimensionsInvalid" => "PaymentProofFormatInvalid",
                _ => "PaymentProofContentInvalid"
            };
            throw new PaymentProofException(exception.Message, key);
        }
        var bytes = processed.Bytes;
        var extension = processed.Extension;

        var directory = ProofDirectory;
        Directory.CreateDirectory(directory);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var temporaryName = $".{Guid.NewGuid():N}.tmp";
        var temporaryPath = Path.Combine(directory, temporaryName);
        var destinationPath = Path.Combine(directory, fileName);
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await output.WriteAsync(bytes.AsMemory(), cancellationToken);
            File.Move(temporaryPath, destinationPath);
            return new StoredPaymentProof(fileName, processed.OriginalFileName, processed.ContentType);
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(destinationPath);
            throw;
        }
    }

    public string GetPath(string storedFileName)
    {
        var safeName = Path.GetFileName(storedFileName);
        if (string.IsNullOrWhiteSpace(safeName) || !string.Equals(safeName, storedFileName, StringComparison.Ordinal))
            throw new ArgumentException("Invalid stored proof file name.", nameof(storedFileName));
        return Path.Combine(ProofDirectory, safeName);
    }

    public void Delete(string? storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName)) return;
        TryDelete(GetPath(storedFileName));
    }

    private string ProofDirectory => Path.Combine(environment.ContentRootPath, "App_Data", "payment-proofs");

    private static string SanitizeOriginalName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "proof").Trim();
        if (name.Length == 0) name = "proof";
        return name[..Math.Min(name.Length, 255)];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
