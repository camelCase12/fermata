using Fermata.Tests;

if (args is ["--compare-ffprobe", var directory])
    return FfprobeComparison.Run(directory);
if (args is ["--make-large-library", var largeDirectory, var count])
    return LargeLibrary.Run(largeDirectory, int.Parse(count));
if (args is ["--measure-scan", var scanDirectory])
    return LargeLibrary.MeasureScan(scanDirectory);

var checks = new Checks();
checks.Run("text folding", TextChecks.Run);
checks.Run("imaging", ImagingChecks.Run);
checks.Run("storage", StorageChecks.Run);
checks.Run("metadata", MetadataChecks.Run);
checks.Run("library", LibraryChecks.Run);
checks.Run("play queue", QueueChecks.Run);
checks.Run("player", PlayerChecks.Run);
checks.Run("libmpv", MpvChecks.Run);
checks.Run("mpris", MprisChecks.Run);

Console.WriteLine();
if (checks.Failures.Count == 0)
{
    Console.WriteLine($"All {checks.Passed} checks passed.");
    return 0;
}
foreach (string failure in checks.Failures)
    Console.Error.WriteLine(failure);
Console.Error.WriteLine($"{checks.Failures.Count} failed, {checks.Passed} passed.");
return 1;
