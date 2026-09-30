// SQL that has to embed identifiers (schema and role names in GRANT / REVOKE).
//
// Identifiers cannot be sent as SQL parameters, so they are the one place where text is
// placed into SQL. To keep that safe, statements are built only here and only from
// SqlIdentifier values, which can exist only after passing a strict pattern check. The
// platform's static-analysis rule for raw SQL recognises statements from this class.
using System.Text.RegularExpressions;

namespace Commerce.BuildingBlocks.Persistence;

public readonly partial record struct SqlIdentifier
{
    private SqlIdentifier(string value) => Value = value;

    public string Value { get; }

    // Lower-case letters, digits and underscores only: no quotes, spaces, semicolons or comments.
    public static SqlIdentifier Parse(string value) =>
        value is not null && Pattern().IsMatch(value)
            ? new SqlIdentifier(value)
            : throw new ArgumentException($"'{value}' is not an allowed schema or role name.", nameof(value));

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Pattern();
}

public static class PrivilegeStatements
{
    // Row access for a module's runtime role. Append-only schemas (the audit trail) get no
    // UPDATE or DELETE, and the migration history stays owner-only.
    public static IReadOnlyList<string> RuntimeGrants(SqlIdentifier schema, SqlIdentifier role, SqlIdentifier historyTable, bool appendOnly)
    {
        var rowPrivileges = appendOnly ? "SELECT, INSERT" : "SELECT, INSERT, UPDATE, DELETE";
        return
        [
            $"GRANT USAGE ON SCHEMA {schema} TO {role}",
            $"GRANT {rowPrivileges} ON ALL TABLES IN SCHEMA {schema} TO {role}",
            $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO {role}",
            $"REVOKE ALL ON {schema}.{historyTable} FROM {role}",
        ];
    }
}
