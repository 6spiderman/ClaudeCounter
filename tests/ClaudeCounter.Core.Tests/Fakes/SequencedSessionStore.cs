using ClaudeCounter.Core.Auth;

namespace ClaudeCounter.Tests.Fakes;

/// <summary>
/// Returns a different value on each successive <see cref="Read"/>, to
/// simulate another process rotating the session between TokenProvider's
/// first read and the lock-protected re-read (CC-04). Once the queue is
/// drained, further reads keep returning the last value.
/// </summary>
public sealed class SequencedSessionStore : ISessionStore
{
    private readonly Queue<OAuthSession?> _reads;
    private OAuthSession? _current;

    public SequencedSessionStore(params OAuthSession?[] reads) => _reads = new Queue<OAuthSession?>(reads);

    public int ReadCount { get; private set; }
    public int Writes { get; private set; }
    public int Clears { get; private set; }

    public OAuthSession? Read()
    {
        ReadCount++;
        if (_reads.Count > 0)
            _current = _reads.Dequeue();
        return _current;
    }

    public void Write(OAuthSession session)
    {
        _current = session;
        Writes++;
    }

    public void Clear()
    {
        _current = null;
        Clears++;
    }
}
