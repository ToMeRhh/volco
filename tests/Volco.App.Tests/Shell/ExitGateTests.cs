using Xunit;

namespace Volco.App.Tests.Shell;

public class ExitGateTests
{
    [Fact]
    public void AnExitDuringAWrite_RunsOnceTheWriteEnds_ExactlyOnce()
    {
        var busy = true;
        var exits = 0;
        var gate = new ExitGate(() => busy);
        Assert.True(gate.ShouldDefer());
        gate.Defer(() => exits++);
        gate.Defer(() => exits++);           // a second Exit click: still one exit
        gate.OnBusyChanged();
        Assert.Equal(0, exits);
        busy = false;
        gate.OnBusyChanged();
        gate.OnBusyChanged();
        Assert.Equal(1, exits);
    }

    [Fact]
    public void NotBusy_NothingIsDeferred() => Assert.False(new ExitGate(() => false).ShouldDefer());
}
