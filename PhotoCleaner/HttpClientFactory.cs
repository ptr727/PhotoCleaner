using System.Net.Http.Headers;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace PhotoCleaner;

internal static class HttpClientFactory
{
    private static readonly Lazy<HttpClient> s_httpClient = new(CreateHttpClient);
    private static readonly Lazy<ResilienceHandler> s_resilienceHandler = new(
        CreateResilienceHandler
    );

    internal static HttpClient GetHttpClient() => s_httpClient.Value;

    internal static ResilienceHandler GetResilienceHandler() => s_resilienceHandler.Value;

    private static ResilienceHandler CreateResilienceHandler() =>
        new(
            new ResiliencePipelineBuilder<HttpResponseMessage>()
                .AddRetry(
                    new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>
                    {
                        MaxRetryAttempts = 3,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromSeconds(1),
                        MaxDelay = TimeSpan.FromSeconds(30),
                        ShouldHandle = args =>
                            ValueTask.FromResult(IsTransientFailure(args.Outcome)),
                    }
                )
                .AddCircuitBreaker(
                    new Polly.CircuitBreaker.CircuitBreakerStrategyOptions<HttpResponseMessage>
                    {
                        FailureRatio = 0.2,
                        MinimumThroughput = 10,
                        SamplingDuration = TimeSpan.FromSeconds(60),
                        BreakDuration = TimeSpan.FromSeconds(30),
                        ShouldHandle = args =>
                            ValueTask.FromResult(IsTransientFailure(args.Outcome)),
                    }
                )
                .AddTimeout(TimeSpan.FromSeconds(30))
                .Build()
        )
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            },
        };

    private static HttpClient CreateHttpClient()
    {
        HttpClient httpClient = new(GetResilienceHandler()) { Timeout = TimeSpan.FromSeconds(120) };
        string version = AssemblyInfo.InformationalVersion;
        int plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
        {
            version = version[..plusIndex];
        }

        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(AssemblyInfo.AppName, version)
        );
        return httpClient;
    }

    private static bool IsTransientFailure(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is not null)
        {
            return true;
        }

        if (outcome.Result is null)
        {
            return false;
        }

        int statusCode = (int)outcome.Result.StatusCode;
        return statusCode is 408 or 429 or >= 500;
    }
}
