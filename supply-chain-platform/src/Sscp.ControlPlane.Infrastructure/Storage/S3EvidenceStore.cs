// Raw evidence in the MinIO `evidence` bucket.
//
// The bucket has object locking with a default GOVERNANCE retention (set up by the
// bootstrap), and the Control Plane's MinIO user has no delete permission, so a stored
// report cannot be removed or overwritten with the Control Plane's own credentials. The
// SHA-256 recorded with each evidence record is computed here from the bytes received.
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Sscp.ControlPlane.Application;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.Infrastructure.Storage;

public sealed class EvidenceStoreOptions
{
    public const string SectionName = "EvidenceStore";
    public string Endpoint { get; set; } = string.Empty;
    public string Bucket { get; set; } = "evidence";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string? CaCertificatePath { get; set; }
}

public sealed class S3EvidenceStore(IAmazonS3 s3, EvidenceStoreOptions options) : IEvidenceStore
{
    public async Task<RawReport> StoreAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        using var stream = new MemoryStream(content, writable: false);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            // MinIO verifies the upload against this checksum.
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(sha256)),
        }, cancellationToken);
        return new RawReport(key, sha256, content.LongLength, contentType);
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await s3.GetObjectAsync(options.Bucket, key, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
