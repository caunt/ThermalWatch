using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramDiscussionMessageTrackerTests
{
    [Fact]
    public async Task WaitReturnsAutomaticForwardObservedBeforeWaitAsync()
    {
        var tracker = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);
        tracker.Observe(CreateUpdate(
            channelMessageId: 701,
            discussionMessageId: 801,
            isAutomaticForward: true,
            discussionId: -100200L,
            channelId: -100100L));

        int? result = await tracker.WaitAsync(
            channelMessageId: 701,
            TimeSpan.FromSeconds(seconds: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected: 801, actual: result);
    }

    [Fact]
    public async Task WaitCompletesWhenAutomaticForwardArrivesLaterAsync()
    {
        var tracker = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);
        Task<int?> wait = tracker.WaitAsync(
            channelMessageId: 701,
            TimeSpan.FromSeconds(seconds: 1),
            TestContext.Current.CancellationToken);

        tracker.Observe(CreateUpdate(
            channelMessageId: 701,
            discussionMessageId: 801,
            isAutomaticForward: true,
            discussionId: -100200L,
            channelId: -100100L));

        Assert.Equal(expected: 801, actual: await wait);
    }

    [Theory]
    [InlineData(false, -100200L, -100100L)]
    [InlineData(true, -100201L, -100100L)]
    [InlineData(true, -100200L, -100101L)]
    public async Task WaitIgnoresUnrelatedUpdatesAsync(
        bool isAutomaticForward,
        long discussionId,
        long channelId)
    {
        var tracker = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);
        tracker.Observe(CreateUpdate(
            channelMessageId: 701,
            discussionMessageId: 801,
            isAutomaticForward,
            discussionId,
            channelId));

        int? result = await tracker.WaitAsync(
            channelMessageId: 701,
            TimeSpan.FromMilliseconds(milliseconds: 10),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task TrackerBoundsEarlyAutomaticForwardsAsync()
    {
        var tracker = new TelegramDiscussionMessageTracker(
            channelId: -100100L,
            discussionId: -100200L,
            capacity: 2);
        tracker.Observe(CreateUpdate(
            channelMessageId: 701,
            discussionMessageId: 801,
            isAutomaticForward: true,
            discussionId: -100200L,
            channelId: -100100L));
        tracker.Observe(CreateUpdate(
            channelMessageId: 702,
            discussionMessageId: 802,
            isAutomaticForward: true,
            discussionId: -100200L,
            channelId: -100100L));
        tracker.Observe(CreateUpdate(
            channelMessageId: 703,
            discussionMessageId: 803,
            isAutomaticForward: true,
            discussionId: -100200L,
            channelId: -100100L));

        int? evicted = await tracker.WaitAsync(
            channelMessageId: 701,
            TimeSpan.FromMilliseconds(milliseconds: 10),
            TestContext.Current.CancellationToken);
        int? retained = await tracker.WaitAsync(
            channelMessageId: 702,
            TimeSpan.FromSeconds(seconds: 1),
            TestContext.Current.CancellationToken);

        Assert.Null(evicted);
        Assert.Equal(expected: 802, actual: retained);
    }

    private static Update CreateUpdate(
        int channelMessageId,
        int discussionMessageId,
        bool isAutomaticForward,
        long discussionId,
        long channelId) =>
        new()
        {
            Id = discussionMessageId + 100,
            Message = new()
            {
                Id = discussionMessageId,
                Date = new DateTime(year: 2026, month: 7, day: 24, hour: 12, minute: 0, second: 0, kind: DateTimeKind.Utc),
                Chat = new() { Id = discussionId, Type = ChatType.Supergroup },
                IsAutomaticForward = isAutomaticForward,
                ForwardOrigin = new MessageOriginChannel
                {
                    Date = new DateTime(year: 2026, month: 7, day: 24, hour: 12, minute: 0, second: 0, kind: DateTimeKind.Utc),
                    Chat = new() { Id = channelId, Type = ChatType.Channel },
                    MessageId = channelMessageId
                }
            }
        };
}
