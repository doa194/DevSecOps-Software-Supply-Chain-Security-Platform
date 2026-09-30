// The S3 adapter against a real MinIO: bucket creation and a content round trip under a
// server-generated key.
using System.Text;
using Commerce.BuildingBlocks.Storage;
using Commerce.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Minio;

namespace Commerce.IntegrationTests.Storage;

public sealed class MinioFixture : IAsyncLifetime
{
    private readonly MinioContainer _minio = new MinioBuilder(TestImages.Minio)
        .WithUsername("integration")
        .WithPassword("integration-secret")
        .Build();

    public IObjectStorage Storage { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _minio.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ObjectStorage:Endpoint"] = _minio.GetConnectionString(),
            ["ObjectStorage:Bucket"] = "documents-it",
            ["ObjectStorage:AccessKey"] = "integration",
            ["ObjectStorage:SecretKey"] = "integration-secret",
        }).Build();
        Storage = new ServiceCollection().AddObjectStorage(configuration).BuildServiceProvider().GetRequiredService<IObjectStorage>();
        await Storage.EnsureBucketAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync() => await _minio.DisposeAsync();
}

public sealed class ObjectStorageTests(MinioFixture minio) : IClassFixture<MinioFixture>
{
    [Fact]
    public async Task Stored_content_is_read_back_unchanged()
    {
        var content = Encoding.UTF8.GetBytes("invoice body");
        using var upload = new MemoryStream(content);

        await minio.Storage.PutAsync("invoices/test/1.html", upload, "text/html", CancellationToken.None);
        await using var download = await minio.Storage.OpenReadAsync("invoices/test/1.html", CancellationToken.None);
        using var copy = new MemoryStream();
        await download.CopyToAsync(copy);

        Assert.Equal(content, copy.ToArray());
    }

    [Fact]
    public async Task Ensuring_the_bucket_twice_is_harmless() =>
        await minio.Storage.EnsureBucketAsync(CancellationToken.None);
}
