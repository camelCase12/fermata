using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Fermata.Tests;

/// <summary>A minimal assertion collector: every failure is reported, and the run fails if any occurred.</summary>
internal sealed class Checks
{
    private readonly List<string> failures = [];
    private string suite = "";

    public int Passed { get; private set; }
    public IReadOnlyList<string> Failures => failures;

    public void Run(string name, Action<Checks> body)
    {
        suite = name;
        int failedBefore = failures.Count, passedBefore = Passed;
        var clock = Stopwatch.StartNew();
        try
        {
            body(this);
        }
        catch (Exception error)
        {
            failures.Add($"[{suite}] threw {error}");
        }
        string status = failures.Count == failedBefore ? "ok" : $"{failures.Count - failedBefore} FAILED";
        Console.WriteLine($"{name,-24} {Passed - passedBefore,5} checks  {clock.ElapsedMilliseconds,6} ms  {status}");
    }

    public void That(bool condition, string description)
    {
        if (condition)
            Passed++;
        else
            failures.Add($"[{suite}] {description}");
    }

    public void Equal<T>(T expected, T actual, string description)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
            Passed++;
        else
            failures.Add($"[{suite}] {description}: expected <{expected}>, got <{actual}>");
    }

    public void Near(double expected, double actual, double tolerance, string description)
    {
        if (Math.Abs(expected - actual) <= tolerance)
            Passed++;
        else
            failures.Add($"[{suite}] {description}: expected {expected:0.###} ± {tolerance}, got {actual:0.###}");
    }

    public void Throws<TException>(Action action, string description) where TException : Exception
    {
        try
        {
            action();
            failures.Add($"[{suite}] {description}: no exception");
        }
        catch (TException)
        {
            Passed++;
        }
        catch (Exception other)
        {
            failures.Add($"[{suite}] {description}: threw {other.GetType().Name} instead of {typeof(TException).Name}");
        }
    }
}
