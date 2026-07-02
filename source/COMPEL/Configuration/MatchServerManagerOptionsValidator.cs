namespace COMPEL.Configuration;

/// <summary>
///     Validates <see cref="MatchServerManagerOptions"/> at startup, reproducing the pre-flight checks the legacy COMPEL performed before launching the manager.
///     Unlike the legacy implementation, which exited the process on the first failure, this collects every failure so the operator sees all configuration problems at once.
/// </summary>
public sealed class MatchServerManagerOptionsValidator : IValidateOptions<MatchServerManagerOptions>
{
    private static readonly string[] SupportedLocations = [ "USW", "USE", "EU", "AU", "BR", "RU", "SEA", "NEWERTH" ];

    private static readonly string[] SupportedAliases = [ "DEFAULT" ];

    // These Characters Would Corrupt The Manager's "-execute" CVar String: A Double Quote Can Terminate Its Quoted Argument Early, And A Semicolon Is The CVar-Command Separator. A Control Character (For Example A Newline Or Tab) Is Re-Tokenised As Whitespace By The CVar Parser And Silently Truncates The Value, So It Is Rejected Too.
    private static readonly char[] UnsafeManagerArgumentCharacters = [ '"', ';' ];

    public ValidateOptionsResult Validate(string? name, MatchServerManagerOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.UserName))
            failures.Add(@"""UserName"" Must Be Provided");

        else if (options.UserName.Equals("USERNAME", StringComparison.OrdinalIgnoreCase))
            failures.Add(@"""UserName"" Is Still The Default Placeholder; Set It To A Registered Project KONGOR User");

        else if (ContainsUnsafeManagerArgumentCharacter(options.UserName))
            failures.Add(@"""UserName"" Must Not Contain A Double Quote, Semicolon, Or Control Character");

        if (string.IsNullOrWhiteSpace(options.Password))
            failures.Add(@"""Password"" Must Be Provided");

        else if (options.Password.Equals("PASSWORD", StringComparison.OrdinalIgnoreCase))
            failures.Add(@"""Password"" Is Still The Default Placeholder; Set It To The User's Password");

        else if (ContainsUnsafeManagerArgumentCharacter(options.Password))
            failures.Add(@"""Password"" Must Not Contain A Double Quote, Semicolon, Or Control Character");

        int processorCount = Environment.ProcessorCount;

        if (options.Instances < 1)
            failures.Add(@"""Instances"" Must Be At Least One");

        else if (options.Instances > processorCount)
            failures.Add($@"""Instances"" ({options.Instances}) Must Not Exceed The Number Of Logical Processors ({processorCount})");

        if (string.IsNullOrWhiteSpace(options.Gateway))
            failures.Add(@"""Gateway"" Must Be Provided");

        if (string.IsNullOrWhiteSpace(options.Location))
            failures.Add(@"""Location"" Must Be Provided");

        else if (SupportedLocations.Contains(options.Location.ToUpperInvariant()) is false)
            failures.Add($@"""Location"" ""{options.Location}"" Is Not Valid; Valid Locations Are {string.Join(", ", SupportedLocations)}");

        if (string.IsNullOrWhiteSpace(options.ServerNamePrefix))
            failures.Add(@"""ServerNamePrefix"" Must Be Provided");

        else if (ContainsUnsafeManagerArgumentCharacter(options.ServerNamePrefix))
            failures.Add(@"""ServerNamePrefix"" Must Not Contain A Double Quote, Semicolon, Or Control Character");

        if (options.PortRangeOffset < 0)
            failures.Add(@"""PortRangeOffset"" Must Not Be Negative");

        else
        {
            int minimumGamePort  = options.UseProxy ? PortPlan.BaseGamePort  + PortPlan.ProxyPublicOffset : PortPlan.BaseGamePort;
            int maximumGamePort  = minimumGamePort + PortPlan.PortRangeWindow;
            int minimumVoicePort = options.UseProxy ? PortPlan.BaseVoicePort + PortPlan.ProxyPublicOffset : PortPlan.BaseVoicePort;
            int maximumVoicePort = minimumVoicePort + PortPlan.PortRangeWindow;

            // "- 1" Matches "PortPlan.LocalGameEnd"/"LocalVoiceEnd", Whose Highest Port Is "Start + Instances - 1", Not "Start + Instances".
            if (minimumGamePort + options.PortRangeOffset + options.Instances - 1 > maximumGamePort || minimumVoicePort + options.PortRangeOffset + options.Instances - 1 > maximumVoicePort)
                failures.Add($@"A Port Range Offset Of {options.PortRangeOffset} Causes Ports For {options.Instances} Instance(s) To Bleed Outside Of The Allowed Port Range");
        }

        if (string.IsNullOrWhiteSpace(options.RuntimeArtefactsPath))
            failures.Add(@"""RuntimeArtefactsPath"" Must Be Provided");

        else if (SupportedAliases.Contains(options.RuntimeArtefactsPath.ToUpperInvariant()) is false && Path.IsPathFullyQualified(options.RuntimeArtefactsPath) is false)
            failures.Add($@"""RuntimeArtefactsPath"" ""{options.RuntimeArtefactsPath}"" Is Not Valid; Use The ""DEFAULT"" Alias Or A Fully Qualified Path");

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool ContainsUnsafeManagerArgumentCharacter(string value)
        => value.IndexOfAny(UnsafeManagerArgumentCharacters) >= 0 || value.Any(char.IsControl);
}
