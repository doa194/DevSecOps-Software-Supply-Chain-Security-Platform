// S3-compatible object storage (MinIO) for documents. Objects are addressed by keys the
// server generates; client-supplied file names are metadata only and never become paths.
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Commerce.BuildingBlocks.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.BuildingBlocks.Storage;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";
    public string Endpoint { get; set; } = string.Empty;
    public string Bucket { get; set; } = "commerce-documents";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string? CaCertificatePath { get; set; }
}

public interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);
    Task EnsureBucketAsync(CancellationToken cancellationToken);
}

internal sealed class S3ObjectStorage(IAmazonS3 client, ObjectStorageOptions options) : IObjectStorage
{
    public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken) =>
        client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, cancellationToken);

    public async Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var response = await client.GetObjectAsync(options.Bucket, key, cancellationToken);
        return response.ResponseStream;
    }

    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        var buckets = await client.ListBucketsAsync(cancellationToken);
        if (buckets.Buckets?.Any(bucket => bucket.BucketName == options.Bucket) != true)
        {
            await client.PutBucketAsync(options.Bucket, cancellationToken);
        }
    }
}

public static class ObjectStorageRegistration
{
    public static IServiceCollection AddObjectStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ObjectStorageOptions.SectionName).Get<ObjectStorageOptions>() ?? new ObjectStorageOptions();
        services.AddSingleton(options);
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = options.Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                HttpClientFactory = new LocalCaHttpClientFactory(options.CaCertificatePath),
            }));
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        return services;
    }

    private sealed class LocalCaHttpClientFactory(string? caPath) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(LocalCaTrust.CreateHandler(caPath));
    }
}
