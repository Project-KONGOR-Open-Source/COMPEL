namespace COMPEL.Tests;

/// <summary>
///     Verifies the foreign-entry enumeration and the display capping used by the location guard.
/// </summary>
public sealed class LocationGuardTests
{
    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        Directory.CreateDirectory(directory);

        return directory;
    }

    [Test]
    public async Task An_Empty_Directory_Has_No_Foreign_Entries()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            await Assert.That(LocationGuard.EnumerateForeignEntries(directory).Count).IsEqualTo(0);
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_Fresh_Deployment_Has_No_Foreign_Entries()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            File.WriteAllText(Path.Combine(directory, DeploymentManifest.ApplicationExecutableFileName), string.Empty);
            File.WriteAllText(Path.Combine(directory, DeploymentManifest.ConfigurationFileName), "{}");
            File.WriteAllText(Path.Combine(directory, DeploymentManifest.LogFileName), string.Empty);
            File.WriteAllText(Path.Combine(directory, DeploymentManifest.LockFileName), string.Empty);

            await Assert.That(LocationGuard.EnumerateForeignEntries(directory).Count).IsEqualTo(0);
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_Foreign_File_Is_Reported()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            File.WriteAllText(Path.Combine(directory, DeploymentManifest.ApplicationExecutableFileName), string.Empty);
            File.WriteAllText(Path.Combine(directory, "holiday-photos.zip"), string.Empty);

            IReadOnlyList<string> foreignEntries = LocationGuard.EnumerateForeignEntries(directory);

            using (Assert.Multiple())
            {
                await Assert.That(foreignEntries.Count).IsEqualTo(1);
                await Assert.That(foreignEntries[0]).IsEqualTo("holiday-photos.zip");
            }
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_Subdirectory_Containing_Files_Is_Reported_With_A_Trailing_Glob_Suffix()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "documents"));

            File.WriteAllText(Path.Combine(directory, "documents", "important.txt"), string.Empty);

            IReadOnlyList<string> foreignEntries = LocationGuard.EnumerateForeignEntries(directory);

            using (Assert.Multiple())
            {
                await Assert.That(foreignEntries.Count).IsEqualTo(1);
                await Assert.That(foreignEntries[0]).IsEqualTo("documents/**");
            }
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task An_Empty_Subdirectory_Tree_Is_Not_Reported()
    {
        string directory = CreateTemporaryDirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "empty", "nested"));

            await Assert.That(LocationGuard.EnumerateForeignEntries(directory).Count).IsEqualTo(0);
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_List_Longer_Than_The_Display_Cap_Is_Truncated_With_A_Trailing_Summary()
    {
        List<string> foreignEntries = [.. Enumerable.Range(1, 30).Select(index => $"file-{index}.txt")];

        IReadOnlyList<string> capped = LocationGuard.ApplyForeignEntriesDisplayCap(foreignEntries);

        using (Assert.Multiple())
        {
            await Assert.That(capped.Count).IsEqualTo(LocationGuard.MaximumCountOfForeignEntriesToDisplay + 1);
            await Assert.That(capped[LocationGuard.MaximumCountOfForeignEntriesToDisplay]).IsEqualTo("... and 5 more");
        }
    }

    [Test]
    public async Task A_List_Within_The_Display_Cap_Is_Returned_Unchanged()
    {
        List<string> foreignEntries = [ "one.txt", "two.txt" ];

        IReadOnlyList<string> capped = LocationGuard.ApplyForeignEntriesDisplayCap(foreignEntries);

        await Assert.That(capped).IsEquivalentTo(foreignEntries);
    }
}
