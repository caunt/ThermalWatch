using Telegram.Bot.Types;

namespace ThermalWatch.Telegram;

internal sealed class TelegramDiscussionMessageTracker(
    long channelId,
    long discussionId,
    int capacity = 256)
{
    private readonly Dictionary<int, int> _observed = [];
    private readonly Queue<int> _observationOrder = [];
    private readonly Lock _sync = new();
    private readonly Dictionary<int, TaskCompletionSource<int>> _waiters = [];

    internal void Observe(Update update)
    {
        if (update.Message is not
            {
                IsAutomaticForward: true,
                Chat.Id: var messageDiscussionId,
                ForwardOrigin: MessageOriginChannel
                {
                    Chat.Id: var originChannelId,
                    MessageId: var channelMessageId
                }
            } message
            || messageDiscussionId != discussionId
            || originChannelId != channelId)
        {
            return;
        }

        lock (_sync)
        {
            if (_waiters.Remove(channelMessageId, out TaskCompletionSource<int>? waiter))
            {
                waiter.TrySetResult(message.Id);
                return;
            }

            if (_observed.TryAdd(channelMessageId, message.Id))
                _observationOrder.Enqueue(channelMessageId);

            while (_observationOrder.Count > capacity)
                _observed.Remove(_observationOrder.Dequeue());
        }
    }

    internal async Task<int?> WaitAsync(
        int channelMessageId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<int> waiter;
        lock (_sync)
        {
            if (_observed.Remove(channelMessageId, out int discussionMessageId))
                return discussionMessageId;

            waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waiters.TryAdd(channelMessageId, waiter))
                throw new InvalidOperationException(message: "A Telegram discussion-message waiter already exists.");
        }

        try
        {
            return await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_sync)
            {
                if (_waiters.TryGetValue(channelMessageId, out TaskCompletionSource<int>? current)
                    && ReferenceEquals(current, waiter))
                {
                    _waiters.Remove(channelMessageId);
                }
            }
        }
    }
}
