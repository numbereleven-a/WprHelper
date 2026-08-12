using WprHelper.Contracts;

namespace WprHelper.Core;

public sealed class StopConditionEvaluator
{
    public StopReason Evaluate(StopOptions options, TimeSpan elapsed, long etlBytes, long freeBytes, bool targetExited, TimeSpan? timeSinceTargetExit)
    {
        if (options.MaximumDuration is { } duration && elapsed >= duration) return StopReason.DurationReached;
        if (freeBytes <= options.MinimumFreeBytes) return StopReason.FreeSpaceReserveReached;
        if (options.StopAfterTargetExit && targetExited && timeSinceTargetExit >= options.TargetExitDelay) return StopReason.TargetExited;
        return StopReason.None;
    }
}
