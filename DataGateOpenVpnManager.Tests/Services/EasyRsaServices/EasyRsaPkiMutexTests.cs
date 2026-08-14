using DataGateOpenVpnManager.Services.EasyRsaServices;

namespace DataGateOpenVpnManager.Tests.Services.EasyRsaServices;

public class EasyRsaPkiMutexTests
{
    [Fact]
    public async Task AcquireAsync_SerializesConcurrentWaiters_OnSamePath()
    {
        var mutex = new EasyRsaPkiMutex();
        var path = Path.Combine(Path.GetTempPath(), "easyrsa-mutex-test");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var maxConcurrent = 0;

        async Task Worker()
        {
            await using var _ = await mutex.AcquireAsync(path);
            var now = Interlocked.Increment(ref entered);
            Interlocked.Exchange(ref maxConcurrent, Math.Max(Volatile.Read(ref maxConcurrent), now));
            await gate.Task;
            Interlocked.Decrement(ref entered);
        }

        var w1 = Worker();
        var w2 = Worker();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref entered));
        gate.SetResult();
        await Task.WhenAll(w1, w2);
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task AcquireAsync_IsReentrant_OnSameAsyncFlow()
    {
        var mutex = new EasyRsaPkiMutex();
        var path = Path.Combine(Path.GetTempPath(), "easyrsa-mutex-reentrant");

        await using var outer = await mutex.AcquireAsync(path);
        await using var inner = await mutex.AcquireAsync(path);
        Assert.NotNull(outer);
        Assert.NotNull(inner);
    }

    [Fact]
    public async Task AcquireAsync_AllowsParallel_OnDifferentPaths()
    {
        var mutex = new EasyRsaPkiMutex();
        var a = Path.Combine(Path.GetTempPath(), "easyrsa-a");
        var b = Path.Combine(Path.GetTempPath(), "easyrsa-b");
        var bothHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;

        async Task Hold(string path)
        {
            await using var _ = await mutex.AcquireAsync(path);
            if (Interlocked.Increment(ref held) == 2)
                bothHeld.TrySetResult();
            await release.Task;
        }

        var t1 = Hold(a);
        var t2 = Hold(b);
        await bothHeld.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref held));
        release.SetResult();
        await Task.WhenAll(t1, t2);
    }
}
