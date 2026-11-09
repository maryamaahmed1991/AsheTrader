using AsheTrader.Integrations;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class AutoRetainerRelogRequestTests
{
    private const string Identity = "Ashe O'Rynn@Faerie";

    [Fact]
    public void LoggedInCharacterSwitchDoesNotConsultTitleScreenGate()
    {
        var titleChecks = 0;
        var commands = new List<string>();
        var result = AutoRetainerRelogRequest.TryQueue(Identity, true, true,
            () => { titleChecks++; return false; },
            command => { commands.Add(command); return true; }, out var error);

        Assert.True(result, error);
        Assert.Empty(error);
        Assert.Equal(0, titleChecks);
        Assert.Equal("/autoretainer relog Ashe O'Rynn@Faerie", Assert.Single(commands));
    }

    [Fact]
    public void LoggedInLoadingStateDoesNotDispatchOrConsultTitleScreenGate()
    {
        var result = AutoRetainerRelogRequest.TryQueue(Identity, true, false,
            () => throw new InvalidOperationException("Title-only IPC must not be invoked in-game."),
            _ => throw new InvalidOperationException("A loading character must not start a relog."), out var error);

        Assert.False(result);
        Assert.Contains("finish loading", error);
    }

    [Fact]
    public void ReadyTitleScreenUsesSameDocumentedCommandAfterCheckingReadiness()
    {
        var calls = new List<string>();
        var result = AutoRetainerRelogRequest.TryQueue(Identity, false, false,
            () => { calls.Add("title-check"); return true; },
            command => { calls.Add(command); return true; }, out var error);

        Assert.True(result, error);
        Assert.Empty(error);
        Assert.Equal(new[] { "title-check", "/autoretainer relog Ashe O'Rynn@Faerie" }, calls);
    }

    [Fact]
    public void LoggedOutUnreadyScreenDoesNotDispatchACommand()
    {
        var titleChecks = 0;
        var result = AutoRetainerRelogRequest.TryQueue(Identity, false, false,
            () => { titleChecks++; return false; },
            _ => throw new InvalidOperationException("No login may be dispatched from an unready screen."), out var error);

        Assert.False(result);
        Assert.Equal(1, titleChecks);
        Assert.Contains("fully loaded title screen", error);
    }

    [Fact]
    public void MissingCommandHandlerReportsFailureWithoutRetryOrTitleFallback()
    {
        var commands = new List<string>();
        var result = AutoRetainerRelogRequest.TryQueue(Identity, true, true,
            () => throw new InvalidOperationException("No title-screen fallback is allowed in-game."),
            command => { commands.Add(command); return false; }, out var error);

        Assert.False(result);
        Assert.Single(commands);
        Assert.Contains("command handler is unavailable", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ashe O'Rynn@Faerie\n/autoretainer multi enable")]
    [InlineData("Ashe O'Rynn@Faerie\r")]
    [InlineData("Ashe\tO'Rynn@Faerie")]
    [InlineData("Ashe O'Rynn@Faerie\0")]
    public void InvalidIdentityIsRejectedBeforeAnyGateOrCommand(string? identity)
    {
        var result = AutoRetainerRelogRequest.TryQueue(identity!, false, false,
            () => throw new InvalidOperationException("An invalid identity cannot query readiness."),
            _ => throw new InvalidOperationException("An invalid identity cannot dispatch a command."), out var error);

        Assert.False(result);
        Assert.Contains("invalid character identity", error);
    }

    [Fact]
    public void CommandExceptionPropagatesWithoutImplicitRetry()
    {
        var commandCalls = 0;
        var failure = new InvalidOperationException("Command handler failed.");
        var thrown = Assert.Throws<InvalidOperationException>(() => AutoRetainerRelogRequest.TryQueue(Identity, true, true,
            () => throw new InvalidOperationException("Title-only IPC must not be invoked in-game."),
            _ => { commandCalls++; throw failure; }, out _));

        Assert.Same(failure, thrown);
        Assert.Equal(1, commandCalls);
    }
}
