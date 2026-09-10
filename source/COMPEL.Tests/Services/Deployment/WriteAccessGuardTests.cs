namespace COMPEL.Tests.Services.Deployment;

/// <summary>
///     Verifies the start-up check that COMPEL can write to the directories it depends on writing to.
/// </summary>
public sealed class WriteAccessGuardTests
{
    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"COMPEL.WriteAccessGuardTests.{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        return directory;
    }

    [Test]
    public async Task An_Existing_Writable_Directory_Is_Reported_As_Writable()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await Assert.That(WriteAccessGuard.CanWriteTo(directory)).IsTrue();
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // The Linux Server Writes To A Fixed Location That May Not Exist Yet, So The Check Creates A Missing Directory Rather Than Reporting It As Unwritable
    [Test]
    public async Task A_Missing_Directory_That_Can_Be_Created_Is_Reported_As_Writable()
    {
        string parent = CreateTemporaryDirectory();
        string directory = Path.Combine(parent, "artefacts", "nested");

        try
        {
            using (Assert.Multiple())
            {
                await Assert.That(WriteAccessGuard.CanWriteTo(directory)).IsTrue();
                await Assert.That(Directory.Exists(directory)).IsTrue();
            }
        }

        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    // A Path Occupied By A File Cannot Become A Directory, Which Is A Portable Way To Exercise The Failure Branch Without Depending On Platform Permissions
    [Test]
    public async Task A_Path_Occupied_By_A_File_Is_Reported_As_Not_Writable()
    {
        string parent = CreateTemporaryDirectory();
        string occupied = Path.Combine(parent, "occupied");

        File.WriteAllText(occupied, "not a directory");

        try
        {
            await Assert.That(WriteAccessGuard.CanWriteTo(occupied)).IsFalse();
        }

        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Test]
    public async Task A_Writable_Installation_Directory_Yields_No_Unwritable_Directory()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await Assert.That(WriteAccessGuard.FindUnwritableDirectory(directory)).IsNull();
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task An_Unwritable_Installation_Directory_Is_The_Reported_Directory()
    {
        string parent = CreateTemporaryDirectory();
        string occupied = Path.Combine(parent, "occupied");

        File.WriteAllText(occupied, "not a directory");

        try
        {
            await Assert.That(WriteAccessGuard.FindUnwritableDirectory(occupied)).IsEqualTo(occupied);
        }

        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Test]
    public async Task The_Probe_Leaves_Nothing_Behind()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await Assert.That(WriteAccessGuard.CanWriteTo(directory)).IsTrue();

            await Assert.That(Directory.EnumerateFileSystemEntries(directory).Any()).IsFalse();
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
