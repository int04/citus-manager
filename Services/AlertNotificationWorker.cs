using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using CitusManager.Data;
using CitusManager.Domain;
using Microsoft.EntityFrameworkCore;

namespace CitusManager.Services;

public sealed class AlertNotificationWorker(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    IConfiguration configuration,
    ILogger<AlertNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await DispatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError("Alert dispatcher cycle failed ({ErrorType}).", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DispatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var alerts = await db.Alerts.Include(x => x.Cluster)
            .Where(x => x.State == AlertState.Open && x.NotificationAttempts < 5 &&
                        (x.LastNotifiedAt == null || x.LastSeenAt > x.LastNotifiedAt))
            .OrderBy(x => x.FirstSeenAt).Take(50).ToListAsync(cancellationToken);
        foreach (var alert in alerts)
        {
            try
            {
                var configured = false;
                var webhook = configuration["Notifications:WebhookUrl"];
                if (Uri.TryCreate(webhook, UriKind.Absolute, out var webhookUri) &&
                    webhookUri.Scheme is "https" or "http")
                {
                    configured = true;
                    var client = httpClients.CreateClient("alerts");
                    using var response = await client.PostAsJsonAsync(webhookUri, new
                    {
                        source = "CitusManager",
                        alert.Id,
                        clusterId = alert.ClusterId,
                        cluster = alert.Cluster?.Name,
                        severity = alert.Severity.ToString(),
                        state = alert.State.ToString(),
                        alert.Title,
                        alert.Detail,
                        alert.FirstSeenAt,
                        alert.LastSeenAt
                    }, cancellationToken);
                    response.EnsureSuccessStatusCode();
                }
                if (!string.IsNullOrWhiteSpace(configuration["Notifications:Smtp:Host"]) &&
                    !string.IsNullOrWhiteSpace(configuration["Notifications:Smtp:To"]))
                {
                    configured = true;
                    await SendEmailAsync(alert, cancellationToken);
                }
                if (!configured) continue;
                alert.LastNotifiedAt = DateTimeOffset.UtcNow;
                alert.NotificationAttempts = 0;
            }
            catch (Exception exception)
            {
                alert.NotificationAttempts++;
                logger.LogWarning("Alert {AlertId} delivery attempt {Attempt} failed ({ErrorType}).",
                    alert.Id, alert.NotificationAttempts, exception.GetType().Name);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SendEmailAsync(AlertRecord alert, CancellationToken cancellationToken)
    {
        var section = configuration.GetSection("Notifications:Smtp");
        using var message = new MailMessage(
            section["From"] ?? "citus-manager@localhost",
            section["To"]!,
            $"[{alert.Severity}] {alert.Title}",
            $"Cluster: {alert.Cluster?.Name}\nState: {alert.State}\nFirst seen: {alert.FirstSeenAt:O}\nLast seen: {alert.LastSeenAt:O}\n\n{alert.Detail}");
        using var client = new SmtpClient(section["Host"], section.GetValue("Port", 587))
        {
            EnableSsl = section.GetValue("EnableSsl", true),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        var username = section["Username"];
        if (!string.IsNullOrWhiteSpace(username))
            client.Credentials = new NetworkCredential(username, section["Password"]);
        await client.SendMailAsync(message, cancellationToken);
    }
}
