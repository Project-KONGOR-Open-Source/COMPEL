namespace COMPEL.Configuration;

/// <summary>
///     Validates <see cref="CDNOptions"/> at startup, ensuring the configured content delivery network host is provided and syntactically valid.
/// </summary>
public sealed class CDNOptionsValidator : IValidateOptions<CDNOptions>
{
    public ValidateOptionsResult Validate(string? name, CDNOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.Host))
            failures.Add(@"""CDN"" Must Be Provided");

        else if (AddressValidation.IsValidAddress(options.Host) is false)
            failures.Add($@"""CDN"" ""{options.Host}"" Is Not A Valid Host Name, IP Address, Or URL");

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
