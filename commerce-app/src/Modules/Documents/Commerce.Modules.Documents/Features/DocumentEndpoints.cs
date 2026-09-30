// Document endpoints: customers upload attachments and read their own documents
// (including generated invoices); support staff may read any document, which is audited.
using System.Security.Cryptography;
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Storage;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Customers.Contracts;
using Commerce.Modules.Documents.Data;
using Commerce.Modules.Documents.Domain;
using Commerce.SharedKernel.Auditing;
using Commerce.SharedKernel.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Documents.Features;

public static class DocumentEndpoints
{
    public sealed record DocumentDto(Guid Id, Guid? OrderId, string Kind, string Status, string FileName, string ContentType, long SizeBytes, string? Sha256, DateTimeOffset CreatedAt)
    {
        public static DocumentDto From(Document d) => new(d.Id, d.OrderId, d.Kind.ToString(), d.Status.ToString(), d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.CreatedAt);
    }

    public static void Map(RouteGroupBuilder documents)
    {
        documents.MapPost("/", UploadAsync)
            .RequireAuthorization(Permissions.OrdersPlace)
            // Bearer-token API: no cookies are used, so cross-site request forgery does not apply.
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(UploadPolicy.MaxBytes + 64 * 1024))
            .WithName("UploadDocument");
        documents.MapGet("/mine", MineAsync).RequireAuthorization(Permissions.OrdersPlace).WithName("GetMyDocuments");
        documents.MapGet("/{id:guid}", GetAsync).WithName("GetDocument");
        documents.MapGet("/{id:guid}/content", DownloadAsync).WithName("DownloadDocument");
    }

    private static async Task<IResult> UploadAsync(
        IFormFile file, DocumentsDbContext db, ICustomerDirectory customers, ICurrentUser user, IObjectStorage storage, AuditTrail<DocumentsDbContext> audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var customer = await customers.FindBySubjectAsync(user.Subject!, cancellationToken);
        if (customer is null)
        {
            return Error.Conflict("documents.customer.unregistered", "Create a customer profile first.").ToProblem();
        }

        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        var check = UploadPolicy.Check(file.ContentType, bytes.LongLength, bytes.AsSpan(0, Math.Min(bytes.Length, 512)));
        if (check.IsFailure)
        {
            return check.Error.ToProblem();
        }

        var id = Guid.CreateVersion7();
        var key = $"attachments/{customer.CustomerId:N}/{id:N}";
        buffer.Position = 0;
        await storage.PutAsync(key, buffer, file.ContentType, cancellationToken);

        var document = Document.UploadedAttachment(id, customer.CustomerId, UploadPolicy.SanitiseFileName(file.FileName), file.ContentType,
            bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), key, clock.GetUtcNow());
        db.Documents.Add(document);
        audit.Record("documents.upload", "document", id.ToString(), details: new Dictionary<string, string> { ["sha256"] = document.Sha256! });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/documents/{id}", DocumentDto.From(document));
    }

    private static async Task<IResult> MineAsync(DocumentsDbContext db, ICustomerDirectory customers, ICurrentUser user, CancellationToken cancellationToken)
    {
        var customer = await customers.FindBySubjectAsync(user.Subject!, cancellationToken);
        if (customer is null)
        {
            return TypedResults.Ok(Array.Empty<DocumentDto>());
        }

        var documents = await db.Documents.AsNoTracking().Where(d => d.CustomerId == customer.CustomerId).OrderByDescending(d => d.CreatedAt).ToListAsync(cancellationToken);
        return TypedResults.Ok(documents.Select(DocumentDto.From).ToList());
    }

    private static async Task<IResult> GetAsync(Guid id, DocumentsDbContext db, ICustomerDirectory customers, ICurrentUser user, SecurityEventLog securityEvents, CancellationToken cancellationToken)
    {
        var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (document is null || !await CanAccessAsync(document, customers, user, cancellationToken))
        {
            if (document is not null)
            {
                securityEvents.AuthorizationDenied(user.Subject, $"document/{id}", "owner-or-documents:read-any");
            }

            return TypedResults.NotFound();
        }

        return TypedResults.Ok(DocumentDto.From(document));
    }

    private static async Task<IResult> DownloadAsync(
        Guid id, DocumentsDbContext db, ICustomerDirectory customers, ICurrentUser user, IObjectStorage storage, AuditTrail<DocumentsDbContext> audit, SecurityEventLog securityEvents, CancellationToken cancellationToken)
    {
        var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (document is null || document.Status != DocumentStatus.Available)
        {
            return TypedResults.NotFound();
        }

        var owner = await IsOwnerAsync(document, customers, user, cancellationToken);
        if (!owner && !user.HasPermission(Permissions.DocumentsReadAny))
        {
            securityEvents.AuthorizationDenied(user.Subject, $"document/{id}", "owner-or-documents:read-any");
            return TypedResults.NotFound();
        }

        if (!owner)
        {
            // Staff reading a customer's document is recorded as a security-relevant access.
            audit.Record("documents.download", "document", id.ToString(), AuditCategories.Security);
            await db.SaveChangesAsync(cancellationToken);
            securityEvents.PrivilegedOperation(user.Actor, "documents.download-other-customer", $"document/{id}");
        }

        var content = await storage.OpenReadAsync(document.StorageKey!, cancellationToken);
        // Always served as a download; browsers never render uploaded content inline.
        return TypedResults.File(content, "application/octet-stream", document.FileName);
    }

    private static async Task<bool> IsOwnerAsync(Document document, ICustomerDirectory customers, ICurrentUser user, CancellationToken cancellationToken)
    {
        if (user.Subject is null)
        {
            return false;
        }

        var customer = await customers.FindBySubjectAsync(user.Subject, cancellationToken);
        return customer?.CustomerId == document.CustomerId;
    }

    private static async Task<bool> CanAccessAsync(Document document, ICustomerDirectory customers, ICurrentUser user, CancellationToken cancellationToken) =>
        user.HasPermission(Permissions.DocumentsReadAny) || await IsOwnerAsync(document, customers, user, cancellationToken);
}
