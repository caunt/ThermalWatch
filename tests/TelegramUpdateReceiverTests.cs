using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramUpdateReceiverTests
{
    [Fact]
    public async Task GetInitialUpdateOffsetDiscardsPendingUpdatesAsync()
    {
        var handler = new SequenceTelegramHandler((_, _, _) => Task.FromResult(JsonResponse(
            statusCode: HttpStatusCode.OK,
            json: """{"ok":true,"result":[{"update_id":42}]}""")));
        using var httpClient = new HttpClient(handler);
        TelegramBotClient client = CreateClient(httpClient);

        int offset = await TelegramNotificationService.GetInitialUpdateOffsetAsync(
            client,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected: 43, actual: offset);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("getUpdates", request.MethodName);
        using var document = JsonDocument.Parse(request.Body);
        JsonElement root = document.RootElement;
        Assert.Equal(expected: -1, actual: root.GetProperty(propertyName: "offset").GetInt32());
        Assert.Equal(expected: 1, actual: root.GetProperty(propertyName: "limit").GetInt32());
        Assert.Equal(expected: 0, actual: root.GetProperty(propertyName: "timeout").GetInt32());
        Assert.Equal(
            expected: "message",
            actual: root.GetProperty(propertyName: "allowed_updates")[0].GetString());
    }

    [Fact]
    public async Task ReceiverCorrelatesAutomaticForwardAndStopsOnCancellationAsync()
    {
        var handler = new SequenceTelegramHandler(async (requestNumber, _, cancellationToken) =>
        {
            if (requestNumber == 1)
            {
                return JsonResponse(
                    statusCode: HttpStatusCode.OK,
                    json: CreateAutomaticForwardResponse());
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(message: "The canceled long poll unexpectedly resumed.");
        });
        using var httpClient = new HttpClient(handler);
        TelegramBotClient client = CreateClient(httpClient);
        var tracker = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<TelegramUpdateReceiverStopReason> receiver = TelegramNotificationService.ReceiveDiscussionUpdatesAsync(
            client,
            initialUpdateOffset: 43,
            tracker,
            retryDelay: TimeSpan.Zero,
            NullLogger.Instance,
            cancellation.Token);
        int? discussionMessageId = await tracker.WaitAsync(
            channelMessageId: 701,
            TimeSpan.FromSeconds(seconds: 1),
            TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        Assert.Equal(expected: 801, actual: discussionMessageId);
        Assert.Equal(TelegramUpdateReceiverStopReason.Canceled, await receiver);
        using var document = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal(
            expected: 43,
            actual: document.RootElement.GetProperty(propertyName: "offset").GetInt32());
        Assert.Equal(
            expected: 5,
            actual: document.RootElement.GetProperty(propertyName: "timeout").GetInt32());
    }

    [Fact]
    public async Task ReceiverStopsOnCompetingConsumerAsync()
    {
        var handler = new SequenceTelegramHandler((_, _, _) => Task.FromResult(JsonResponse(
            statusCode: HttpStatusCode.Conflict,
            json: """{"ok":false,"error_code":409,"description":"Conflict: terminated by other getUpdates request"}""")));
        using var httpClient = new HttpClient(handler);
        TelegramBotClient client = CreateClient(httpClient);

        TelegramUpdateReceiverStopReason result = await TelegramNotificationService.ReceiveDiscussionUpdatesAsync(
            client,
            initialUpdateOffset: 0,
            new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L),
            retryDelay: TimeSpan.Zero,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(TelegramUpdateReceiverStopReason.PermanentFailure, result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReceiverRetriesTransientFailureAsync()
    {
        var handler = new SequenceTelegramHandler((requestNumber, _, _) => Task.FromResult(
            requestNumber == 1
                ? JsonResponse(
                    statusCode: HttpStatusCode.InternalServerError,
                    json: """{"ok":false,"error_code":500,"description":"Temporary failure"}""")
                : JsonResponse(
                    statusCode: HttpStatusCode.Conflict,
                    json: """{"ok":false,"error_code":409,"description":"Competing consumer"}""")));
        using var httpClient = new HttpClient(handler);
        TelegramBotClient client = CreateClient(httpClient);

        TelegramUpdateReceiverStopReason result = await TelegramNotificationService.ReceiveDiscussionUpdatesAsync(
            client,
            initialUpdateOffset: 0,
            new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L),
            retryDelay: TimeSpan.Zero,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Equal(TelegramUpdateReceiverStopReason.PermanentFailure, result);
        Assert.Equal(expected: 2, actual: handler.Requests.Count);
    }

    [Fact]
    public void DiscussionReadValidationAcceptsAdministratorOrDisabledPrivacyMode()
    {
        var privacyEnabledBot = new User { CanReadAllGroupMessages = false };
        var privacyDisabledBot = new User { CanReadAllGroupMessages = true };

        Assert.True(TelegramNotificationService.CanReadDiscussion(
            privacyEnabledBot,
            new ChatMemberAdministrator()));
        Assert.True(TelegramNotificationService.CanReadDiscussion(
            privacyDisabledBot,
            new ChatMemberMember()));
        Assert.False(TelegramNotificationService.CanReadDiscussion(
            privacyEnabledBot,
            new ChatMemberMember()));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("https://example.test/telegram", true)]
    public void WebhookValidationRequiresEmptyUrl(string url, bool expected)
    {
        bool result = TelegramNotificationService.HasConfiguredWebhook(new WebhookInfo { Url = url });

        Assert.Equal(expected, result);
    }

    private static TelegramBotClient CreateClient(HttpClient httpClient) =>
        new(
            new TelegramBotClientOptions(token: "123456:test-token") { RetryCount = 0 },
            httpClient);

    private static string CreateAutomaticForwardResponse() =>
        """
        {
          "ok": true,
          "result": [
            {
              "update_id": 43,
              "message": {
                "message_id": 801,
                "date": 1784894400,
                "chat": { "id": -100200, "type": "supergroup" },
                "is_automatic_forward": true,
                "forward_origin": {
                  "type": "channel",
                  "date": 1784894400,
                  "chat": { "id": -100100, "type": "channel" },
                  "message_id": 701
                }
              }
            }
          ]
        }
        """;

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, mediaType: "application/json")
        };

    private sealed class SequenceTelegramHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new(request.RequestUri!.Segments[^1], body));
            return await respond(Requests.Count, request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record RecordedRequest(string MethodName, string Body);
}
