namespace MotorBench.Web;

/// <summary>
/// Only one browser tab operates the bench at a time; others watch. The holder
/// keeps the lock by polling status. If its page closes, the lock expires and
/// someone else can take control. Stop and abort never need the lock.
/// </summary>
public sealed class ControlLock(TimeProvider time)
{
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private string? _user;
    private string? _client;
    private DateTimeOffset _seen;

    /// <summary>Holder's user name, or null if nobody holds an unexpired lock.</summary>
    public string? Holder
    {
        get
        {
            lock (_sync)
                return Active ? _user : null;
        }
    }

    public void Seen(string user, string? client)
    {
        lock (_sync)
        {
            if (Matches(user, client) && Active)
                _seen = time.GetUtcNow();
        }
    }

    public bool Holds(string user, string? client)
    {
        lock (_sync)
            return Matches(user, client) && Active;
    }

    /// <summary>Takes the lock if it is free, expired, or already ours. Otherwise returns false.</summary>
    public bool TryTake(string user, string client)
    {
        lock (_sync)
        {
            if (Active && !Matches(user, client))
                return false;
            _user = user;
            _client = client;
            _seen = time.GetUtcNow();
            return true;
        }
    }

    public void Release(string user, string? client)
    {
        lock (_sync)
        {
            if (Matches(user, client))
                _user = _client = null;
        }
    }

    private bool Active => _client is not null && time.GetUtcNow() - _seen <= Expiry;

    private bool Matches(string user, string? client) => client is not null && _client == client && _user == user;
}
