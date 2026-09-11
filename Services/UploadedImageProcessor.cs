using ImageMagick;
using Microsoft.AspNetCore.Http;

namespace Splitbill.Services;

public sealed record ProcessedImage(
    byte[] Bytes,
    string ContentType,
    string Extension,
    string OriginalFileName,
    string OriginalFormat,
    int PixelWidth,
    int PixelHeight);

public sealed class UploadedImageException(string message, string messageKey = "ImageFormatInvalid") : Exception(message)
{
    public string MessageKey { get; } = messageKey;
}

public interface IUploadedImageProcessor
{
    Task<ProcessedImage> ProcessAsync(IFormFile file, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads uploaded raster images through ImageMagick, applies bounded resource
/// checks and emits a static, metadata-free image that is safe for viewers and AI.
/// </summary>
public sealed class UploadedImageProcessor : IUploadedImageProcessor
{
    public const long MaximumBytes = 10 * 1024 * 1024;
    public const long MaximumPixels = 40_000_000;
    public const int MaximumDimension = 12_000;
    private static readonly HashSet<string> RejectedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "SVG", "SVGZ", "PDF", "PS", "EPS", "PSD", "XCF", "CR2", "NEF", "ARW", "DNG", "ICO", "CUR"
    };

    public async Task<ProcessedImage> ProcessAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length <= 0)
            throw new UploadedImageException("File gambar wajib diunggah.", "ImageRequired");
        if (file.Length > MaximumBytes)
            throw new UploadedImageException("Ukuran gambar maksimal 10 MB.", "ImageTooLarge");

        await using var input = file.OpenReadStream();
        await using var buffer = new MemoryStream(checked((int)Math.Min(file.Length + 1, MaximumBytes + 1)));
        var copyBuffer = new byte[81920];
        var total = 0L;
        int read;
        while ((read = await input.ReadAsync(copyBuffer.AsMemory(), cancellationToken)) > 0)
        {
            total += read;
            if (total > MaximumBytes)
                throw new UploadedImageException("Ukuran gambar maksimal 10 MB.", "ImageTooLarge");
            await buffer.WriteAsync(copyBuffer.AsMemory(0, read), cancellationToken);
        }

        var bytes = buffer.ToArray();
        try
        {
            using var image = new MagickImage(bytes);
            var format = image.Format.ToString();
            if (RejectedFormats.Contains(format))
                throw new UploadedImageException("Format file bukan foto raster yang didukung.", "ImageFormatInvalid");
            if (image.Width == 0 || image.Height == 0 || image.Width > MaximumDimension || image.Height > MaximumDimension ||
                (long)image.Width * image.Height > MaximumPixels)
                throw new UploadedImageException("Resolusi gambar terlalu besar.", "ImageDimensionsInvalid");

            var originalFileName = SanitizeOriginalName(file.FileName);
            image.AutoOrient();
            image.Strip();
            image.ColorSpace = ColorSpace.sRGB;
            image.BackgroundColor = MagickColors.White;
            if (image.HasAlpha)
                image.Alpha(AlphaOption.Remove);
            image.Quality = 92;

            using var output = new MemoryStream();
            image.Write(output, MagickFormat.Jpeg);
            if (output.Length == 0 || output.Length > MaximumBytes)
                throw new UploadedImageException("Gambar hasil normalisasi terlalu besar.", "ImageTooLarge");

            return new ProcessedImage(output.ToArray(), "image/jpeg", ".jpg", originalFileName, format,
                checked((int)image.Width), checked((int)image.Height));
        }
        catch (UploadedImageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is MagickException or InvalidOperationException or ArgumentException)
        {
            throw new UploadedImageException("File bukan gambar yang valid atau formatnya belum didukung.", "ImageFormatInvalid");
        }
    }

    private static string SanitizeOriginalName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "image").Trim();
        if (name.Length == 0) name = "image";
        return name[..Math.Min(name.Length, 255)];
    }
}
