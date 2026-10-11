using System.Collections.Immutable;

using Nito.AsyncEx;

using Telegram.Bot.Types;

namespace ThermalWatch.Telegram;

internal sealed class TelegramDiscussionMessageTracker(long channelId, long discussionId, int capacity = 256)
{
    private ImmutableDictionary<int, int> _observed = [];
    private readonly Queue<int> _observationOrder = [];
    private readonly AsyncLock _sync = new();
    private ImmutableDictionary<int, TaskCompletionSource<int>> _waiters = [];

    internal void Observe(Update update)
    {
        if (update.Message is not { IsAutomaticForward: true } message || message.Chat.Id != discussionId)
            return;

        if (message.ForwardOrigin is not MessageOriginChannel origin || origin.Chat.Id != channelId)
            return;

        int channelMessageId = origin.MessageId;

        using (_sync.Lock())
        {
            if (_waiters.TryGetValue(channelMessageId, out TaskCompletionSource<int>? waiter))
            {
                _waiters = _waiters.Remove(channelMessageId);
                waiter.SetResult(message.Id);

                return;
            }

            if (!_observed.ContainsKey(channelMessageId))
            {
                _observed = _observed.SetItem(channelMessageId, message.Id);
                _observationOrder.Enqueue(channelMessageId);
            }

            while (_observationOrder.Count > capacity)
                _observed = _observed.Remove(_observationOrder.Dequeue());
        }
    }

    internal async Task<int?> WaitAsync(int channelMessageId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<int> waiter;

        using (await _sync.LockAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
        {
            if (_observed.TryGetValue(channelMessageId, out int discussionMessageId))
            {
                _observed = _observed.Remove(channelMessageId);

                return discussionMessageId;
            }

            waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);

            if (_waiters.ContainsKey(channelMessageId))
                throw new InvalidOperationException(message: "A Telegram discussion-message waiter already exists.");

            _waiters = _waiters.Add(channelMessageId, waiter);
        }

        try
        {
            return await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            // Cleanup must remove the waiter even when its caller has cancelled.
            using (await _sync.LockAsync(CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false))
            {
                if (_waiters.TryGetValue(channelMessageId, out TaskCompletionSource<int>? current) && ReferenceEquals(current, waiter))
                    _waiters = _waiters.Remove(channelMessageId);
            }
        }
    }
}
