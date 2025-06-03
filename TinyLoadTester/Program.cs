using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;

var urlArg = new Argument<string>("url", "Target URL");
var durationArg = new Argument<int>("duration", "Test length, seconds");
var concurrencyArg = new Argument<int>("concurrency", () => 10, "Concurrent workers");
var rateLimiterArg = new Argument<int>("rate-limit", () => 0, "limit requests per second");

var root = new RootCommand("Tiny HTTP load-tester")
{
    urlArg,
    durationArg,
    concurrencyArg,
    rateLimiterArg
};

root.SetHandler(async context =>
{
    
    var rateLimit = context.ParseResult.GetValueForArgument(rateLimiterArg);
    var rateLimiter = rateLimit > 0
        ? new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit          = rateLimit,              // bucket size == 1 second of burst
            TokensPerPeriod     = rateLimit,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit          = int.MaxValue      // never drop
        })
        : null;
    var url = context.ParseResult.GetValueForArgument(urlArg);
    if (!url.StartsWith("http://") && !url.StartsWith("https://"))
    {
        url = "https://" + url;
    }
    
    var duration = context.ParseResult.GetValueForArgument(durationArg);
    var concurrency = context.ParseResult.GetValueForArgument(concurrencyArg);

    Console.WriteLine($"Starting load test against {url}");
    Console.WriteLine($"Duration: {duration}s, Concurrency: {concurrency}");

    var latenciesMs = new ConcurrentBag<long>();
    long ok = 0;
    long errors = 0;
    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));

    var handler = new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = concurrency
    };
    using var http = new HttpClient(handler);
    http.Timeout = TimeSpan.FromSeconds(30);
    http.DefaultRequestHeaders.UserAgent.Add(
        new ProductInfoHeaderValue("tiny-load-tester", "0.1"));

    try 
    {
        DateTimeOffset? pauseUntil = null;
        Console.WriteLine("Test in progress...");
        var task = Parallel.ForEachAsync(
            Enumerable.Range(0, concurrency),
            cts.Token,
            async (_, ct) =>
            {
                while (!ct.IsCancellationRequested)
                {
                    if (pauseUntil is { } next && next > DateTimeOffset.UtcNow)
                        await Task.Delay(next - DateTimeOffset.UtcNow, ct);
                    if (rateLimiter != null)
                    {
                        var lease = await rateLimiter.AcquireAsync(1, ct);
                        if (!lease.IsAcquired) break;            // cancelled
                    }
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        using var resp = await http.GetAsync(url, ct);
                        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                        {
                            var retry = resp.Headers.RetryAfter?.Delta
                                        ?? TimeSpan.FromSeconds(2);        // fallback
                            pauseUntil = DateTimeOffset.UtcNow + retry;
                            Console.WriteLine($"Too Many Requests response retrying in {retry.TotalSeconds}s");
                            continue;
                        }
                        resp.EnsureSuccessStatusCode();
                        Interlocked.Increment(ref ok);
                    }
                    catch (Exception ex)
                    {
                        if (ex.GetType() == typeof(TaskCanceledException))
                        {
                            Console.WriteLine($"{ex.Message}");
                        }
                        else
                        {
                            Interlocked.Increment(ref errors);
                            Console.WriteLine($"Error: {ex.Message}");
                        }
                    }
                    finally
                    {
                        sw.Stop();
                        latenciesMs.Add(sw.ElapsedMilliseconds);
                    }
                }
            });

        // Wait for either the task to complete or the duration to expire
        await task;
    }
    finally 
    {
        var total = ok + errors;
        var avg = !latenciesMs.IsEmpty ? latenciesMs.Average() : 0;
        var p95 = !latenciesMs.IsEmpty ? Percentile(latenciesMs, 0.95) : 0;

        Console.WriteLine($"""
                           Results for {url}
                           Elapsed (s)           : {duration}
                           Concurrency           : {concurrency}
                           Requests (total)      : {total}
                             ▸ OK                : {ok}
                             ▸ Errors            : {errors}  ({(total > 0 ? errors * 100.0 / total : 0):0.00}%)
                           Throughput (req/s)    : {total / (double)duration:0.00}
                           Latency (ms)          : avg {avg:0.0}   p95 {p95:0.0}
                           """);
    }
});

await root.InvokeAsync(args);
return;

// ---------- helpers --------------------------------------------------------
static double Percentile(ConcurrentBag<long> values, double p)
{
    if (values.IsEmpty) return double.NaN;
    var arr = values.ToArray();
    Array.Sort(arr);
    var idx = (int)Math.Ceiling(p * arr.Length) - 1;
    return arr[Math.Clamp(idx, 0, arr.Length - 1)];
}