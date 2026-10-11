using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Telegram.Bot;
using Telegram.Bot.Types;

using ThermalWatch.Telegram;

namespace ThermalWatch.Tests.Telegram;

/// <summary>Verifies Telegram update polling, discussion correlation, and startup validation.</summary>
public sealed class TelegramUpdateReceiverTests
{
    /// <summary>Provides absent and configured webhook URLs.</summary>
    public static TheoryData<string, bool> WebhookCases => new()
    {
        { "", false },
        { "https://example.test/telegram", true }
    };

    /// <summary>Allows discussion reads for administrators or bots with privacy disabled.</summary>
    [Fact]
    public void DiscussionReadValidationAcceptsAdministratorOrDisabledPrivacyMode()
    {
        var privacyEnabledBot = new User { CanReadAllGroupMessages = false };
        var privacyDisabledBot = new User { CanReadAllGroupMessages = true };

        Assert.True(TelegramNotificationService.CanReadDiscussion(privacyEnabledBot, new ChatMemberAdministrator()));
        Assert.True(TelegramNotificationService.CanReadDiscussion(privacyDisabledBot, new ChatMemberMember()));
        Assert.False(TelegramNotificationService.CanReadDiscussion(privacyEnabledBot, new ChatMemberMember()));
    }

    /// <summary>Skips updates that were pending before the receiver starts.</summary>
    [Fact]
    public async Task GetInitialUpdateOffsetDiscardsPendingUpdatesAsync()
    {
        using var handler = new SequenceTelegramHandler(
            (requestNumber, request, cancellationToken) => Task.FromResult(JsonResponse(HttpStatusCode.OK, json: """{"ok":true,"result":[{"update_id":42}]}"""))
        );

        using var httpClient = new HttpClient(handler);

        TelegramBotClient client = CreateClient(httpClient);

        int offset = await TelegramNotificationService.GetInitialUpdateOffsetAsync(client, TestContext.Current.CancellationToken);

        Assert.Equal(expected: 43, offset);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(expected: "getUpdates", request.MethodName);

        using var document = JsonDocument.Parse(request.Body);

        JsonElement root = document.RootElement;
        Assert.Equal(expected: -1, root.GetProperty(propertyName: "offset").GetInt32());
        Assert.Equal(expected: 1, root.GetProperty(propertyName: "limit").GetInt32());
        Assert.Equal(expected: 0, root.GetProperty(propertyName: "timeout").GetInt32());
        Assert.Equal(expected: "message", root.GetProperty(propertyName: "allowed_updates")[index: 0].GetString());
    }

    /// <summary>Correlates the configured channel's automatic forward and joins canceled polling.</summary>
    [Fact]
    public async Task ReceiverCorrelatesAutomaticForwardAndStopsOnCancellationAsync()
    {
        using var handler = new SequenceTelegramHandler(
            async (requestNumber, request, cancellationToken) =>
        {
            if (requestNumber == 1)
                return JsonResponse(HttpStatusCode.OK, CreateAutomaticForwardResponse());

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            throw new InvalidOperationException(message: "The canceled long poll unexpectedly resumed.");
        }
        );

        using var httpClient = new HttpClient(handler);

        TelegramBotClient client = CreateClient(httpClient);
        var tracker = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<TelegramUpdateReceiverStopReason> receiver = TelegramNotificationService.ReceiveDiscussionUpdatesAsync(client, initialUpdateOffset: 43, tracker, TimeSpan.Zero, NullLogger.Instance, cancellation.Token);

        int? discussionMessageId = await tracker.WaitAsync(channelMessageId: 701, TimeSpan.FromSeconds(seconds: 1), TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        Assert.Equal(expected: 801, discussionMessageId);
        Assert.Equal(TelegramUpdateReceiverStopReason.Canceled, await receiver.ConfigureAwait(continueOnCapturedContext: true));

        using var document = JsonDocument.Parse(handler.Requests[index: 0].Body);

        Assert.Equal(expected: 43, document.RootElement.GetProperty(propertyName: "offset").GetInt32());
        Assert.Equal(expected: 5, document.RootElement.GetProperty(propertyName: "timeout").GetInt32());
    }

    /// <summary>Retries transient provider failures before stopping on a permanent conflict.</summary>
    [Fact]
    public async Task ReceiverRetriesTransientFailureAsync()
    {
        using var handler = new SequenceTelegramHandler(
            (requestNumber, request, cancellationToken) => Task.FromResult(
                requestNumber == 1
                ? JsonResponse(HttpStatusCode.InternalServerError, json: """{"ok":false,"error_code":500,"description":"Temporary failure"}""")
                : JsonResponse(HttpStatusCode.Conflict, json: """{"ok":false,"error_code":409,"description":"Competing consumer"}""")
            )
        );

        using var httpClient = new HttpClient(handler);

        TelegramBotClient client = CreateClient(httpClient);

        TelegramUpdateReceiverStopReason result = await TelegramNotificationService.ReceiveDiscussionUpdatesAsync(
            client,
            initialUpdateOffset: 0,
            new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L),
            TimeSpan.Zero,
            NullLogger.Instance,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(TelegramUpdateReceiverStopReason.PermanentFailure, result);
        Assert.Equal(expected: 2, handler.Requests.Count);
    }

    /// <summary>Stops immediately when another update consumer owns the bot.</summary>
    [Fact]
    public async Task ReceiverStopsOnCompetingConsumerAsync()
    {
        using var handler = new SequenceTelegramHandler(
            (requestNumber, request, cancellationToken) => Task.FromResult(
                JsonResponse(
                    HttpStatusCode.Conflict,
                    json: """{"ok":false,"error_code":409,"description":"Conflict: terminated by other getUpdates request"}"""
                )
            )
        );

        using var httpClient = new HttpClient(handler);

        TelegramBotClient client = CreateClient(httpClient);

        TelegramUpdateReceiverStopReason result = await TelegramNotificationService.ReceiveDiscussionUpdatesAsync(
            client,
            initialUpdateOffset: 0,
            new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L),
            TimeSpan.Zero,
            NullLogger.Instance,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(TelegramUpdateReceiverStopReason.PermanentFailure, result);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(expected: "getUpdates", request.MethodName);
    }

    /// <summary>Recognizes configured webhooks that conflict with polling.</summary>
    [Theory]
    [MemberData(nameof(WebhookCases))]
    public void WebhookValidationRequiresEmptyUrl(string configuredValue, bool expected)
    {
        bool result = TelegramNotificationService.HasConfiguredWebhook(new WebhookInfo { Url = configuredValue });

        Assert.Equal(expected, result);
    }

    private static string CreateAutomaticForwardResponse()
    {
        // lang=json
        return """
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
    }

    private static TelegramBotClient CreateClient(HttpClient httpClient)
    {
        return new(new TelegramBotClientOptions(token: "123456:test-token") { RetryCount = 0 }, httpClient);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, [StringSyntax(StringSyntaxAttribute.Json)] string json)
    {
        return new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, mediaType: "application/json")
        };
    }

    private sealed record RecordedRequest(string MethodName, string Body);

    private sealed class SequenceTelegramHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotNull(request.Content);
            string body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            Uri requestUri = Assert.IsType<Uri>(request.RequestUri);
            string[] segments = requestUri.Segments;
            Requests.Add(new(segments[segments.Length - 1], body));

            return await respond(Requests.Count, request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
