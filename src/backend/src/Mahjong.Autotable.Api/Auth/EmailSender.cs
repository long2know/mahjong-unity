using System.Collections.Concurrent;
using System.Net.Mail;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase J Wave 8 — outbound email transport for the magic-link surface.
///
/// <para>The interface is a single fire-and-forget Send. In dev / test the
/// <see cref="InMemoryEmailSender"/> implementation captures emails so tests
/// can read back the magic-link URL; in production the
/// <see cref="SmtpEmailSender"/> ships over SMTP. The
/// <see cref="LogEmailSender"/> logs the payload to ILogger only and is the
/// safest default when no SMTP is configured.</para>
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends an email. Best-effort — failures are logged but the
    /// auth flow proceeds (the magic link is still stored in the DB and can
    /// be surfaced to QA via the response body when configured).</summary>
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);

    /// <summary>Returns the most recent emails captured by an in-memory
    /// sender. Implementations that don't capture return an empty list.</summary>
    IReadOnlyList<CapturedEmail> RecentlyCaptured { get; }
}

/// <summary>Captured email payload for in-memory testing.</summary>
public sealed record CapturedEmail(string To, string Subject, string Body, DateTime SentAtUtc);

/// <summary>
/// Phase J Wave 8 — logger-only email sender. The default for environments
/// with no SMTP configured. Also captures the last 32 emails in memory so
/// tests / QA can inspect what *would* have been sent.
/// </summary>
public sealed class LogEmailSender : IEmailSender
{
    private const int CaptureCapacity = 32;
    private readonly ILogger<LogEmailSender> _logger;
    private readonly ConcurrentQueue<CapturedEmail> _captured = new();

    public LogEmailSender(ILogger<LogEmailSender> logger) { _logger = logger; }

    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        var captured = new CapturedEmail(to, subject, body, DateTime.UtcNow);
        _captured.Enqueue(captured);
        while (_captured.Count > CaptureCapacity && _captured.TryDequeue(out _)) { }
        _logger.LogInformation(
            "[stub-email] to={To} subject={Subject} bodyPreview={Preview}",
            to, subject, body.Length > 200 ? body[..200] : body);
        return Task.CompletedTask;
    }

    public IReadOnlyList<CapturedEmail> RecentlyCaptured => _captured.ToArray();
}

/// <summary>
/// Phase J Wave 8 — in-memory capturing sender for xUnit. Tests resolve
/// this directly and read <see cref="RecentlyCaptured"/> to extract the
/// magic-link URL emitted by the runtime.
/// </summary>
public sealed class InMemoryEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<CapturedEmail> _captured = new();

    public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        _captured.Enqueue(new CapturedEmail(to, subject, body, DateTime.UtcNow));
        return Task.CompletedTask;
    }

    public IReadOnlyList<CapturedEmail> RecentlyCaptured => _captured.ToArray();

    public void Clear()
    {
        while (_captured.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Phase J Wave 8 — production SMTP email sender. Only registered when
/// <see cref="SmtpOptions.Host"/> is non-empty. Failures are caught + logged
/// — a transient SMTP outage must not prevent the magic-link token from
/// being persisted (a recoverable retry / admin fallback is preferable to a
/// 500).
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(SmtpOptions options, ILogger<SmtpEmailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public IReadOnlyList<CapturedEmail> RecentlyCaptured => Array.Empty<CapturedEmail>();

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        try
        {
            using var client = new SmtpClient(_options.Host, _options.Port)
            {
                EnableSsl = _options.UseSsl,
                Credentials = string.IsNullOrEmpty(_options.User) ? null : new System.Net.NetworkCredential(_options.User, _options.Pass)
            };
            using var msg = new MailMessage(_options.From, to, subject, body);
            await client.SendMailAsync(msg, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SMTP send failed (to={To} subject={Subject}); magic-link token is still valid in the DB.",
                to, subject);
        }
    }
}
