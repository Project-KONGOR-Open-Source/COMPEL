namespace COMPEL;

internal static class CONTEXT
{
    internal static string Verifier { get; set; } = string.Empty;

    internal static CONFIGURATION JSONConfiguration = HANDLERS.HandleJSON();

    # pragma warning disable CA1416 // Validate Platform Compatibility
    internal static bool ProcessIsElevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    # pragma warning restore CA1416 // Validate Platform Compatibility

    internal static string RuntimeArtefactsPath = HANDLERS.HandleRuntimeArtefactsPath();

    internal static string ServerAddress = HANDLERS.GetServerAddress().GetAwaiter().GetResult();
    internal static string MasterServerAddress = HANDLERS.GetMasterServerAddress();
    internal static string MasterServerAddressAndPort = MasterServerAddress + (CONTEXT.JSONConfiguration.HostingEnvironment!.Value!.ToUpper().Equals("LOCAL") ? ":55555" : string.Empty /* Port 80 Is Implied */ );
}
