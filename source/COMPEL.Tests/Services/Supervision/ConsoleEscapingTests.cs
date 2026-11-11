namespace COMPEL.Tests.Services.Supervision;

/// <summary>
///     Verifies the escaping of values passed to the Heroes Of Newerth console, which must mirror the game's own escaping exactly.
/// </summary>
public sealed class ConsoleEscapingTests
{
    [Test]
    public async Task Escape_Puts_A_Backslash_Before_Every_ASCII_Character_That_Is_Not_A_Letter_Or_Digit()
    {
        await Assert.That(ConsoleEscaping.Escape(@"a b""c;d$e\f")).IsEqualTo(@"a\ b\""c\;d\$e\\f");
    }

    [Test]
    public async Task Escape_Leaves_Letters_Digits_And_Non_ASCII_Characters_Alone()
    {
        await Assert.That(ConsoleEscaping.Escape("Abc123Ünï名😀")).IsEqualTo("Abc123Ünï名😀");
    }

    [Test]
    public async Task The_Command_Limit_Is_The_Console_Maximum()
    {
        await Assert.That(ConsoleEscaping.CommandLimit).IsEqualTo(2048);
    }
}
