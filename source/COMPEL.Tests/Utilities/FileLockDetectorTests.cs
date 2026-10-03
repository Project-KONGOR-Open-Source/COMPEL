namespace COMPEL.Tests.Utilities;

/// <summary>
///     Verifies the lock detector against the one process whose locks the test controls: itself.
/// </summary>
public sealed class FileLockDetectorTests
{
    [Test]
    public async Task The_Current_Process_Is_Reported_When_It_Holds_A_File_Open()
    {
        string path = Path.Combine(Path.GetTempPath(), $"compel-lock-{Guid.NewGuid():N}.bin");

        try
        {
            using (FileStream stream = new (path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                List<FileLockingProcess> lockingProcesses = FileLockDetector.GetLockingProcesses(path);

                await Assert.That(lockingProcesses.Select(lockingProcess => lockingProcess.ProcessID).Contains(Environment.ProcessId)).IsTrue();
            }
        }

        finally
        {
            File.Delete(path);
        }
    }
}
