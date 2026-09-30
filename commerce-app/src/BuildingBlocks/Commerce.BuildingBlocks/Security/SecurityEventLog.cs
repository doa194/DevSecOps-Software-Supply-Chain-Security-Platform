// Structured runtime security events.
//
// Security-relevant situations are logged with a fixed event id, a stable event type and
// named fields, and counted in the `commerce_security_events_total` metric. Log queries
// and alerts can then match `security.event` values instead of parsing free text, and a
// dashboard can show denials, tampered messages or rate limiting per workload.
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Commerce.BuildingBlocks.Security;

public static class SecurityEventTypes
{
    public const string AuthenticationFailed = "authentication.failed";
    public const string AuthorizationDenied = "authorization.denied";
    public const string RoleChanged = "identity.role_changed";
    public const string PrivilegedOperation = "privileged.operation";
    public const string MessageRejected = "integration.message_rejected";
    public const string PolicyViolation = "policy.violation";
    public const string RateLimited = "ratelimit.rejected";
}

public sealed partial class SecurityEventLog(ILogger<SecurityEventLog> logger)
{
    public static readonly Meter Meter = new("Commerce.Security");

    private static readonly Counter<long> Events =
        Meter.CreateCounter<long>("commerce_security_events", description: "Security events by type and outcome");

    public void AuthenticationFailed(string reason, string? path, string? remoteIp)
    {
        Count(SecurityEventTypes.AuthenticationFailed, "failure");
        LogAuthenticationFailed(logger, SecurityEventTypes.AuthenticationFailed, reason, path, remoteIp);
    }

    public void AuthorizationDenied(string? subject, string resource, string requirement)
    {
        Count(SecurityEventTypes.AuthorizationDenied, "denied");
        LogAuthorizationDenied(logger, SecurityEventTypes.AuthorizationDenied, subject, resource, requirement);
    }

    public void RoleChanged(string actor, string targetUser, string change)
    {
        Count(SecurityEventTypes.RoleChanged, "success");
        LogRoleChanged(logger, SecurityEventTypes.RoleChanged, actor, targetUser, change);
    }

    public void PrivilegedOperation(string actor, string operation, string resource)
    {
        Count(SecurityEventTypes.PrivilegedOperation, "success");
        LogPrivilegedOperation(logger, SecurityEventTypes.PrivilegedOperation, actor, operation, resource);
    }

    public void MessageRejected(string reason, string? keyId, string? source, string? messageType, string queue)
    {
        Count(SecurityEventTypes.MessageRejected, "rejected");
        LogMessageRejected(logger, SecurityEventTypes.MessageRejected, reason, keyId, source, messageType, queue);
    }

    public void PolicyViolation(string? subject, string policy, string detail)
    {
        Count(SecurityEventTypes.PolicyViolation, "blocked");
        LogPolicyViolation(logger, SecurityEventTypes.PolicyViolation, subject, policy, detail);
    }

    public void RateLimited(string? subject, string? remoteIp, string path, string policy)
    {
        Count(SecurityEventTypes.RateLimited, "rejected");
        LogRateLimited(logger, SecurityEventTypes.RateLimited, subject, remoteIp, path, policy);
    }

    private static void Count(string type, string outcome) =>
        Events.Add(1, new KeyValuePair<string, object?>("type", type), new KeyValuePair<string, object?>("outcome", outcome));

    [LoggerMessage(EventId = 9001, Level = LogLevel.Warning, Message = "Security event {SecurityEvent}: authentication failed ({Reason}) path={Path} remote={RemoteIp}")]
    private static partial void LogAuthenticationFailed(ILogger logger, string securityEvent, string reason, string? path, string? remoteIp);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning, Message = "Security event {SecurityEvent}: {Subject} denied access to {Resource} ({Requirement})")]
    private static partial void LogAuthorizationDenied(ILogger logger, string securityEvent, string? subject, string resource, string requirement);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Information, Message = "Security event {SecurityEvent}: {Actor} changed roles of {TargetUser}: {Change}")]
    private static partial void LogRoleChanged(ILogger logger, string securityEvent, string actor, string targetUser, string change);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Information, Message = "Security event {SecurityEvent}: {Actor} performed {Operation} on {Resource}")]
    private static partial void LogPrivilegedOperation(ILogger logger, string securityEvent, string actor, string operation, string resource);

    [LoggerMessage(EventId = 9005, Level = LogLevel.Error, Message = "Security event {SecurityEvent}: integration message rejected ({Reason}) key={KeyId} source={Source} type={MessageType} queue={Queue}")]
    private static partial void LogMessageRejected(ILogger logger, string securityEvent, string reason, string? keyId, string? source, string? messageType, string queue);

    [LoggerMessage(EventId = 9006, Level = LogLevel.Warning, Message = "Security event {SecurityEvent}: policy {Policy} blocked {Subject}: {Detail}")]
    private static partial void LogPolicyViolation(ILogger logger, string securityEvent, string? subject, string policy, string detail);

    [LoggerMessage(EventId = 9007, Level = LogLevel.Warning, Message = "Security event {SecurityEvent}: rate limit {Policy} rejected {Subject} remote={RemoteIp} path={Path}")]
    private static partial void LogRateLimited(ILogger logger, string securityEvent, string? subject, string? remoteIp, string path, string policy);
}
