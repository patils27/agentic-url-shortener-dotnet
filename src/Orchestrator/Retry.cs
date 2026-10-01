// Bounded retries with exponential backoff, fallback chain, and rollback hooks.
//
// Every task execution is wrapped by Retry.ExecuteWithRetry:
//   1. Try the primary callable up to max_attempts (bounded — never infinite).
//   2. Between attempts, sleep backoff_base * 2**(attempt-1) (+ small jitter).
//   3. On exhaustion, run the fallback callable if one is registered.
//   4. Whether the fallback succeeds or not, run rollback hooks to undo any
//     partial side effects (compensation), recorded in the audit log.
//
// The returned RetryReport feeds the metrics collector (retry counts, MTTR).

namespace AgenticUrlShortener.Orchestrator;

public sealed class RetryReport
{
    public required bool Succeeded { get; init; }
    public required int Attempts { get; init; }
    public required int Retries { get; init; }
    public required bool FallbackUsed { get; init; }
    public required bool FallbackSucceeded { get; init; }
    public required bool RollbackRun { get; init; }
    public required double TotalDurationS { get; init; }
    public double? FirstFailureAt { get; init; }
    public double? RecoveredAt { get; init; }
    public string LastError { get; init; } = string.Empty;
    public object? Result { get; init; }
    /// <summary>Mean time to recover (failure -> success).</summary>
    public double? MttrS => FirstFailureAt.HasValue && RecoveredAt.HasValue
        ? RecoveredAt.Value - FirstFailureAt.Value
        : null;
}

public static class Retry
{
    private static double Backoff(double baseSeconds, int attempt)
    {
        // exponential backoff with jitter; attempt is 1-based (attempt that failed)
        return baseSeconds * Math.Pow(2, attempt - 1) +
               Random.Shared.NextDouble() * baseSeconds * 0.5;
    }

    private static double NowMonotonic() =>
        (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    public static RetryReport ExecuteWithRetry(
        Func<object?> fn,
        int maxAttempts = 3,
        double backoffBase = 0.5,
        Func<object?>? fallback = null,
        IEnumerable<Action>? rollbackHooks = null,
        Action<int, double, Exception>? onRetry = null,
        Action<int, Exception>? onFailure = null,
        Action<double>? sleepFn = null,
        CancellationToken cancellationToken = default,
        params Type[] fatalExceptions)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "max_attempts must be >= 1");

        sleepFn ??= seconds =>
        {
            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds)))
                cancellationToken.ThrowIfCancellationRequested();
        };
        var start = NowMonotonic();
        double? firstFailureAt = null;
        var lastError = string.Empty;
        var attempts = 0;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts = attempt;
            try
            {
                var result = fn();
                cancellationToken.ThrowIfCancellationRequested();
                var recovered = NowMonotonic();
                return new RetryReport
                {
                    Succeeded = true, Attempts = attempts, Retries = attempt - 1,
                    FallbackUsed = false, FallbackSucceeded = false, RollbackRun = false,
                    TotalDurationS = recovered - start,
                    FirstFailureAt = firstFailureAt, RecoveredAt = recovered,
                    Result = result,
                };
            }
            catch (Exception exc) // retry boundary is intentional
            {
                lastError = $"{exc.GetType().Name}: {exc.Message}";
                if (fatalExceptions.Any(t => t.IsInstanceOfType(exc)))
                {
                    // Fail closed immediately: policy violations etc. are never
                    // retried — the engine converts them into a safe-stop.
                    throw;
                }
                cancellationToken.ThrowIfCancellationRequested();
                firstFailureAt ??= NowMonotonic();
                onFailure?.Invoke(attempt, exc);
                if (attempt < maxAttempts)
                {
                    var delay = Backoff(backoffBase, attempt);
                    onRetry?.Invoke(attempt, delay, exc);
                    sleepFn(delay);
                }
                // else: exhausted — fall through to fallback/rollback
            }
        }

        // ---- bounded retries exhausted: fallback chain ---------------------
        var fallbackUsed = fallback is not null;
        var fallbackSucceeded = false;
        object? fallbackResult = null;
        if (fallback is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                fallbackResult = fallback();
                cancellationToken.ThrowIfCancellationRequested();
                fallbackSucceeded = true;
            }
            catch (Exception exc)
            {
                if (fatalExceptions.Any(t => t.IsInstanceOfType(exc)))
                    throw;
                cancellationToken.ThrowIfCancellationRequested();
                lastError = $"fallback failed: {exc.GetType().Name}: {exc.Message}";
            }
        }

        // ---- rollback / compensation ---------------------------------------
        var rollbackRun = false;
        if (rollbackHooks is not null)
        {
            foreach (var hook in rollbackHooks)
            {
                rollbackRun = true;
                try { hook(); }
                catch { /* rollback must be best-effort */ }
            }
        }

        var end = NowMonotonic();
        return new RetryReport
        {
            Succeeded = fallbackSucceeded, Attempts = attempts,
            Retries = maxAttempts - 1, FallbackUsed = fallbackUsed,
            FallbackSucceeded = fallbackSucceeded, RollbackRun = rollbackRun,
            TotalDurationS = end - start, FirstFailureAt = firstFailureAt,
            RecoveredAt = fallbackSucceeded ? end : null,
            LastError = lastError, Result = fallbackResult,
        };
    }
}
