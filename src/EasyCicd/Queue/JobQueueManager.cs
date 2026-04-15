using System.Collections.Concurrent;

namespace EasyCicd.Queue;

public class JobQueueManager
{
    private readonly ConcurrentDictionary<string, JobQueue> _queues = new();

    public JobQueue GetOrCreate(string repoName)
    {
        return _queues.GetOrAdd(repoName, _ => new JobQueue());
    }

    public void Enqueue(string repoName, DeployJob job)
    {
        GetOrCreate(repoName).Enqueue(job);
    }

    public IReadOnlyCollection<string> GetActiveRepoNames()
    {
        return _queues.Keys.ToList().AsReadOnly();
    }

    public bool TryRemove(string repoName)
    {
        return _queues.TryRemove(repoName, out _);
    }
}
