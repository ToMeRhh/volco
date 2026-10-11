using Xunit;

namespace Volco.App.Tests.Shell;

public class SingleInstanceTests
{
    private static string UniqueName() => $"VolcoTest-{Guid.NewGuid():N}"[..24];

    /// <summary>A named mutex belongs to a thread, so each "process" here claims and disposes on
    /// its own thread, as each real process does on its main thread.</summary>
    private static T OnOwnThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = e; }
        });
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
        return result;
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--background" }, true)]
    [InlineData(new[] { "--BACKGROUND" }, true)]
    [InlineData(new[] { "--other" }, false)]
    public void LaunchOptions_ReadTheBackgroundFlag(string[] args, bool background)
        => Assert.Equal(background, LaunchOptions.Parse(args).Background);

    [Fact]
    public void ASecondStart_AsksTheFirstToShow_AndABackgroundOneDoesNot()
    {
        var name = UniqueName();
        using var shown = new SemaphoreSlim(0);
        var release = new ManualResetEventSlim();
        var first = new Thread(() =>
        {
            using var claim = SingleInstance.Claim(name, showExisting: true, TimeSpan.FromSeconds(5));
            claim?.Listen(() => shown.Release());
            release.Wait();
        });
        first.Start();
        try
        {
            // The first holds the mutex once it listens; wait for the pipe to answer.
            Assert.True(SpinWait.SpinUntil(() => SingleInstance.TrySend(name, "ping", TimeSpan.FromMilliseconds(200)),
                                           TimeSpan.FromSeconds(10)));

            var background = OnOwnThread(() => SingleInstance.Claim(name, showExisting: false));
            Assert.Null(background);
            Assert.False(shown.Wait(300));

            var second = OnOwnThread(() => SingleInstance.Claim(name, showExisting: true, TimeSpan.FromSeconds(5)));
            Assert.Null(second);
            Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            first.Join();
        }

        // The first one is gone: the next start becomes the one Volco.
        Assert.True(OnOwnThread(() =>
        {
            using var claim = SingleInstance.Claim(name, showExisting: true, TimeSpan.FromSeconds(5));
            return claim is not null;
        }));
    }
}
