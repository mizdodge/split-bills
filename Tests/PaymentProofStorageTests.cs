using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class PaymentProofStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Splitbill-proof-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StoresValidPngWithOpaqueNameAndDeletesIt()
    {
        var storage = new PaymentProofStorageService(new TestEnvironment { ContentRootPath = root });
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await using var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, stream.Length, "proof", "my-proof.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
        var stored = await storage.StoreAsync(file);
        Assert.NotEqual("my-proof.png", stored.FileName);
        Assert.True(File.Exists(storage.GetPath(stored.FileName)));
        storage.Delete(stored.FileName);
        Assert.False(File.Exists(Path.Combine(root, "App_Data", "payment-proofs", stored.FileName)));
    }

    [Fact]
    public async Task RejectsImageWithMismatchedContent()
    {
        var storage = new PaymentProofStorageService(new TestEnvironment { ContentRootPath = root });
        await using var stream = new MemoryStream("not an image"u8.ToArray());
        var file = new FormFile(stream, 0, stream.Length, "proof", "proof.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
        var error = await Assert.ThrowsAsync<PaymentProofException>(() => storage.StoreAsync(file));
        Assert.Equal("PaymentProofContentInvalid", error.MessageKey);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

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
