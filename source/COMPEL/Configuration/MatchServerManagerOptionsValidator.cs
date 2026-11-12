namespace COMPEL.Configuration;

/// <summary>
///     Validates <see cref="MatchServerManagerOptions"/> at startup, reproducing the pre-flight checks the legacy COMPEL performed before launching the match server manager.
///     Unlike the legacy implementation, which exited the process on the first failure, this collects every failure so the operator sees all configuration problems at once.
/// </summary>
public sealed partial class MatchServerManagerOptionsValidator : IValidateOptions<MatchServerManagerOptions>
{
    private static readonly string[] SupportedLocations = [ "USW", "USE", "EU", "AU", "BR", "RU", "SEA", "NEWERTH" ];

    private static readonly string[] SupportedAliases = [ "DEFAULT" ];

    // The Characters NEXUS Accepts In An Account Name; Its Lengths Differ Between Development And Production, So They Are Not Checked Here
    [GeneratedRegex(@"^[a-zA-Z0-9_\-]+\z")]
    private static partial Regex AccountNamePattern();

    public ValidateOptionsResult Validate(string? name, MatchServerManagerOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.UserName))
            failures.Add(@"""UserName"" Must Be Provided");

        else if (options.UserName.Equals("USERNAME", StringComparison.OrdinalIgnoreCase))
            failures.Add(@"""UserName"" Is Still The Default Placeholder; Set It To A Registered Project KONGOR User");

        else if (AccountNamePattern().IsMatch(options.UserName) is false)
            failures.Add(@"""UserName"" May Only Contain Letters, Numbers, Underscores, And Hyphens, Like A Project KONGOR Account Name");

        if (string.IsNullOrWhiteSpace(options.Password))
            failures.Add(@"""Password"" Must Be Provided");

        else if (options.Password.Equals("PASSWORD", StringComparison.OrdinalIgnoreCase))
            failures.Add(@"""Password"" Is Still The Default Placeholder; Set It To The User's Password");

        else if (options.Password.Contains((char)0))
            failures.Add(@"""Password"" Must Not Contain A Null Character");

        int processorCount = Environment.ProcessorCount;

        if (options.Instances < 1)
            failures.Add(@"""Instances"" Must Be At Least One");

        else if (options.Instances > processorCount)
            failures.Add($@"""Instances"" ({options.Instances}) Must Not Exceed The Number Of Logical Processors ({processorCount})");

        if (options.WarmInstancesTarget < 0)
            failures.Add(@"""WarmInstancesTarget"" Must Not Be Negative");

        else if (options.WarmInstancesTarget > options.Instances)
            failures.Add($@"""WarmInstancesTarget"" ({options.WarmInstancesTarget}) Must Not Exceed ""Instances"" ({options.Instances})");

        if (string.IsNullOrWhiteSpace(options.Gateway))
            failures.Add(@"""Gateway"" Must Be Provided");

        if (string.IsNullOrWhiteSpace(options.MasterServer))
            failures.Add(@"""MasterServer"" Must Be Provided");

        else if (IsHostWithOptionalPort(options.MasterServer) is false)
            failures.Add($@"""MasterServer"" ""{options.MasterServer}"" Is Not Valid; Use A Host Name Or An IP Address With An Optional Port From 1 To 65535, Without A Scheme Or A Path");

        if (string.IsNullOrWhiteSpace(options.Location))
            failures.Add(@"""Location"" Must Be Provided");

        else if (SupportedLocations.Contains(options.Location.ToUpperInvariant()) is false)
            failures.Add($@"""Location"" ""{options.Location}"" Is Not Valid; Valid Locations Are {string.Join(", ", SupportedLocations)}");

        if (string.IsNullOrWhiteSpace(options.ServerNamePrefix))
            failures.Add(@"""ServerNamePrefix"" Must Be Provided");

        else if (options.ServerNamePrefix.Contains((char)0))
            failures.Add(@"""ServerNamePrefix"" Must Not Contain A Null Character");

        if (options.PortRangeOffset < 0)
            failures.Add(@"""PortRangeOffset"" Must Not Be Negative");

        else
        {
            int minimumGamePort  = options.UseProxy ? PortPlan.BaseGamePort  + PortPlan.ProxyPublicOffset : PortPlan.BaseGamePort;
            int maximumGamePort  = minimumGamePort + PortPlan.PortRangeWindow;
            int minimumVoicePort = options.UseProxy ? PortPlan.BaseVoicePort + PortPlan.ProxyPublicOffset : PortPlan.BaseVoicePort;
            int maximumVoicePort = minimumVoicePort + PortPlan.PortRangeWindow;

            // "- 1" Matches "PortPlan.LocalGameEnd"/"LocalVoiceEnd", Whose Highest Port Is "Start + Instances - 1", Not "Start + Instances"
            if (minimumGamePort + options.PortRangeOffset + options.Instances - 1 > maximumGamePort || minimumVoicePort + options.PortRangeOffset + options.Instances - 1 > maximumVoicePort)
                failures.Add($@"A Port Range Offset Of {options.PortRangeOffset} Causes Ports For {options.Instances} Instance(s) To Bleed Outside Of The Allowed Port Range");
        }

        if (string.IsNullOrWhiteSpace(options.RuntimeArtefactsPath))
            failures.Add(@"""RuntimeArtefactsPath"" Must Be Provided");

        else if (SupportedAliases.Contains(options.RuntimeArtefactsPath.ToUpperInvariant()) is false && Path.IsPathFullyQualified(options.RuntimeArtefactsPath) is false)
            failures.Add($@"""RuntimeArtefactsPath"" ""{options.RuntimeArtefactsPath}"" Is Not Valid; Use The ""DEFAULT"" Alias Or A Fully Qualified Path");

        // The Console Refuses A Command Longer Than Its Limit Whole, Which Would Start The Match Server Manager With None Of COMPEL's Settings; The Widest Address Gives The Longest Payload
        // This Runs Last, Because Building The Arguments Needs Options That Passed Every Other Check
        if (failures.Count is 0)
        {
            string[] arguments = ManagerArguments.Build(options, new PortPlan(options), "255.255.255.255", options.MasterServer, new ArtefactsLocator(Options.Create(options)).ArtefactsDirectory);

            // The Console Reads The Payload Without Its Surrounding Quotes
            int commandLength = arguments[Array.IndexOf(arguments, "-execute") + 1].Length - 2;

            if (commandLength >= ConsoleEscaping.CommandLimit)
                failures.Add($@"The Settings Passed To The Match Server Manager Take {commandLength} Characters, Which Reaches The Console's Limit Of {ConsoleEscaping.CommandLimit}; Shorten ""UserName"", ""Password"", Or ""ServerNamePrefix""");
        }

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsHostWithOptionalPort(string address)
    {
        string host = address;

        int separator = address.LastIndexOf(':');

        if (separator >= 0)
        {
            host = address[..separator];

            if (int.TryParse(address[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) is false || port is < 1 or > 65535)
                return false;
        }

        return Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4;
    }
}
