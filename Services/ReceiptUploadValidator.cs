using Microsoft.AspNetCore.Http;

namespace Splitbill.Services;

public static class ReceiptUploadValidator
{
    public const int MaximumImageCount = 5;
    public const long MaximumImageBytes = 10_000_000;
    public const long MaximumTotalBytes = 30_000_000;
    public static string? Validate(IReadOnlyCollection<IFormFile> images)
    {
        if (images.Count == 0 || images.All(image => image.Length == 0))
            return "Pilih minimal satu foto struk terlebih dahulu.";
        if (images.Count > MaximumImageCount)
            return $"Maksimal {MaximumImageCount} foto untuk satu struk.";
        if (images.Any(image => image.Length == 0))
            return "Semua foto harus berisi data.";
        if (images.Any(image => image.Length > MaximumImageBytes))
            return "Ukuran setiap foto maksimal 10 MB.";
        if (images.Sum(image => image.Length) > MaximumTotalBytes)
            return "Total ukuran seluruh foto maksimal 30 MB.";
        return null;
    }
}
