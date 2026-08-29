using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.ProductionHistoryRetry.Validation;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            var runtimeType = Assembly.Load("TermLink.Agent")
                .GetType("TermLink.Agent.ProductionAgentRuntime", throwOnError: true);
            var waitMethod = runtimeType.GetMethod(
                "WaitForHistoryRetryOrSignalAsync",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("History retry wait helper is unavailable.");
            Require(waitMethod.ReturnType == typeof(Task<bool>),
                "History retry wait helper does not report signal versus timeout.");

            await ValidateSignalInterruptsLongRetryAsync(waitMethod);
            await ValidateExistingSignalIsConsumedAsync(waitMethod);
            await ValidateKnownWorkBypassesConsumedSignalAsync(waitMethod);
            await ValidateTimeoutDoesNotCreateSignalAsync(waitMethod);
            await ValidateCancellationInterruptsLongRetryAsync(waitMethod);

            Console.WriteLine("Production history retry validation passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task ValidateSignalInterruptsLongRetryAsync(MethodInfo waitMethod)
    {
        using var signal = new SemaphoreSlim(0, 1);
        var stopwatch = Stopwatch.StartNew();
        var wait = InvokeWait(
            waitMethod,
            signal,
            TimeSpan.FromSeconds(30),
            retryImmediately: false,
            CancellationToken.None);
        Require(!wait.IsCompleted, "Long history retry wait completed before a signal arrived.");

        signal.Release();
        var signaled = await wait.WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Require(signaled && stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            "A new history event did not interrupt the 30-second retry wait promptly.");
        Require(signal.CurrentCount == 0,
            "Interrupting history signal was not consumed exactly once.");
    }

    private static async Task ValidateExistingSignalIsConsumedAsync(MethodInfo waitMethod)
    {
        using var signal = new SemaphoreSlim(0, 1);
        signal.Release();

        var signaled = await InvokeWait(
            waitMethod,
            signal,
            TimeSpan.FromSeconds(30),
            retryImmediately: false,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Require(signaled && signal.CurrentCount == 0,
            "A history signal queued before the retry wait was lost or left unconsumed.");
    }

    private static async Task ValidateKnownWorkBypassesConsumedSignalAsync(MethodInfo waitMethod)
    {
        using var signal = new SemaphoreSlim(0, 1);
        var stopwatch = Stopwatch.StartNew();
        var ready = await InvokeWait(
            waitMethod,
            signal,
            TimeSpan.FromSeconds(30),
            retryImmediately: true,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Require(ready && stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            "Known superseding history work fell into the 30-second transport backoff after its signal was consumed.");
        Require(signal.CurrentCount == 0,
            "Immediate history retry manufactured a semaphore signal.");
    }

    private static async Task ValidateTimeoutDoesNotCreateSignalAsync(MethodInfo waitMethod)
    {
        using var signal = new SemaphoreSlim(0, 1);
        var stopwatch = Stopwatch.StartNew();
        var signaled = await InvokeWait(
            waitMethod,
            signal,
            TimeSpan.FromMilliseconds(75),
            retryImmediately: false,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Require(!signaled && stopwatch.Elapsed >= TimeSpan.FromMilliseconds(25),
            "History retry timeout did not produce one bounded retry interval.");
        Require(signal.CurrentCount == 0,
            "History retry timeout manufactured a semaphore signal.");

        var secondWait = InvokeWait(
            waitMethod,
            signal,
            TimeSpan.FromSeconds(30),
            retryImmediately: false,
            CancellationToken.None);
        var completed = await Task.WhenAny(secondWait, Task.Delay(TimeSpan.FromMilliseconds(150)));
        Require(completed != secondWait,
            "History retry timeout left a token that could create a hot loop.");

        signal.Release();
        Require(await secondWait.WaitAsync(TimeSpan.FromSeconds(2)),
            "A signal could not wake the waiter after an earlier retry timeout.");
        Require(signal.CurrentCount == 0,
            "Post-timeout history signal was not consumed exactly once.");
    }

    private static async Task ValidateCancellationInterruptsLongRetryAsync(MethodInfo waitMethod)
    {
        using var signal = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        var wait = InvokeWait(
            waitMethod,
            signal,
            TimeSpan.FromSeconds(30),
            retryImmediately: false,
            cancellation.Token);
        Require(!wait.IsCompleted, "Cancelable history retry wait completed before cancellation.");

        cancellation.Cancel();
        try
        {
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("History retry wait ignored cancellation.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        Require(signal.CurrentCount == 0,
            "Canceled history retry wait changed semaphore state.");
    }

    private static Task<bool> InvokeWait(
        MethodInfo waitMethod,
        SemaphoreSlim signal,
        TimeSpan delay,
        bool retryImmediately,
        CancellationToken token)
    {
        try
        {
            return (Task<bool>)waitMethod.Invoke(
                null,
                new object[] { signal, delay, retryImmediately, token });
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            throw error.InnerException;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
