using System.Net.Sockets;
using Baton.Domain.Automations;
using Baton.Domain.Notifications;
using Baton.Persistence.Notifications;
using Baton.Persistence.Security;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Baton.Orchestrator.Notifications;

/// <summary>How one attempt ended. <see cref="Retryable"/> failures are tried again; the rest fail the delivery at once.</summary>
public readonly record struct SendResult(bool Succeeded, bool Retryable, string? Error)
{
    public static SendResult Sent => new(true, false, null);

    public static SendResult Retry(string error) => new(false, true, error);

    public static SendResult Fail(string error) => new(false, false, error);
}

/// <summary>Sends one attempt of a delivery of one channel kind.</summary>
public interface INotificationSender
{
    /// <summary><c>email</c> or <c>webhook</c>.</summary>
    string Kind { get; }

    Task<SendResult> SendAsync(DueDelivery delivery, NotificationMessage message, CancellationToken ct);
}

/// <summary>
/// Signed webhook deliveries (OBS-05 "Signed webhook deliveries", design.md decision 21): POST of the JSON body with
/// <c>X-Delivery-Id</c> and <c>X-Signature</c> in the format Baton verifies on inbound webhooks, through a client that
/// follows no redirect and waits at most 10 s. 2xx is sent; 408, 425, 429, 5xx, timeouts and connection errors are
/// retried; any other answer, and a URL or secret that can no longer be decrypted, fail the delivery at once. The
/// signature timestamp is this host's clock: it is a header for a third party checked within ±5 minutes, not
/// platform state.
/// </summary>
public sealed class WebhookNotificationSender(IHttpClientFactory clients, NotificationSecrets secrets, IOptions<NotificationPolicy> policy)
    : INotificationSender
{
    public const string ClientName = "baton-notifications";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public string Kind => NotificationRules.WebhookKind;

    public async Task<SendResult> SendAsync(DueDelivery delivery, NotificationMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(message);

        var url = delivery.WebhookUrl is { } storedUrl ? secrets.RevealUrl(storedUrl) : null;
        var secret = delivery.WebhookSecret is { } storedSecret ? secrets.RevealSecret(storedSecret) : null;
        if (url is null || secret is null)
            return SendResult.Fail("The channel's URL or signing secret cannot be decrypted; check the Data Protection key ring.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || !(target.Scheme == Uri.UriSchemeHttps || (policy.Value.AllowInsecureWebhooks && target.Scheme == Uri.UriSchemeHttp)))
        {
            return SendResult.Fail("The channel's URL is not an allowed https URL.");
        }

        var body = message.WebhookBody(delivery.DeliveryId);
        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Delivery-Id", delivery.DeliveryId);
        request.Headers.Add(WebhookSignature.HeaderName,
            WebhookSignature.Compute(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), delivery.DeliveryId, body));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return SendResult.Sent;

            var error = $"The receiver answered {status} {response.ReasonPhrase}.";
            return status is 408 or 425 or 429 or >= 500 ? SendResult.Retry(error) : SendResult.Fail(error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SendResult.Retry($"The receiver did not answer within {Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            return SendResult.Retry($"The receiver could not be reached: {ex.Message}");
        }
    }
}

/// <summary>SMTP settings (section <c>Baton:Notifications:Smtp</c>, ADR-0019). The password comes from the environment.</summary>
public sealed class SmtpOptions
{
    public const string Section = "Baton:Notifications:Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 25;

    /// <summary><c>StartTls</c> (default), <c>SslOnConnect</c>, <c>Auto</c> or <c>None</c>.</summary>
    public string Security { get; set; } = nameof(SecureSocketOptions.StartTls);

    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// Email deliveries through MailKit (OBS-05, ADR-0019, design.md decision 22): one plain-text and HTML message to the
/// channel's recipients. 4xx SMTP replies and connection errors are retried, 5xx replies fail at once, and without
/// SMTP settings every email delivery fails at once with that reason.
/// </summary>
public sealed class SmtpNotificationSender(IOptions<SmtpOptions> options) : INotificationSender
{
    public const string NotConfigured = "Email delivery is not configured (Baton:Notifications:Smtp:Host).";

    public string Kind => NotificationRules.EmailKind;

    public async Task<SendResult> SendAsync(DueDelivery delivery, NotificationMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(message);

        var smtp = options.Value;
        if (!smtp.IsConfigured)
            return SendResult.Fail(NotConfigured);

        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(smtp.From!));
        foreach (var recipient in delivery.EmailTo ?? [])
            mail.To.Add(MailboxAddress.Parse(recipient));
        mail.Subject = message.Subject;
        mail.Headers.Add("X-Baton-Delivery-Id", delivery.DeliveryId);
        mail.Body = new BodyBuilder { TextBody = message.TextBody(), HtmlBody = message.HtmlBody() }.ToMessageBody();

        using var client = new SmtpClient { Timeout = (int)TimeSpan.FromSeconds(30).TotalMilliseconds };
        try
        {
            await client.ConnectAsync(smtp.Host!, smtp.Port, Enum.Parse<SecureSocketOptions>(smtp.Security, ignoreCase: true), ct);
            if (!string.IsNullOrEmpty(smtp.UserName))
                await client.AuthenticateAsync(smtp.UserName, smtp.Password ?? "", ct);
            await client.SendAsync(mail, ct);
            await client.DisconnectAsync(quit: true, ct);
            return SendResult.Sent;
        }
        catch (SmtpCommandException ex) when ((int)ex.StatusCode >= 500)
        {
            return SendResult.Fail($"The mail server refused the message: {(int)ex.StatusCode} {ex.Message}");
        }
        catch (Exception ex) when (ex is SmtpCommandException or SmtpProtocolException or IOException or SocketException
                                       or ServiceNotConnectedException or ServiceNotAuthenticatedException)
        {
            return SendResult.Retry($"The mail server could not take the message: {ex.Message}");
        }
        catch (AuthenticationException ex)
        {
            return SendResult.Fail($"The mail server rejected Baton's credentials: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SendResult.Retry("The mail server did not answer in time.");
        }
    }
}
