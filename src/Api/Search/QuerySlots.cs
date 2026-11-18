namespace Urlog.Api.Search;

/// <summary>
/// How many statements the API runs on the event store at once (design record, "Security", query path): a number
/// for each user and a number for all of them. A request takes what it needs before its first byte, one slot for
/// each statement it may have in flight, and gives it back when it ends. One that cannot have its slots within
/// <see cref="Wait"/> is answered 429 with <c>Retry-After</c>; a stream that has started never is, because it
/// holds what it needs.
/// <para>
/// The numbers come from what one search costs: three statements at once (histogram, page, counts). A user has
/// four, a search and one more request beside it; all users have twelve, three analysts at their limit, so one
/// user at the limit never takes a slot another user is entitled to. The event store's own limit for the query
/// account is above the total and is the backstop, not the working limit.
/// </para>
/// </summary>
public sealed class QuerySlots
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, int> _byUser = [];
    private readonly HashSet<long> _exporting = [];
    private readonly LinkedList<Waiter> _waiters = [];
    private int _inUse;

    public QuerySlots(int perUser, int total, TimeSpan wait)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perUser, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(total, perUser);
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);
        PerUser = perUser;
        Total = total;
        Wait = wait;
    }

    public int PerUser { get; }

    public int Total { get; }

    public TimeSpan Wait { get; }

    /// <summary>Statements in flight for all users.</summary>
    public int InUse
    {
        get
        {
            lock (_gate)
            {
                return _inUse;
            }
        }
    }

    /// <summary>
    /// Takes <paramref name="count"/> slots for <paramref name="userId"/>, waiting up to <see cref="Wait"/> for
    /// them. Null: busy. An export is one at a time for a user and is refused at once while another runs.
    /// </summary>
    public async Task<Lease?> AcquireAsync(long userId, int count, bool export = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (count > PerUser)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "a request never needs more than a user's slots");
        }

        Waiter waiter;
        lock (_gate)
        {
            if (export && _exporting.Contains(userId))
            {
                return null;
            }

            if (Fits(userId, count))
            {
                return Grant(userId, count, export);
            }

            waiter = new Waiter(userId, count, export);
            waiter.Node = _waiters.AddLast(waiter);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Wait);
        await using (timeout.Token.Register(static state => ((Waiter)state!).Source.TrySetResult(null), waiter).ConfigureAwait(false))
        {
            var lease = await waiter.Source.Task.ConfigureAwait(false);
            if (lease is null)
            {
                lock (_gate)
                {
                    // Granted in the instant the wait ended: the grant stands.
                    if (waiter.Granted is { } late)
                    {
                        return late;
                    }

                    _waiters.Remove(waiter.Node!);
                    waiter.Node = null;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            return lease;
        }
    }

    private bool Fits(long userId, int count) =>
        _inUse + count <= Total && _byUser.GetValueOrDefault(userId) + count <= PerUser;

    private Lease Grant(long userId, int count, bool export)
    {
        _inUse += count;
        _byUser[userId] = _byUser.GetValueOrDefault(userId) + count;
        if (export)
        {
            _exporting.Add(userId);
        }

        return new Lease(this, userId, count, export);
    }

    private void Release(long userId, int count, bool export)
    {
        lock (_gate)
        {
            _inUse -= count;
            var left = _byUser[userId] - count;
            if (left == 0)
            {
                _byUser.Remove(userId);
            }
            else
            {
                _byUser[userId] = left;
            }

            if (export)
            {
                _exporting.Remove(userId);
            }

            // In arrival order, but a waiter held back by its own user's limit does not hold back the others.
            for (var node = _waiters.First; node is not null;)
            {
                var next = node.Next;
                var waiter = node.Value;
                if (!(waiter.Export && _exporting.Contains(waiter.UserId)) && Fits(waiter.UserId, waiter.Count))
                {
                    _waiters.Remove(node);
                    waiter.Node = null;
                    waiter.Granted = Grant(waiter.UserId, waiter.Count, waiter.Export);
                    waiter.Source.TrySetResult(waiter.Granted);
                }

                node = next;
            }
        }
    }

    /// <summary>Slots held by one request; disposing gives them back, once.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly QuerySlots _owner;
        private readonly long _userId;
        private readonly bool _export;
        private int _released;

        internal Lease(QuerySlots owner, long userId, int count, bool export)
        {
            _owner = owner;
            _userId = userId;
            Count = count;
            _export = export;
        }

        public int Count { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release(_userId, Count, _export);
            }
        }
    }

    private sealed class Waiter(long userId, int count, bool export)
    {
        public long UserId { get; } = userId;

        public int Count { get; } = count;

        public bool Export { get; } = export;

        public TaskCompletionSource<Lease?> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }

        public Lease? Granted { get; set; }
    }
}
