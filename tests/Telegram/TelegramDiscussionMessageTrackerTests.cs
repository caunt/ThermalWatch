using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

using ThermalWatch.Telegram;

namespace ThermalWatch.Tests.Telegram;

/// <summary>Verifies linked-discussion correlation and bounded early-forward retention.</summary>
public sealed class TelegramDiscussionMessageTrackerTests
{
    /// <summary>Provides forwards from unrelated chats or without automatic-forward status.</summary>
    public static TheoryData<bool, long, long> UnrelatedUpdateCases => new()
    {
        { false, -100200L, -100100L },
        { true, -100201L, -100100L },
        { true, -100200L, -100101L }
    };

    /// <summary>Evicts the oldest unmatched automatic forward when capacity is exceeded.</summary>
    [Fact]
    public async Task TrackerBoundsEarlyAutomaticForwardsAsync()
    {
        var tracker = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L, capacity: 2);

        tracker.Observe(
            CreateUpdate(channelMessageId: 701, discussionMessageId: 801, isAutomaticForward: true, discussionId: -100200L, channelId: -100100L)
        );
        tracker.Observe(
            CreateUpdate(channelMessageId: 702, discussionMessageId: 802, isAutomaticForward: true, discussionId: -100200L, channelId: -100100L)
        );
        tracker.Observe(
            CreateUpdate(channelMessageId: 703, discussionMessageId: 803, isAutomaticForward: true, discussionId: -100200L, channelId: -100100L)
        );

        int? evicted = await tracker.WaitAsync(channelMessageId: 701, TimeSpan.FromMilliseconds(milliseconds: 10), TestContext.Current.CancellationToken);

        int? retained = await tracker.WaitAsync(channelMessageId: 702, TimeSpan.FromSeconds(seconds: 1), TestContext.Current.CancellationToken);

        Assert.False(evicted.HasValue);
        Assert.Equal(expected: 802, retained);
    }

    /// <summary>Removes a cancelled waiter so the same channel message can be awaited again.</summary>
    [Fact]
    public async Task WaitCancellationAllowsAnotherWaitForTheSameMessageAsync()
    {
        var tracker = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<int?> cancelledWait = tracker.WaitAsync(channelMessageId: 701, TimeSpan.FromSeconds(seconds: 1), cancellation.Token);
        await cancellation.CancelAsync();
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        Assert.Equal(cancellation.Token, failure.CancellationToken);

        Task<int?> nextWait = tracker.WaitAsync(channelMessageId: 701, TimeSpan.FromSeconds(seconds: 1), TestContext.Current.CancellationToken);
        tracker.Observe(
            CreateUpdate(channelMessageId: 701, discussionMessageId: 801, isAutomaticForward: true, discussionId: -100200L, channelId: -100100L)
        );
        Assert.Equal(expected: 801, await nextWait.ConfigureAwait(continueOnCapturedContext: true));
    }

    /// <summary>Completes an existing waiter when its automatic forward arrives.</summary>
    [Fact]
    public async Task WaitCompletesWhenAutomaticForwardArrivesLaterAsync()
    {
        var tracker = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);

        Task<int?> wait = tracker.WaitAsync(channelMessageId: 701, TimeSpan.FromSeconds(seconds: 1), TestContext.Current.CancellationToken);

        tracker.Observe(
            CreateUpdate(channelMessageId: 701, discussionMessageId: 801, isAutomaticForward: true, discussionId: -100200L, channelId: -100100L)
        );

        Assert.Equal(expected: 801, await wait.ConfigureAwait(continueOnCapturedContext: true));
    }

    /// <summary>Ignores updates that cannot represent the configured channel's discussion forward.</summary>
    [Theory]
    [MemberData(nameof(UnrelatedUpdateCases))]
    public async Task WaitIgnoresUnrelatedUpdatesAsync(bool isAutomaticForward, long discussionId, long channelId)
    {
        var tracker = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);
        tracker.Observe(CreateUpdate(channelMessageId: 701, discussionMessageId: 801, isAutomaticForward, discussionId, channelId));

        int? result = await tracker.WaitAsync(channelMessageId: 701, TimeSpan.FromMilliseconds(milliseconds: 10), TestContext.Current.CancellationToken);

        Assert.False(result.HasValue);
    }

    /// <summary>Returns a matching automatic forward observed before the waiter is registered.</summary>
    [Fact]
    public async Task WaitReturnsAutomaticForwardObservedBeforeWaitAsync()
    {
        var tracker = new TelegramDiscussionMessageTracker(channelId: -100100L, discussionId: -100200L);
        tracker.Observe(
            CreateUpdate(channelMessageId: 701, discussionMessageId: 801, isAutomaticForward: true, discussionId: -100200L, channelId: -100100L)
        );

        int? result = await tracker.WaitAsync(channelMessageId: 701, TimeSpan.FromSeconds(seconds: 1), TestContext.Current.CancellationToken);

        Assert.Equal(expected: 801, result);
    }

    private static Update CreateUpdate(int channelMessageId, int discussionMessageId, bool isAutomaticForward, long discussionId, long channelId)
    {
        return new()
        {
            Id = discussionMessageId + 100,
            Message = new()
            {
                Id = discussionMessageId,
                Date = new DateTime(year: 2026, month: 7, day: 24, hour: 12, minute: 0, second: 0, DateTimeKind.Utc),
                Chat = new() { Id = discussionId, Type = ChatType.Supergroup },
                IsAutomaticForward = isAutomaticForward,
                ForwardOrigin = new MessageOriginChannel
                {
                    Date = new DateTime(year: 2026, month: 7, day: 24, hour: 12, minute: 0, second: 0, DateTimeKind.Utc),
                    Chat = new() { Id = channelId, Type = ChatType.Channel },
                    MessageId = channelMessageId
                }
            }
        };
    }
}
