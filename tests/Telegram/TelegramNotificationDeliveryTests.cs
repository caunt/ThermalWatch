using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

using ThermalWatch.Core;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests.Telegram;

/// <summary>Verifies delivery and discussion-comment failure boundaries.</summary>
public sealed class TelegramNotificationDeliveryTests
{
    /// <summary>Provides the supported main-message transports.</summary>
    public static TheoryData<bool, string> MainTransports => new()
    {
        { false, "sendMessage" },
        { true, "sendPhoto" }
    };

    /// <summary>Checks that each main transport precedes its linked comment.</summary>
    [Theory]
    [MemberData(nameof(MainTransports))]
    public async Task SendNotificationPostsMainThenLinkedDiscussionCommentAsync(bool hasPreview, string expectedMainMethod)
    {
        using var handler = new RecordingTelegramHandler(failedRequestNumber: null);

        using var httpClient = new HttpClient(handler);

        var client = new TelegramBotClient(new TelegramBotClientOptions(token: "123456:test-token") { RetryCount = 0 }, httpClient);

        var discussionMessages = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);

        Task delivery = TelegramNotificationService.SendNotificationAsync(
            client,
            new ChatId(username: "@cso_ukr"),
            new ChatId(identifier: -100200L),
            discussionMessages,
            CreateCandidate(hasPreview),
            TimeSpan.FromSeconds(seconds: 5),
            NullLogger.Instance,
            TestContext.Current.CancellationToken
        );

