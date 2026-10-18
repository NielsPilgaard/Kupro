namespace Kupro.Api.Features.Email;

public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>Outgoing email. Interface because the transport is an external system (vendor: ADR-006).</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Logs instead of sending until the transactional email integration exists.</summary>
public sealed partial class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        LogEmail(logger, message.To, message.Subject, message.Body);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email to {To}: {Subject} | {Body}")]
    private static partial void LogEmail(ILogger logger, string to, string subject, string body);
}
