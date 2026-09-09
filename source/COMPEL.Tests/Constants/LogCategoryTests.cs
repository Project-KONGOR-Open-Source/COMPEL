namespace COMPEL.Tests.Constants;

/// <summary>
///     Verifies the category constants share one width and that logger names resolve to the categories the log displays.
/// </summary>
public sealed class LogCategoryTests
{
    [Test]
    public async Task Every_Category_Is_Eleven_Characters_Wide()
    {
        IEnumerable<string> categories = typeof(LogCategory)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string?) field.GetRawConstantValue() ?? string.Empty);

        using (Assert.Multiple())
        {
            foreach (string category in categories)
                await Assert.That(category.Length).IsEqualTo(11);
        }
    }

    [Test]
    public async Task The_Synchronisation_Service_Resolves_To_The_Synchronise_Category()
    {
        await Assert.That(LogCategory.Resolve(typeof(DistributionSynchronisationService).FullName ?? string.Empty)).IsEqualTo(LogCategory.Synchronise);
    }

    [Test]
    public async Task The_Hosting_Lifetime_Resolves_To_The_Initialise_Category()
    {
        await Assert.That(LogCategory.Resolve("Microsoft.Hosting.Lifetime")).IsEqualTo(LogCategory.Initialise);
    }

    [Test]
    public async Task An_Unrecognised_Logger_Name_Resolves_To_The_Host_Category()
    {
        await Assert.That(LogCategory.Resolve("Some.Unrelated.Type")).IsEqualTo(LogCategory.Host);
    }
}
