// Schema and role names are the only text placed directly into SQL (GRANT / REVOKE during
// migrations). Anything that could change the meaning of a statement must be rejected
// before it gets there.
using Commerce.BuildingBlocks.Persistence;

namespace Commerce.UnitTests.Security;

public sealed class SqlIdentifierTests
{
    [Theory]
    [InlineData("commerce_orders")]
    [InlineData("__ef_migrations")]
    [InlineData("reporting2")]
    public void Plain_lower_case_identifiers_are_accepted(string name)
    {
        Assert.Equal(name, SqlIdentifier.Parse(name).Value);
    }

    [Theory]
    [InlineData("orders; DROP TABLE orders")]
    [InlineData("orders\" OR 1=1 --")]
    [InlineData("Orders")]
    [InlineData("orders role")]
    [InlineData("1orders")]
    [InlineData("")]
    public void Anything_that_could_change_the_statement_is_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => SqlIdentifier.Parse(name));
    }

    [Fact]
    public void Append_only_schemas_never_receive_update_or_delete()
    {
        var statements = PrivilegeStatements.RuntimeGrants(
            SqlIdentifier.Parse("audit"), SqlIdentifier.Parse("commerce_audit_worker"), SqlIdentifier.Parse("__ef_migrations"), appendOnly: true);

        Assert.DoesNotContain(statements, s => s.Contains("UPDATE", StringComparison.Ordinal) || s.Contains("DELETE", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.StartsWith("REVOKE ALL ON audit.__ef_migrations", StringComparison.Ordinal));
    }
}
