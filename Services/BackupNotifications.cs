using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using CitusManager.Domain;
using CitusManager.Security;

namespace CitusManager.Services;

public sealed record BackupNotificationMessage(string Subject, string Body, NotificationEvent Event);

public interface INotificationSender
{
    Task SendAsync(NotificationProfileVersion profile, BackupNotificationMessage message, CancellationToken cancellationToken);
}

public sealed class BackupNotificationSender(
    IBackupSecretProtector secrets,
    IHttpClientFactory clients) : INotificationSender
{
    public async Task SendAsync(
        NotificationProfileVersion profile, BackupNotificationMessage message, CancellationToken cancellationToken)
    {
        switch (profile.Type)
        {
            case NotificationType.Email:
                await SendEmailAsync(profile, message, cancellationToken);
                break;
            case NotificationType.Telegram:
                await SendTelegramAsync(profile, message, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Unsupported backup notification type.");
        }
    }

    private async Task SendEmailAsync(
        NotificationProfileVersion profile, BackupNotificationMessage notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.SmtpHost) || profile.SmtpPort is null)
            throw new InvalidOperationException("SMTP host and port are required.");
        var recipients = JsonSerializer.Deserialize<string[]>(profile.EmailRecipientsJson ?? "[]") ?? [];
        if (recipients.Length == 0) throw new InvalidOperationException("At least one email recipient is required.");
        using var message = new MailMessage { From = new(profile.SmtpFrom ?? profile.SmtpUsername ?? "citus-manager@localhost"), Subject = notification.Subject, Body = notification.Body };
        foreach (var recipient in recipients.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            message.To.Add(new MailAddress(recipient));
        using var client = new SmtpClient(profile.SmtpHost, profile.SmtpPort.Value)
        {
            EnableSsl = profile.SmtpUseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        if (!string.IsNullOrWhiteSpace(profile.SmtpUsername))
            client.Credentials = new NetworkCredential(profile.SmtpUsername,
                string.IsNullOrWhiteSpace(profile.ProtectedSmtpPassword) ? string.Empty : secrets.Unprotect(profile.ProtectedSmtpPassword));
        await client.SendMailAsync(message, cancellationToken);
    }

    private async Task SendTelegramAsync(
        NotificationProfileVersion profile, BackupNotificationMessage notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.ProtectedTelegramBotToken))
            throw new InvalidOperationException("Telegram bot token is required.");
        var token = secrets.Unprotect(profile.ProtectedTelegramBotToken);
        var targets = ParseTelegramTargets(profile.TelegramTargetsJson);
        if (targets.Count == 0) throw new InvalidOperationException("At least one Telegram target is required.");
        var client = clients.CreateClient("backup-notifications");
        foreach (var target in targets)
        {
            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = target.ChatId,
                ["text"] = $"{notification.Subject}\n\n{notification.Body}"
            };
            if (target.ThreadId.HasValue) payload["message_thread_id"] = target.ThreadId.Value;
            using var response = await client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{Uri.EscapeDataString(token)}/sendMessage", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Telegram API returned HTTP {(int)response.StatusCode}.");
        }
    }

    private static IReadOnlyList<TelegramTarget> ParseTelegramTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<TelegramTarget[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (JsonException)
        {
            var ids = JsonSerializer.Deserialize<string[]>(json) ?? [];
            return ids.Select(x => new TelegramTarget(x, null)).ToList();
        }
    }

    private sealed record TelegramTarget(string ChatId, long? ThreadId);
}
