// Document metadata. The content lives in object storage under a server-generated key;
// this record holds who owns it, what it is, its SHA-256 and whether it is ready.
using Commerce.SharedKernel.Domain;
using Commerce.SharedKernel.Results;

namespace Commerce.Modules.Documents.Domain;

public enum DocumentKind
{
    Invoice,
    Attachment,
}

public enum DocumentStatus
{
    Requested,
    Available,
    Failed,
}

public sealed class Document : AggregateRoot<Guid>
{
    private Document() { }

    public Guid CustomerId { get; private set; }
    public Guid? OrderId { get; private set; }
    public DocumentKind Kind { get; private set; }
    public DocumentStatus Status { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public string? Sha256 { get; private set; }
    public string? StorageKey { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Document RequestInvoice(Guid customerId, Guid orderId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        CustomerId = customerId,
        OrderId = orderId,
        Kind = DocumentKind.Invoice,
        Status = DocumentStatus.Requested,
        FileName = $"invoice-{orderId:N}.html",
        ContentType = "text/html",
        CreatedAt = now,
    };

    public static Document UploadedAttachment(Guid id, Guid customerId, string fileName, string contentType, long sizeBytes, string sha256, string storageKey, DateTimeOffset now) => new()
    {
        Id = id,
        CustomerId = customerId,
        Kind = DocumentKind.Attachment,
        Status = DocumentStatus.Available,
        FileName = fileName,
        ContentType = contentType,
        SizeBytes = sizeBytes,
        Sha256 = sha256,
        StorageKey = storageKey,
        CreatedAt = now,
    };

    public Result MarkGenerated(string storageKey, string sha256, long sizeBytes, string contentType)
    {
        if (Status != DocumentStatus.Requested)
        {
            return Error.Conflict("documents.state.invalid", $"Document is {Status}.");
        }

        StorageKey = storageKey;
        Sha256 = sha256;
        SizeBytes = sizeBytes;
        ContentType = contentType;
        Status = DocumentStatus.Available;
        Touch();
        return Result.Success();
    }

    public Result MarkFailed(string reason)
    {
        if (Status != DocumentStatus.Requested)
        {
            return Error.Conflict("documents.state.invalid", $"Document is {Status}.");
        }

        FailureReason = reason;
        Status = DocumentStatus.Failed;
        Touch();
        return Result.Success();
    }
}

// Upload rules: size limit, an allow-list of types, and a check that the file's first bytes
// really match the declared type (a script renamed to .pdf is rejected).
public static class UploadPolicy
{
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly Dictionary<string, byte[][]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = [[0x25, 0x50, 0x44, 0x46]], // %PDF
        ["image/png"] = [[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]],
        ["image/jpeg"] = [[0xFF, 0xD8, 0xFF]],
        ["text/plain"] = [],
    };

    public static Result Check(string contentType, long sizeBytes, ReadOnlySpan<byte> header)
    {
        if (sizeBytes is <= 0 or > MaxBytes)
        {
            return Error.Validation("documents.upload.size", $"Files must be between 1 byte and {MaxBytes / 1024 / 1024} MiB.");
        }

        if (!Signatures.TryGetValue(contentType, out var signatures))
        {
            return Error.Validation("documents.upload.type", "Only PDF, PNG, JPEG and plain text files are accepted.");
        }

        if (signatures.Length == 0)
        {
            // Plain text: reject control characters other than tab/newline, which rules out
            // binaries and most executables disguised as text.
            foreach (var value in header)
            {
                if (value < 0x09 || value is > 0x0D and < 0x20)
                {
                    return Error.Validation("documents.upload.content", "The file content does not match its declared type.");
                }
            }

            return Result.Success();
        }

        foreach (var signature in signatures)
        {
            if (header.Length >= signature.Length && header[..signature.Length].SequenceEqual(signature))
            {
                return Result.Success();
            }
        }

        return Error.Validation("documents.upload.content", "The file content does not match its declared type.");
    }

    // Keeps only a safe display name; it is metadata and never used as a storage path.
    public static string SanitiseFileName(string? name)
    {
        // Split on both separators explicitly: Path.GetFileName ignores '\' on Linux, where
        // the service runs, so a Windows-style path would otherwise survive.
        var baseName = (name ?? string.Empty).Split('/', '\\')[^1];
        var safe = new string(baseName.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return string.IsNullOrEmpty(safe) ? "upload" : safe[..Math.Min(safe.Length, 100)];
    }
}
