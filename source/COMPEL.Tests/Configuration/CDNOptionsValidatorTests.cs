namespace COMPEL.Tests.Configuration;

/// <summary>
///     Verifies the start-up validation of content delivery network options: host presence and address validity.
/// </summary>
public sealed class CDNOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(CDNOptions options)
        => new CDNOptionsValidator().Validate(name: null, options);

    [Test]
    public async Task A_Valid_Host_Passes_Validation()
    {
        CDNOptions officialOptions = new () { Host = "https://cdn.kongor.net/" };
        CDNOptions localOptions    = new () { Host = "http://localhost:5555/cdn/" };

        using (Assert.Multiple())
        {
            await Assert.That(Validate(officialOptions).Succeeded).IsTrue();
            await Assert.That(Validate(localOptions).Succeeded).IsTrue();
        }
    }

    [Test]
    public async Task An_Empty_Or_Whitespace_Host_Is_Rejected()
    {
        CDNOptions emptyOptions      = new () { Host = string.Empty };
        CDNOptions whitespaceOptions = new () { Host = "   " };

        ValidateOptionsResult emptyResult      = Validate(emptyOptions);
        ValidateOptionsResult whitespaceResult = Validate(whitespaceOptions);

        using (Assert.Multiple())
        {
            await Assert.That(emptyResult.Failed).IsTrue();
            await Assert.That(emptyResult.FailureMessage?.Contains(@"""CDN"" Must Be Provided")).IsTrue();
            await Assert.That(whitespaceResult.Failed).IsTrue();
            await Assert.That(whitespaceResult.FailureMessage?.Contains(@"""CDN"" Must Be Provided")).IsTrue();
        }
    }

    [Test]
    public async Task An_Invalid_Address_Is_Rejected()
    {
        CDNOptions spaceOptions     = new () { Host = "cdn .kongor.net" };
        CDNOptions quoteOptions     = new () { Host = "\"cdn.kongor.net\"" };
        CDNOptions semicolonOptions = new () { Host = "cdn.kongor.net;injection" };

        ValidateOptionsResult spaceResult     = Validate(spaceOptions);
        ValidateOptionsResult quoteResult     = Validate(quoteOptions);
        ValidateOptionsResult semicolonResult = Validate(semicolonOptions);

        using (Assert.Multiple())
        {
            await Assert.That(spaceResult.Failed).IsTrue();
            await Assert.That(spaceResult.FailureMessage?.Contains("Is Not A Valid Host Name, IP Address, Or URL")).IsTrue();
            await Assert.That(quoteResult.Failed).IsTrue();
            await Assert.That(quoteResult.FailureMessage?.Contains("Is Not A Valid Host Name, IP Address, Or URL")).IsTrue();
            await Assert.That(semicolonResult.Failed).IsTrue();
            await Assert.That(semicolonResult.FailureMessage?.Contains("Is Not A Valid Host Name, IP Address, Or URL")).IsTrue();
        }
    }
}
