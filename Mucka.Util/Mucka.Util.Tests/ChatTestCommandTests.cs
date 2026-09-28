using Mucka.Commands;

namespace Mucka.Util.Tests;

/// <summary>
/// <c>$CHATTEST [slow|stop]</c>: the argument, and the slow run's pacing and stopping. The pause is
/// injected, so nothing here waits on a clock: each pause is a task the test releases by hand.
/// </summary>
public class ChatTestCommandTests
{
    // A run that is never stopped or released fails here instead of hanging the suite.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("", ChatTestMode.All)]
    [InlineData("   ", ChatTestMode.All)]
    [InlineData("slow", ChatTestMode.Slow)]
    [InlineData("SLOW", ChatTestMode.Slow)]
    [InlineData(" Slow ", ChatTestMode.Slow)]
    [InlineData("stop", ChatTestMode.Stop)]
    [InlineData("STOP", ChatTestMode.Stop)]
    [InlineData("fast", ChatTestMode.Unknown)]
    [InlineData("slow stop", ChatTestMode.Unknown)]
    [InlineData("slowly", ChatTestMode.Unknown)]
    public void Parse(string argument, ChatTestMode expected)
        => Assert.Equal(expected, ChatTestCommand.Parse(argument));

    // 0.5s - ln(1 - u) seconds, capped at 9s: u = 1 - e^-8.5 is where the cap begins.
    [Theory]
    [InlineData(0.0, 500.0)]
    [InlineData(0.5, 1193.147)]           // the median: 0.5 + ln 2
    [InlineData(0.99, 5105.170)]          // 0.5 + ln 100: one pause in a hundred is longer
    [InlineData(0.9999, 9000.0)]          // 0.5 + ln 10000 = 9.71, past the cap
    [InlineData(0.9999999999999999, 9000.0)]
    [InlineData(1.0, 9000.0)]
    [InlineData(-3.0, 500.0)]
    [InlineData(7.0, 9000.0)]
    public void DelayFor_IsHalfASecondPlusAnExponentialSecond_CappedAtNine(double draw, double expectedMs)
        => Assert.Equal(expectedMs, ChatTestSlowRun.DelayFor(draw).TotalMilliseconds, 0.01);

    [Fact]
    public void DelayFor_JustBelowTheCap_IsNotCapped()
    {
        double capStart = 1.0 - Math.Exp(-8.5);
        Assert.True(ChatTestSlowRun.DelayFor(capStart - 1e-6) < ChatTestSlowRun.MaxDelay);
        Assert.Equal(ChatTestSlowRun.MaxDelay.TotalMilliseconds, ChatTestSlowRun.DelayFor(capStart + 1e-9).TotalMilliseconds, 0.01);
    }

    /// <summary>The operator's rule: the average interval is 1.5s and 9s is an outlier. Over a
    /// seeded run the mean sits at the analytic 0.5 + (1 - e^-8.5) = 1.4998s, about 1.1% of pauses
    /// pass 5s, and none leaves [0.5s, 9s].</summary>
    [Fact]
    public void Delays_AverageOneAndAHalfSeconds_WithNineAnOutlier()
    {
        var random = new Random(20260928);
        const int n = 200_000;
        double sum = 0;
        int overFive = 0, atCap = 0;
        for (int i = 0; i < n; i++)
        {
            var d = ChatTestSlowRun.DelayFor(random.NextDouble());
            Assert.InRange(d, ChatTestSlowRun.MinDelay, ChatTestSlowRun.MaxDelay);
            sum += d.TotalSeconds;
            if (d.TotalSeconds > 5) overFive++;
            if (d == ChatTestSlowRun.MaxDelay) atCap++;
        }
        double analytic = 0.5 + (1 - Math.Exp(-8.5));
        Assert.Equal(analytic, sum / n, 0.01);
        Assert.InRange(overFive / (double)n, 0.009, 0.013);   // e^-4.5 = 0.0111
        Assert.InRange(atCap / (double)n, 0.0, 0.001);        // e^-8.5 = 0.0002
    }

    // Each pause the run asks for, held until the test releases it or the run cancels it.
    private sealed class Pauses
    {
        public readonly List<(TimeSpan Delay, TaskCompletionSource Gate, CancellationToken Token)> Asked = new();

