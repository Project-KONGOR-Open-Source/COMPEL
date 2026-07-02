namespace COMPEL;

/// <summary>
///     Ensures that only one COMPEL process runs against a given installation at a time, using an exclusively-held lock file that the operating system releases automatically when the process exits, even on a crash.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly FileStream? lockStream;

    private SingleInstanceGuard(FileStream? lockStream) => this.lockStream = lockStream;

    /// <summary>
    ///     Attempts to exclusively acquire the lock file at <paramref name="lockFilePath"/>. Returns <see langword="false"/> if another process already holds it.
    /// </summary>
    public static bool TryAcquire(string lockFilePath, out SingleInstanceGuard guard)
    {
        try
        {
            FileStream stream = new (lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            guard = new SingleInstanceGuard(stream);

            return true;
        }

        catch (IOException)
        {
            guard = new SingleInstanceGuard(null);

            return false;
        }
    }

    public void Dispose() => lockStream?.Dispose();
}
