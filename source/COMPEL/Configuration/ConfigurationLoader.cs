namespace COMPEL.Configuration;

/// <summary>
///     Loads, and on first run generates, the single self-describing "COMPEL.json" configuration file that sits alongside the executable.
/// </summary>
public static class ConfigurationLoader
{
    private const string FileName = DeploymentManifest.ConfigurationFileName;

    // A Dedicated Context Whose Options Indent The Output And Avoid Escaping Apostrophes And Slashes In The Descriptions, So The Generated File Reads Cleanly
    private static readonly ConfigurationJSONContext WriteContext = new (new JsonSerializerOptions
    {
        WriteIndented = true,
        IndentSize = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    public static string ResolvePath() => Path.Combine(AppContext.BaseDirectory, FileName);

    public static bool Exists() => File.Exists(ResolvePath());

    /// <summary>
    ///     Writes a "COMPEL.json" populated with default values and descriptions.
    /// </summary>
    public static void CreateDefault() => CreateDefault(ResolvePath());

    internal static void CreateDefault(string path)
    {
        string serialised = JsonSerializer.Serialize(new ConfigurationFile(), WriteContext.ConfigurationFile);

        File.WriteAllText(path, serialised);
    }

    /// <summary>
    ///     Reads and deserialises "COMPEL.json". Settings absent from the file fall back to their defaults.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown with a message naming the problem when "COMPEL.json" cannot be read, is not valid JSON, or does not match the expected shape (for example a string where a number is expected), rather than letting a raw <see cref="IOException"/> or <see cref="JsonException"/> propagate.
    /// </exception>
    public static ConfigurationFile Load() => Load(ResolvePath());

    internal static ConfigurationFile Load(string path)
    {
        string json;

        try
        {
            json = File.ReadAllText(path);
        }

        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($@"""COMPEL.json"" Could Not Be Read: {exception.Message}", exception);
        }

        ConfigurationFile file;

        try
        {
            file = JsonSerializer.Deserialize(json, ConfigurationJSONContext.Default.ConfigurationFile) ?? throw new InvalidOperationException(@"""COMPEL.json"" Deserialised To NULL");
        }

        catch (JsonException exception)
        {
            throw new InvalidOperationException($@"""COMPEL.json"" Is Not Valid: {exception.Message}", exception);
        }

        // A Setting Explicitly Set To "null" In The File Deserialises As A Null Object, So Each Is Coalesced Back To Its Default
        // This Keeps A Null Setting Behaving Like A Missing One (Falling Back To Its Default) Rather Than Faulting Later When Its Value Is Read
        file.UserName             ??= new ();
        file.Password             ??= new ();
        file.Instances            ??= new ();
        file.WarmInstancesTarget  ??= new ();
        file.Gateway              ??= new ();
        file.Location             ??= new ();
        file.ServerNamePrefix     ??= new ();
        file.UseProxy             ??= new ();
        file.PortRangeOffset      ??= new ();
        file.RuntimeArtefactsPath ??= new ();
        file.CDNSynchronisation   ??= new ();
        file.AuthenticationToken  ??= new ();
        file.ControlPlanePort     ??= new ();

        return file;
    }
}
