namespace COMPEL.Utilities;

/// <summary>
///     An <see cref="IProgress{T}"/> that invokes its handler on the reporting thread.
///     <see cref="Progress{T}"/> posts its callbacks to the thread pool when there is no synchronisation context, which lets a stale report interleave with the lines written after the operation completes; reporting synchronously keeps the console and the log in order.
/// </summary>
internal sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
