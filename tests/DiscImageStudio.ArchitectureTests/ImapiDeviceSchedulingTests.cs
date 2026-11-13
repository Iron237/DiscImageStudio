using DiscImageStudio.Burning;

internal static class ImapiDeviceSchedulingTests
{
    internal static void Run()
    {
        TestInFlightProbeFinishesBeforeBurn().GetAwaiter().GetResult();
        TestCancelledNativeWorkKeepsOwnership().GetAwaiter().GetResult();
        TestNativeFailureReleasesOwnership().GetAwaiter().GetResult();
        Console.WriteLine("imapi-device-scheduling: passed (probe/burn STA work cannot overlap, cancelled native work drains, failures release ownership; no devices)");
    }

    private static async Task TestInFlightProbeFinishesBeforeBurn()
    {
        using ManualResetEventSlim releaseProbe = new();
        TaskCompletionSource probeStarted = NewSignal(), burnStarted = NewSignal();
        Task<int> probe = ImapiCom.RunOnStaThread(() =>
        {
            Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "the device owner stays on an STA thread");
            probeStarted.SetResult();
            Require(releaseProbe.Wait(TimeSpan.FromSeconds(5)), "probe fixture was released");
            return 1;
        });
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Task<int> burn = ImapiCom.RunOnStaThread(() => { burnStarted.SetResult(); return 2; });
        try
        {
            Task observed = await Task.WhenAny(burnStarted.Task, Task.Delay(250)).ConfigureAwait(false);
            Require(observed != burnStarted.Task, "a pending sidebar read must finish before the burn's native work starts");
        }
        finally
        {
            releaseProbe.Set();
            await Task.WhenAll(probe, burn).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        Require(probe.Result == 1 && burn.Result == 2, "both clients retain their results");
    }

    private static async Task TestCancelledNativeWorkKeepsOwnership()
    {
        using ManualResetEventSlim releaseWrite = new();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource writeStarted = NewSignal(), probeStarted = NewSignal();
        Task<int> write = ImapiCom.RunOnStaThread(() =>
        {
            writeStarted.SetResult();
            Require(releaseWrite.Wait(TimeSpan.FromSeconds(5)), "write fixture was released");
            cancellation.Token.ThrowIfCancellationRequested();
            return 1;
        });
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        cancellation.Cancel();
        Task<int> probe = ImapiCom.RunOnStaThread(() => { probeStarted.SetResult(); return 2; });
        try
        {
            Task observed = await Task.WhenAny(probeStarted.Task, Task.Delay(250)).ConfigureAwait(false);
            Require(observed != probeStarted.Task && !write.IsCompleted,
                "cancelling managed work cannot start a probe while its native command is still running");
        }
        finally
        {
            releaseWrite.Set();
            try { await write.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            await probe.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        Require(probe.Result == 2, "a probe can resume after the cancelled native task returns");
    }

    private static async Task TestNativeFailureReleasesOwnership()
    {
        InvalidOperationException expected = new("simulated native failure");
        try
        {
            await ImapiCom.RunOnStaThread<int>(() => throw expected).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            throw new Exception("Expected the original native failure.");
        }
        catch (InvalidOperationException actual) when (ReferenceEquals(expected, actual)) { }
        Require(await ImapiCom.RunOnStaThread(() => 3).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false) == 3,
            "failed native work cannot strand all later device operations");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("IMAPI device scheduling: " + message);
    }
}