        public Task Delay(TimeSpan delay, CancellationToken token)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => gate.TrySetCanceled(token));
            Asked.Add((delay, gate, token));
            return gate.Task;
        }

        public void ReleaseLast() => Asked[^1].Gate.TrySetResult();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(2);
        Assert.True(condition(), "timed out");
    }

    [Fact]
    public async Task EveryUnit_IsDeliveredAfterItsOwnPause_ThenFinished()
    {
        var draws = new Queue<double>(new[] { 0.0, 1.0, 0.25 });
        var pauses = new Pauses();
        var run = new ChatTestSlowRun(() => draws.Dequeue(), pauses.Delay);
        var delivered = new List<int>();
        bool finished = false;
        var task = run.Start(new[] { 1, 2, 3 }, delivered.Add, () => finished = true, out var stopped);
        Assert.False(stopped);

        for (int n = 1; n <= 3; n++)
        {
            await Until(() => pauses.Asked.Count == n);
            Assert.Equal(n - 1, delivered.Count);   // a pause comes before each unit, the first too
            pauses.ReleaseLast();
        }
        await task.WaitAsync(Bound);

        Assert.Equal(new[] { 1, 2, 3 }, delivered);
        Assert.Equal(new[] { 500.0, 9000.0, 787.68 }, pauses.Asked.Select(p => Math.Round(p.Delay.TotalMilliseconds, 2)));
        Assert.True(finished);
        Assert.False(run.IsRunning);
        Assert.False(run.Stop());   // a finished run is nothing to stop
    }

    [Fact]
    public async Task Stop_CancelsThePause_AndDeliversNothingMore()
    {
        var pauses = new Pauses();
        var run = new ChatTestSlowRun(() => 0.5, pauses.Delay);
        var delivered = new List<int>();
        bool finished = false;
        var task = run.Start(new[] { 1, 2, 3 }, delivered.Add, () => finished = true, out _);
        pauses.ReleaseLast();
        await Until(() => pauses.Asked.Count == 2);

        Assert.True(run.Stop());
        await task.WaitAsync(Bound);

        Assert.Equal(new[] { 1 }, delivered);
        Assert.True(pauses.Asked[1].Token.IsCancellationRequested);
        Assert.True(pauses.Asked[1].Gate.Task.IsCanceled);
        Assert.False(finished);
        Assert.False(run.IsRunning);
        Assert.False(run.Stop());
    }

    /// <summary>A pause that elapsed just before the stop still resumes afterwards; it must not
    /// deliver.</summary>
    [Fact]
    public async Task APauseThatElapsedBeforeTheStop_DeliversNothing()
    {
        var delivered = new List<int>();
        var gate = new TaskCompletionSource();
        // Ignores the token, as a pause whose continuation is already queued does.
        var run = new ChatTestSlowRun(() => 0.5, (_, _) => gate.Task);
        var task = run.Start(new[] { 1, 2 }, delivered.Add, () => { }, out _);

        Assert.True(run.Stop());
        gate.SetResult();
        await task.WaitAsync(Bound);

        Assert.Empty(delivered);
    }

    [Fact]
    public async Task Restart_StopsTheOldRun_AndTheOldRunLeavesTheNewOneAlone()
    {
        var pauses = new Pauses();
        var run = new ChatTestSlowRun(() => 0.5, pauses.Delay);
        var old = new List<int>();
        var fresh = new List<int>();
        var first = run.Start(new[] { 1, 2 }, old.Add, () => { }, out var stoppedFirst);
        var second = run.Start(new[] { 10, 20 }, fresh.Add, () => { }, out var stoppedSecond);
        Assert.False(stoppedFirst);
        Assert.True(stoppedSecond);

        await first.WaitAsync(Bound);
        Assert.Empty(old);
        Assert.True(run.IsRunning);   // the old run's ending did not clear the new one

        pauses.ReleaseLast();
        await Until(() => pauses.Asked.Count == 3);
        pauses.ReleaseLast();
        await second.WaitAsync(Bound);
        Assert.Equal(new[] { 10, 20 }, fresh);
        Assert.Empty(old);
    }

    [Fact]
    public async Task Start_AsksForARealPause_ByDefault()
    {
        var run = new ChatTestSlowRun();
        var task = run.Start(new[] { 1 }, _ => Assert.Fail("delivered before any pause"), () => { }, out _);
        Assert.True(run.IsRunning);
        Assert.True(run.Stop());
        await task.WaitAsync(Bound);
    }
}
