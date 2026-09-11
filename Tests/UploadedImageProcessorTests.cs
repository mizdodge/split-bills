using ImageMagick;
using Microsoft.AspNetCore.Http;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class UploadedImageProcessorTests
{
    [Theory]
    [InlineData(MagickFormat.Jpeg, "photo.jpeg", "image/jpeg")]
    [InlineData(MagickFormat.Png, "photo.png", "image/png")]
    [InlineData(MagickFormat.WebP, "photo.webp", "image/webp")]
    [InlineData(MagickFormat.Gif, "photo.gif", "image/gif")]
    [InlineData(MagickFormat.Bmp, "photo.bmp", "image/bmp")]
    [InlineData(MagickFormat.Tiff, "photo.tiff", "image/tiff")]
    public async Task CommonRasterFormatsAreNormalizedToStaticJpeg(MagickFormat format, string fileName, string contentType)
    {
        using var source = new MagickImage(MagickColors.CornflowerBlue, 2, 2);
        using var encoded = new MemoryStream();
        source.Write(encoded, format);
        encoded.Position = 0;
        var formFile = new FormFile(encoded, 0, encoded.Length, "image", fileName)
        {
            Headers = new HeaderDictionary(), ContentType = contentType
        };

        var processed = await new UploadedImageProcessor().ProcessAsync(formFile);

        Assert.Equal("image/jpeg", processed.ContentType);
        Assert.Equal(".jpg", processed.Extension);
        Assert.Equal(2, processed.PixelWidth);
        Assert.Equal(2, processed.PixelHeight);
        using var decoded = new MagickImage(processed.Bytes);
        Assert.Equal(MagickFormat.Jpeg, decoded.Format);
    }

    [Fact]
    public async Task DecoderDoesNotTrustFilenameOrContentType()
    {
        using var source = new MagickImage(MagickColors.White, 1, 1);
        using var encoded = new MemoryStream();
        source.Write(encoded, MagickFormat.Png);
        encoded.Position = 0;
        var formFile = new FormFile(encoded, 0, encoded.Length, "image", "looks-like-a-pdf.pdf")
        {
            Headers = new HeaderDictionary(), ContentType = "application/octet-stream"
        };

        var processed = await new UploadedImageProcessor().ProcessAsync(formFile);

        Assert.Equal("image/jpeg", processed.ContentType);
        Assert.Equal("looks-like-a-pdf.pdf", processed.OriginalFileName);
        Assert.Equal("Png", processed.OriginalFormat, ignoreCase: true);
    }

    [Fact]
    public async Task RejectsPdfEvenWhenItHasAnImageMimeHint()
    {
        await using var stream = new MemoryStream("%PDF-1.7"u8.ToArray());
        var formFile = new FormFile(stream, 0, stream.Length, "image", "receipt.pdf")
        {
            Headers = new HeaderDictionary(), ContentType = "image/png"
        };

        var error = await Assert.ThrowsAsync<UploadedImageException>(() => new UploadedImageProcessor().ProcessAsync(formFile));

        Assert.Equal("ImageFormatInvalid", error.MessageKey);
    }
}
