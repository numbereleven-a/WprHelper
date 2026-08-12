using WprHelper.Contracts;
using WprHelper.Core;

namespace WprHelper.Core.Tests;

public sealed class StateMachineTests
{
    [Fact]
    public void ValidLifecycle_IsAccepted()
    {
        var state = new CaptureStateMachine();
        foreach (var next in new[] { CaptureState.Validating, CaptureState.Preparing, CaptureState.WaitingForElevation, CaptureState.StartingWpr, CaptureState.WaitingForWpr, CaptureState.LaunchingTarget, CaptureState.Capturing, CaptureState.StopRequested, CaptureState.StoppingWpr, CaptureState.Finalizing, CaptureState.Completed })
            state.TransitionTo(next);
        Assert.Equal(CaptureState.Completed, state.State);
    }

    [Fact]
    public void InvalidTransition_IsRejected() => Assert.Throws<InvalidOperationException>(() => new CaptureStateMachine().TransitionTo(CaptureState.Capturing));
}

public sealed class StopConditionTests
{
    private readonly StopConditionEvaluator _evaluator = new();
    [Fact] public void DurationWins() => Assert.Equal(StopReason.DurationReached, _evaluator.Evaluate(new() { MaximumDuration = TimeSpan.FromMinutes(1), MinimumFreeBytes = 1 }, TimeSpan.FromMinutes(1), 0, 100, false, null));
    [Fact] public void EtlSizeDoesNotStopWprBecauseFileIsCreatedOnStop() => Assert.Equal(StopReason.None, _evaluator.Evaluate(new() { StopAfterTargetExit = false, MinimumFreeBytes = 1 }, TimeSpan.Zero, 100, 1000, false, null));
    [Fact] public void DelayedTargetExitWaits() => Assert.Equal(StopReason.None, _evaluator.Evaluate(new() { StopAfterTargetExit = true, TargetExitDelay = TimeSpan.FromSeconds(10), MinimumFreeBytes = 1 }, TimeSpan.Zero, 0, 1000, true, TimeSpan.FromSeconds(9)));
    [Fact] public void FreeReserveTriggers() => Assert.Equal(StopReason.FreeSpaceReserveReached, _evaluator.Evaluate(new() { MinimumFreeBytes = 100 }, TimeSpan.Zero, 0, 100, false, null));
}

public sealed class FileNameTests
{
    [Fact]
    public void TokensAreInvariantAndTraversalIsRemoved()
    {
        var value = FileNameTemplate.Expand("../{AppName}_{DateTime}_{SessionId}", new("bad:name", "p", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), 10, new DateTimeOffset(2026, 8, 4, 20, 15, 30, TimeSpan.Zero)));
        Assert.DoesNotContain("..", value); Assert.DoesNotContain('/', value); Assert.Contains("2026-08-04_20-15-30", value);
    }
    [Fact] public void ReservedNameIsPrefixed() => Assert.Equal("_CON", FileNameTemplate.Expand("CON", new("a", "p", Guid.NewGuid(), null, DateTimeOffset.Now)));
    [Fact] public void ReservedDeviceNameBeforeDotIsPrefixed() => Assert.Equal("_CON.log", FileNameTemplate.Expand("CON.log", new("a", "p", Guid.NewGuid(), null, DateTimeOffset.Now)));
    [Fact]
    public void TruncatedNameDoesNotEndWithDotOrSpace()
    {
        var value = FileNameTemplate.Expand(new string('a', 179) + " .tail", new("a", "p", Guid.NewGuid(), null, DateTimeOffset.Now));
        Assert.True(value.Length <= 180); Assert.False(value.EndsWith('.')); Assert.False(value.EndsWith(' '));
    }
    [Fact]
    public void TruncationDoesNotSplitSurrogatePair()
    {
        var value = FileNameTemplate.Expand(new string('a', 179) + "😀tail", new("a", "p", Guid.NewGuid(), null, DateTimeOffset.Now));
        Assert.False(char.IsHighSurrogate(value[^1]));
        Assert.True(value.Length <= 180);
    }
}

public sealed class ProfileValidationTests
{
    [Theory]
    [InlineData("service", "service.exe")]
    [InlineData("SERVICE.EXE", "SERVICE.EXE")]
    public void ProcessNamesAreNormalized(string input, string expected) => Assert.Equal(expected, ProfileValidator.NormalizeProcessName(input));
    [Fact] public void PathLikeProcessNameIsRejected() => Assert.False(ProfileValidator.IsValidProcessName("..\\service.exe"));
}