        await handler.FirstRequestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);
        discussionMessages.Observe(CreateAutomaticForward(channelMessageId: 701, discussionMessageId: 801));
        await delivery.ConfigureAwait(continueOnCapturedContext: true);

        Assert.Collection(
            handler.Requests,
            main =>
            {
                Assert.Equal(expectedMainMethod, main.MethodName);
                Assert.Contains(expectedSubstring: "@cso_ukr", main.Body, StringComparison.Ordinal);
                Assert.Contains(expectedSubstring: "New thermal anomaly", main.Body, StringComparison.Ordinal);
                Assert.Contains(expectedSubstring: "Kyiv", main.Body, StringComparison.Ordinal);
                Assert.DoesNotContain(expectedSubstring: "reply_markup", main.Body, StringComparison.Ordinal);
            },
            comment =>
            {
                Assert.Equal(expected: "sendMessage", comment.MethodName);

                using var document = JsonDocument.Parse(comment.Body);

                JsonElement root = document.RootElement;
                Assert.Equal(expected: -100200L, root.GetProperty(propertyName: "chat_id").GetInt64());
                Assert.Contains(expectedSubstring: "Satellite:", root.GetProperty(propertyName: "text").GetString(), StringComparison.Ordinal);
                Assert.False(root.TryGetProperty(propertyName: "reply_markup", out JsonElement markup));
                Assert.Equal(JsonValueKind.Undefined, markup.ValueKind);
                JsonElement reply = root.GetProperty(propertyName: "reply_parameters");
                Assert.Equal(expected: 801, reply.GetProperty(propertyName: "message_id").GetInt32());
                Assert.False(reply.TryGetProperty(propertyName: "chat_id", out JsonElement crossChatId));
                Assert.Equal(JsonValueKind.Undefined, crossChatId.ValueKind);
                Assert.True(root.GetProperty(propertyName: "link_preview_options").GetProperty(propertyName: "is_disabled").GetBoolean());
            }
        );
    }

    /// <summary>Checks that a failed main message prevents the comment.</summary>
    [Fact]
    public async Task SendNotificationPropagatesMainFailureAsync()
    {
        using var handler = new RecordingTelegramHandler(failedRequestNumber: 1);

        using var httpClient = new HttpClient(handler);

        var client = new TelegramBotClient(new TelegramBotClientOptions(token: "123456:test-token") { RetryCount = 0 }, httpClient);

        ApiRequestException failure = await Assert.ThrowsAsync<ApiRequestException>(
            () =>
            TelegramNotificationService.SendNotificationAsync(
                client,
                new ChatId(username: "@cso_ukr"),
                new ChatId(identifier: -100200L),
                new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L),
                CreateCandidate(hasPreview: false),
                TimeSpan.FromSeconds(seconds: 5),
                NullLogger.Instance,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(expected: 500, failure.ErrorCode);
        Assert.Equal(expected: "sendMessage", Assert.Single(handler.Requests).MethodName);
    }

    /// <summary>Checks that an optional comment failure preserves main delivery.</summary>
    [Fact]
    public async Task SendNotificationTreatsCommentFailureAsCompletedAsync()
    {
        using var handler = new RecordingTelegramHandler(failedRequestNumber: 2);

        using var httpClient = new HttpClient(handler);

        var client = new TelegramBotClient(new TelegramBotClientOptions(token: "123456:test-token") { RetryCount = 0 }, httpClient);

        var discussionMessages = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);

        Task delivery = TelegramNotificationService.SendNotificationAsync(
            client,
            new ChatId(username: "@cso_ukr"),
            new ChatId(identifier: -100200L),
            discussionMessages,
            CreateCandidate(hasPreview: false),
            TimeSpan.FromSeconds(seconds: 5),
            NullLogger.Instance,
            TestContext.Current.CancellationToken
        );

        await handler.FirstRequestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);
        discussionMessages.Observe(CreateAutomaticForward(channelMessageId: 701, discussionMessageId: 801));
        await delivery.ConfigureAwait(continueOnCapturedContext: true);

        Assert.Equal(expected: 2, handler.Requests.Count);
    }

    /// <summary>Checks that an absent automatic forward preserves main delivery.</summary>
    [Fact]
    public async Task SendNotificationTreatsMissingAutomaticForwardAsCompletedAsync()
    {
        using var handler = new RecordingTelegramHandler(failedRequestNumber: null);

        using var httpClient = new HttpClient(handler);

        var client = new TelegramBotClient(new TelegramBotClientOptions(token: "123456:test-token") { RetryCount = 0 }, httpClient);

        await TelegramNotificationService.SendNotificationAsync(
            client,
            new ChatId(username: "@cso_ukr"),
            new ChatId(identifier: -100200L),
            new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L),
            CreateCandidate(hasPreview: false),
            TimeSpan.FromMilliseconds(milliseconds: 10),
            NullLogger.Instance,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected: "sendMessage", Assert.Single(handler.Requests).MethodName);
    }

    private static Update CreateAutomaticForward(int channelMessageId, int discussionMessageId)
    {
        return new()
        {
            Id = 900,
            Message = new()
            {
                Id = discussionMessageId,
                Date = new DateTime(year: 2026, month: 7, day: 24, hour: 12, minute: 0, second: 0, DateTimeKind.Utc),
                Chat = new() { Id = -100200L, Type = ChatType.Supergroup },
                IsAutomaticForward = true,
                ForwardOrigin = new MessageOriginChannel
                {
                    Date = new DateTime(year: 2026, month: 7, day: 24, hour: 12, minute: 0, second: 0, DateTimeKind.Utc),
                    Chat = new() { Id = -100100L, Type = ChatType.Channel },
                    MessageId = channelMessageId
                }
            }
        };
    }

    private static PreparedNotificationCandidate CreateCandidate(bool hasPreview)
    {
        var anomaly = new Anomaly(
            Id: "anomaly",
            CountryCode: "UKR",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "Suomi-NPP",
            Instrument: "VIIRS",
            Latitude: 50.123456,
            Longitude: 30.654321,
            new(year: 2026, month: 7, day: 19, hour: 12, minute: 0, second: 0, TimeSpan.Zero),
            DayNight: "D",
            BrightnessKelvin: 330,
            SecondaryBrightnessKelvin: 300,
            FrpMegawatts: 100,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: "n",
            ConfidencePercent: null,
            ConfidenceCategory: "nominal",
            Version: "2.0NRT",
            new Uri(uriString: "https://www.google.com/maps?q=50.123456,30.654321")
        );

        var cluster = new NotificationCluster(Id: "cluster", anomaly, [anomaly]);

        GibsPreview preview = hasPreview
            ? new([1], new(FirmsSource: "VIIRS_SNPP_NRT", Satellite: "Suomi-NPP", Instrument: "VIIRS"))
            : GibsPreview.Unavailable;

        return new(
            cluster,
            preview,
            new(
                new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
                ClusterDiameterKilometers: 1,
                IsLargePreview: false
            ),
            LandCoverSummary: "Built-up · 80%",
            []
        )
        {
            SettlementName = "Kyiv"
        };
    }

    private sealed record RecordedRequest(string MethodName, string Body);

    private sealed class RecordingTelegramHandler(int? failedRequestNumber) : HttpMessageHandler
    {
        public TaskCompletionSource FirstRequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotNull(request.Content);
            Uri requestUri = Assert.IsType<Uri>(request.RequestUri);
            string body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            string methodName = requestUri.Segments[requestUri.Segments.Length - 1];
            Requests.Add(new(methodName, body));

            if (Requests.Count == 1)
                FirstRequestReceived.SetResult();

            if (Requests.Count == failedRequestNumber)
                return JsonResponse(HttpStatusCode.InternalServerError, json: """{"ok":false,"error_code":500,"description":"Test failure"}""");

            long chatId = Requests.Count == 1 ? -100100L : -100200L;
            int messageId = Requests.Count == 1 ? 701 : 702;
            string chatType = Requests.Count == 1 ? "channel" : "supergroup";

            return JsonResponse(
                HttpStatusCode.OK,
                JsonSerializer.Serialize(
                    new
                    {
                        ok = true,
                        result = new
                        {
                            message_id = messageId,
                            date = 1784452800,
                            chat = new { id = chatId, type = chatType },
                            text = "sent"
                        }
                    }
                )
            );
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, [StringSyntax(StringSyntaxAttribute.Json)] string json)
        {
            return new(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, mediaType: "application/json")
            };
        }
    }
}
