using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

public sealed class TelegramNotificationService(
    TelegramOptions options,
    AnomalySnapshotStore snapshotStore,
    GibsClient gibsClient,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<TelegramNotificationService> logger) : BackgroundService
{
    private const int MaximumSeenIds = 100_000;
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly List<PendingNotification> _pending = [];
    private bool _firstReadySnapshot = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var validation = await TryValidateAsync(stoppingToken);
        if (validation is not { } validated)
            return;

        await foreach (var snapshot in snapshotStore.ReadUpdatesAsync(stoppingToken))
        {
            if (!snapshot.IsReady)
                continue;

            TrackNewDetections(snapshot);
            if (!await SendPendingAsync(validated.Client, validated.ChatId, stoppingToken))
                return;
        }
    }

    private async Task<ValidatedTelegram?> TryValidateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var clientOptions = new TelegramBotClientOptions(options.BotToken!)
            {
                RetryCount = 2,
                RetryThreshold = 30
            };
            var client = new TelegramBotClient(
                clientOptions,
                httpClientFactory.CreateClient("Telegram"),
                cancellationToken);
            var chatId = new ChatId(options.ChannelId!);
            var bot = await client.GetMe(cancellationToken);
            var chat = await client.GetChat(chatId, cancellationToken);

            if (chat.Type != ChatType.Channel)
            {
                logger.LogError("Telegram notifier disabled: TELEGRAM_CHANNEL_ID is not a channel");
                return null;
            }

            var member = await client.GetChatMember(chatId, bot.Id, cancellationToken);
            if (member is not ChatMemberOwner
                && member is not ChatMemberAdministrator { CanPostMessages: true })
            {
                logger.LogError("Telegram notifier disabled: bot cannot post to the configured channel");
                return null;
            }

            logger.LogInformation("Telegram notifier validated and enabled");
            return new(client, chatId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogError("Telegram notifier disabled: validation failed");
            return null;
        }
    }

    private void TrackNewDetections(AnomalySnapshot snapshot)
    {
        var now = timeProvider.GetUtcNow();
        ExpireSeen(now);

        if (_firstReadySnapshot && !options.NotifyExistingOnStartup)
        {
            foreach (var detection in snapshot.Items)
                _seen[detection.Id] = now;

            _firstReadySnapshot = false;
            TrimSeen();
            logger.LogInformation(
                "Primed Telegram deduplication with {NewDetectionCount} existing detections",
                snapshot.Items.Length);
            return;
        }

        _firstReadySnapshot = false;
        var newDetections = snapshot.Items
            .Where(detection => !_seen.ContainsKey(detection.Id))
            .ToArray();

        foreach (var detection in newDetections)
            _seen[detection.Id] = now;

        TrimSeen();
        var clusters = NotificationClustering.Create(
            newDetections,
            options.ClusterRadiusKilometers,
            options.ClusterTimeWindow);

        foreach (var cluster in clusters)
            _pending.Add(new(cluster, now));

        if (newDetections.Length > 0)
        {
            logger.LogInformation(
                "Prepared {ZoneCount} Telegram zones from {NewDetectionCount} new detections",
                clusters.Length,
                newDetections.Length);
        }
    }

    private async Task<bool> SendPendingAsync(
        TelegramBotClient client,
        ChatId chatId,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < _pending.Count;)
        {
            var pending = _pending[index];
            var preview = await gibsClient.GetPreviewAsync(
                pending.Cluster.Representative,
                cancellationToken);
            var previewExpired = timeProvider.GetUtcNow() - pending.FirstSeenUtc >= options.PreviewRetryWindow;

            if (!preview.IsAvailable && !previewExpired)
            {
                index++;
                continue;
            }

            try
            {
                var keyboard = new InlineKeyboardMarkup(
                [
                    [InlineKeyboardButton.WithUrl(
                        "🗺 Open in Google Maps",
                        pending.Cluster.Representative.GoogleMapsUrl)]
                ]);
                var message = TelegramMessageFormatter.Format(pending.Cluster, preview.IsAvailable);

                if (preview.PngBytes is { } pngBytes)
                {
                    using var stream = new MemoryStream(pngBytes, writable: false);
                    await client.SendPhoto(
                        chatId,
                        InputFile.FromStream(stream, "thermal-anomaly.png"),
                        caption: message,
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await client.SendMessage(
                        chatId,
                        message,
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard,
                        linkPreviewOptions: new() { IsDisabled = true },
                        cancellationToken: cancellationToken);
                }

                logger.LogInformation(
                    "Sent Telegram notification {NotificationId} for {Satellite} at {AcquiredAtUtc}",
                    pending.Cluster.Id,
                    pending.Cluster.Representative.Satellite,
                    pending.Cluster.Representative.AcquiredAtUtc);
                _pending.RemoveAt(index);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiRequestException exception) when (exception.ErrorCode is 400 or 401 or 403)
            {
                logger.LogError("Telegram notifier disabled after a permanent send failure");
                return false;
            }
            catch (Exception)
            {
                logger.LogWarning(
                    "Telegram send failed transiently for notification {NotificationId}",
                    pending.Cluster.Id);
                return true;
            }
        }

        return true;
    }

    private void ExpireSeen(DateTimeOffset now)
    {
        var cutoff = now - options.SeenRetention;
        foreach (var id in _seen.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
            _seen.Remove(id);
    }

    private void TrimSeen()
    {
        var excess = _seen.Count - MaximumSeenIds;
        if (excess <= 0)
            return;

        foreach (var id in _seen.OrderBy(pair => pair.Value).Take(excess).Select(pair => pair.Key).ToArray())
            _seen.Remove(id);
    }

    private sealed record PendingNotification(NotificationCluster Cluster, DateTimeOffset FirstSeenUtc);

    private sealed record ValidatedTelegram(TelegramBotClient Client, ChatId ChatId);
}
