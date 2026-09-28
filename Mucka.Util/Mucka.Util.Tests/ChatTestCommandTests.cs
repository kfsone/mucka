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

    [Theory]
    [InlineData(0.0, 500)]
    [InlineData(0.5, 4750)]
    [InlineData(1.0, 9000)]
    [InlineData(-3.0, 500)]
    [InlineData(7.0, 9000)]
    public void DelayFor_SpansHalfASecondToNine(double draw, int expectedMs)
        => Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), ChatTestSlowRun.DelayFor(draw));

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
        Assert.Equal(new[] { 500.0, 9000.0, 2625.0 }, pauses.Asked.Select(p => p.Delay.TotalMilliseconds));
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
