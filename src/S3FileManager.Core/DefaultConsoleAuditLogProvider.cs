using Microsoft.Extensions.Logging;

namespace S3FileManager.Core;

public sealed class DefaultConsoleAuditLogProvider(ILogger<DefaultConsoleAuditLogProvider> logger) : IAuditLogProvider
{
    public Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "[AUDIT] {Timestamp:u} User={UserId} Action={Action} Path={Path} Details={Details}",
            auditEvent.Timestamp,
            auditEvent.UserId,
            auditEvent.Action,
            auditEvent.Path,
            auditEvent.Details ?? string.Empty
        );

        return Task.CompletedTask;
    }
}
