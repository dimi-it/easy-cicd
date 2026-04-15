using EasyCicd.Queue;

namespace EasyCicd.Tests.Queue;

public class JobQueueTests
{
    [Fact]
    public async Task Enqueue_SingleJob_CanBeDequeued()
    {
        var queue = new JobQueue();
        var job = new DeployJob("my-app", "abc123", "fix bug");

        queue.Enqueue(job);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        Assert.Equal("abc123", dequeued.CommitSha);
    }

    [Fact]
    public async Task Enqueue_MultipleJobs_OnlyLatestKept()
    {
        var queue = new JobQueue();

        queue.Enqueue(new DeployJob("my-app", "aaa", "first"));
        queue.Enqueue(new DeployJob("my-app", "bbb", "second"));
        queue.Enqueue(new DeployJob("my-app", "ccc", "third"));

        var dequeued = await queue.DequeueAsync(CancellationToken.None);

        Assert.Equal("ccc", dequeued.CommitSha);
        Assert.Equal("third", dequeued.CommitMessage);
    }

    [Fact]
    public async Task Enqueue_AfterDequeue_SecondJobAvailable()
    {
        var queue = new JobQueue();

        queue.Enqueue(new DeployJob("my-app", "aaa", "first"));
        var first = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("aaa", first.CommitSha);

        queue.Enqueue(new DeployJob("my-app", "bbb", "second"));
        var second = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal("bbb", second.CommitSha);
    }

    [Fact]
    public async Task DequeueAsync_EmptyQueue_WaitsForEnqueue()
    {
        var queue = new JobQueue();
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var dequeueTask = queue.DequeueAsync(cts.Token);

        Assert.False(dequeueTask.IsCompleted);

        queue.Enqueue(new DeployJob("my-app", "abc", "test"));

        var result = await dequeueTask;
        Assert.Equal("abc", result.CommitSha);
    }

    [Fact]
    public async Task DequeueAsync_Cancelled_ThrowsOperationCanceled()
    {
        var queue = new JobQueue();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(
            () => queue.DequeueAsync(cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }
}
