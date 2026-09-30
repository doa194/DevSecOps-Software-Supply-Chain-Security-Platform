// Test cases for dotnet-security.yaml, run with `semgrep --test`.
// Lines after `ruleid:` must be reported by that rule; lines after `ok:` must not.
using System.Diagnostics;
using System.Security.Cryptography;

public class RuleExamples
{
    public void Sql(AppDb db, string name)
    {
        // ruleid: sscp.dotnet.raw-sql-from-non-constant
        db.Database.ExecuteSqlRaw($"DELETE FROM users WHERE name = '{name}'");
        // ruleid: sscp.dotnet.raw-sql-from-non-constant
        db.Users.FromSqlRaw("SELECT * FROM users WHERE name = '" + name + "'");
        // ok: sscp.dotnet.raw-sql-from-non-constant
        db.Database.ExecuteSqlRaw("SELECT pg_advisory_xact_lock(42)");
        // ok: sscp.dotnet.raw-sql-from-non-constant
        db.Database.ExecuteSql($"DELETE FROM users WHERE name = {name}");
        foreach (var statement in PrivilegeStatements.RuntimeGrants(schema, role, history, false))
        {
            // ok: sscp.dotnet.raw-sql-from-non-constant
            db.Database.ExecuteSqlRaw(statement);
        }

        foreach (var statement in OtherStatements.Build(name))
        {
            // ruleid: sscp.dotnet.raw-sql-from-non-constant
            db.Database.ExecuteSqlRaw(statement);
        }
    }

    public void Deserialization()
    {
        // ruleid: sscp.dotnet.insecure-deserialization
        var formatter = new BinaryFormatter();
        // ruleid: sscp.dotnet.insecure-deserialization
        var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };
        // ok: sscp.dotnet.insecure-deserialization
        var safe = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None };
    }

    public void Tls(HttpClientHandler handler)
    {
        // ruleid: sscp.dotnet.tls-validation-disabled
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        // ruleid: sscp.dotnet.tls-validation-disabled
        handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true;
        // ok: sscp.dotnet.tls-validation-disabled
        handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => Validate(certificate, errors);
    }

    public void Tokens()
    {
        var parameters = new TokenValidationParameters
        {
            // ruleid: sscp.dotnet.token-validation-weakened
            ValidateAudience = false,
            // ok: sscp.dotnet.token-validation-weakened
            ValidateIssuer = true,
        };
    }

    public void Hashes(byte[] data)
    {
        // ruleid: sscp.dotnet.weak-hash
        var md5 = MD5.HashData(data);
        // ok: sscp.dotnet.weak-hash
        var sha = SHA256.HashData(data);
    }

    public void Logging(DbContextOptionsBuilder options)
    {
        // ruleid: sscp.dotnet.sensitive-data-logging
        options.EnableSensitiveDataLogging();
    }

    public void Processes(string command)
    {
        // ruleid: sscp.dotnet.process-start-non-constant
        Process.Start(command);
        // ok: sscp.dotnet.process-start-non-constant
        Process.Start("dotnet");
    }

    public void Endpoints(RouteHandlerBuilder endpoint)
    {
        // ruleid: sscp.dotnet.anonymous-endpoint
        endpoint.AllowAnonymous();
    }
}
