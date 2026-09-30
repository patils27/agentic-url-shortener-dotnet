// Per-IP token-bucket rate limiter (in-memory, thread-safe).

namespace AgenticUrlShortener.Service;

public sealed class TokenBucket
{
    private readonly double _capacity;
    private double _tokens;
    private readonly double _refillPerSec;
    private double _updated = NowMonotonic();
    private readonly object _lock = new();

    public TokenBucket(double ratePerMinute, int burst)
    {
        _capacity = burst;
        _tokens = burst;
        _refillPerSec = ratePerMinute / 60.0;
    }

    private static double NowMonotonic() =>
        (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Return (allowed, retryAfterSeconds).</summary>
    public (bool Allowed, double RetryAfter) Allow()
    {
        lock (_lock)
        {
            var now = NowMonotonic();
            _tokens = Math.Min(_capacity, _tokens + (now - _updated) * _refillPerSec);
            _updated = now;
            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                return (true, 0.0);
            }
            return (false, (1.0 - _tokens) / _refillPerSec);
        }
    }
}

/// <summary>Maps client keys (IPs) to token buckets.</summary>
public sealed class RateLimiter
{
    private readonly double _rate;
    private readonly int _burst;
    private readonly Dictionary<string, TokenBucket> _buckets = new();
    private readonly object _lock = new();

    public RateLimiter(double ratePerMinute = 60.0, int burst = 10)
    {
        _rate = ratePerMinute;
        _burst = burst;
    }

    public (bool Allowed, double RetryAfter) Allow(string key)
    {
        TokenBucket bucket;
        lock (_lock)
        {
            if (!_buckets.TryGetValue(key, out bucket!))
            {
                bucket = new TokenBucket(_rate, _burst);
                _buckets[key] = bucket;
            }
        }
        return bucket.Allow();
    }
}
