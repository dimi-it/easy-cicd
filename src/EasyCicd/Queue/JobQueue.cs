using System.Threading.Channels;

namespace EasyCicd.Queue;

public class JobQueue
{
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(1);
    private readonly object _lock = new();
    private DeployJob? _pending;

    public void Enqueue(DeployJob job)
    {
        lock (_lock)
        {
            _pending = job;
        }

        _signal.Writer.TryWrite(true);
    }

    public async Task<DeployJob> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.Reader.ReadAsync(cancellationToken);

            lock (_lock)
            {
                if (_pending is not null)
                {
                    var job = _pending;
                    _pending = null;
                    return job;
                }
            }
        }
    }
}
