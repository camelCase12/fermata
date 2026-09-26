namespace Fermata.Storage;

/// <summary>A saved document that records the version of its format.</summary>
/// <remarks>
/// A document older than <see cref="CurrentVersion"/> is brought up to date as it is loaded. A document newer
/// than it was written by a later Fermata, and is read but never saved over. Files from before formats had
/// versions read as version 0.
/// </remarks>
public interface IVersionedFile
{
    /// <summary>The version of the format this build reads and writes.</summary>
    static abstract int CurrentVersion { get; }

    int Version { get; set; }
}
