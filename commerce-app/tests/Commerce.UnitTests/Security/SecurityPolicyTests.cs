// Security policies that are pure logic: who may do what, role-change rules, upload
// validation, the audit hash chain and HTML encoding of generated documents.
using System.Text;
using Commerce.BuildingBlocks.Security;
using Commerce.Modules.Documents.Contracts;
using Commerce.Modules.Documents.Domain;
using Commerce.Modules.Identity.Domain;
using Commerce.SharedKernel.Results;
using Commerce.Workers.Audit;
using Commerce.Workers.Documents;

namespace Commerce.UnitTests.Security;

public sealed class PermissionMapTests
{
    [Fact]
    public void Only_finance_may_refund_not_even_administrators()
    {
        Assert.True(PermissionMap.Grants([Roles.Finance], Permissions.PaymentsRefund));
        Assert.False(PermissionMap.Grants([Roles.Admin], Permissions.PaymentsRefund));
    }

    [Fact]
    public void Customers_may_place_orders_but_not_read_other_peoples_orders()
    {
        Assert.True(PermissionMap.Grants([Roles.Customer], Permissions.OrdersPlace));
        Assert.False(PermissionMap.Grants([Roles.Customer], Permissions.OrdersReadAny));
    }

    [Fact]
    public void Support_agents_read_customers_but_see_personal_data_masked()
    {
        Assert.True(PermissionMap.Grants([Roles.SupportAgent], Permissions.CustomersReadAny));
        Assert.False(PermissionMap.Grants([Roles.SupportAgent], Permissions.PersonalDataRead));
    }

    [Fact]
    public void Unknown_permissions_are_never_granted() =>
        Assert.False(PermissionMap.Grants(Roles.All, "made:up"));
}

public sealed class RoleChangePolicyTests
{
    [Fact]
    public void Nobody_may_change_their_own_roles()
    {
        var result = RoleChangePolicy.Evaluate("ada", "ada", Roles.All, [Roles.Admin], [Roles.Admin, Roles.Finance]);

        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public void Unknown_roles_are_rejected()
    {
        var result = RoleChangePolicy.Evaluate("ada", "dave", Roles.All, [], ["superuser"]);

        Assert.Equal("identity.roles.unknown", result.Error.Code);
    }

    [Fact]
    public void The_plan_contains_only_the_differences_and_ignores_keycloak_internal_roles()
    {
        var result = RoleChangePolicy.Evaluate("ada", "dave", Roles.All, [Roles.Customer, "offline_access"], [Roles.SupportAgent]);

        Assert.Equal([Roles.SupportAgent], result.Value.Add);
        Assert.Equal([Roles.Customer], result.Value.Remove);
    }
}

public sealed class UploadPolicyTests
{
    [Fact]
    public void A_real_pdf_is_accepted() =>
        Assert.True(UploadPolicy.Check("application/pdf", 100, "%PDF-1.7"u8).IsSuccess);

    [Fact]
    public void A_script_declared_as_pdf_is_rejected() =>
        Assert.Equal("documents.upload.content", UploadPolicy.Check("application/pdf", 100, "#!/bin/sh\nrm -rf /"u8).Error.Code);

    [Fact]
    public void Binary_content_declared_as_text_is_rejected() =>
        Assert.True(UploadPolicy.Check("text/plain", 4, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }).IsFailure);

    [Theory]
    [InlineData("application/x-msdownload")]
    [InlineData("text/html")]
    public void Types_outside_the_allow_list_are_rejected(string contentType) =>
        Assert.Equal("documents.upload.type", UploadPolicy.Check(contentType, 10, "hello"u8).Error.Code);

    [Fact]
    public void Oversized_files_are_rejected() =>
        Assert.Equal("documents.upload.size", UploadPolicy.Check("text/plain", UploadPolicy.MaxBytes + 1, "a"u8).Error.Code);

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\evil.exe", "evil.exe")]
    [InlineData("<script>.pdf", "script.pdf")]
    [InlineData("", "upload")]
    public void File_names_are_reduced_to_a_safe_display_name(string input, string expected) =>
        Assert.Equal(expected, UploadPolicy.SanitiseFileName(input));
}

public sealed class AuditHashChainTests
{
    private static List<AuditEntry> Chain(int length)
    {
        var entries = new List<AuditEntry>();
        var previous = AuditHashChain.Genesis;
        for (var i = 1; i <= length; i++)
        {
            var occurredAt = AuditHashChain.ToStoragePrecision(DateTimeOffset.UnixEpoch.AddTicks(1234567 * i));
            var hash = AuditHashChain.Compute(previous, Guid.Empty, occurredAt, "commerce-api", "ada", "orders.cancel", "business", "order", $"{i}", "succeeded", "{}", null);
            entries.Add(new AuditEntry
            {
                Sequence = i, EventId = Guid.Empty, OccurredAt = occurredAt, Source = "commerce-api", Actor = "ada", Action = "orders.cancel",
                Category = "business", ResourceType = "order", ResourceId = $"{i}", Outcome = "succeeded", Details = "{}", PreviousHash = previous, Hash = hash,
            });
            previous = hash;
        }

        return entries;
    }

    [Fact]
    public void An_untouched_chain_verifies()
    {
        var result = AuditHashChain.Verify(Chain(5));

        Assert.True(result.Intact);
        Assert.Equal(5, result.EntriesChecked);
    }

    [Fact]
    public void A_modified_entry_is_detected()
    {
        var chain = Chain(5);
        var original = chain[2];
        chain[2] = new AuditEntry
        {
            Sequence = original.Sequence, EventId = original.EventId, OccurredAt = original.OccurredAt, Source = original.Source,
            Actor = "someone-else", Action = original.Action, Category = original.Category, ResourceType = original.ResourceType,
            ResourceId = original.ResourceId, Outcome = original.Outcome, Details = original.Details, PreviousHash = original.PreviousHash, Hash = original.Hash,
        };

        var result = AuditHashChain.Verify(chain);

        Assert.False(result.Intact);
        Assert.Equal(3, result.FirstBrokenSequence);
    }

    [Fact]
    public void A_deleted_entry_is_detected()
    {
        var chain = Chain(5);
        chain.RemoveAt(1);

        Assert.Equal(3, AuditHashChain.Verify(chain).FirstBrokenSequence);
    }

    [Fact]
    public void Timestamps_are_hashed_at_the_precision_the_database_stores()
    {
        var precise = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567);

        var stored = AuditHashChain.ToStoragePrecision(precise);

        Assert.Equal(0, stored.UtcTicks % 10);
        Assert.Equal(
            AuditHashChain.Compute("p", Guid.Empty, precise, "s", "a", "x", "c", "r", "1", "o", "{}", null),
            AuditHashChain.Compute("p", Guid.Empty, stored, "s", "a", "x", "c", "r", "1", "o", "{}", null));
    }
}

public sealed class InvoiceRendererTests
{
    [Fact]
    public void Catalog_text_is_html_encoded_in_generated_invoices()
    {
        var request = new DocumentGenerationRequestedV1(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Invoice",
            [new InvoiceLineV1("KBD-1001", "<script>alert(1)</script>", 1, 10m)], 10m, "EUR");

        var html = InvoiceRenderer.Render(request);

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }
}
