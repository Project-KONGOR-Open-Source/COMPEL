namespace COMPEL.Serialisation;

/// <summary>
///     Source-generated serialisation metadata for the on-disk "COMPEL.json" configuration file.
///     Required because the application is published with Native AOT, which strips the reflection-based <see cref="JsonSerializer"/> paths.
///     Default (PascalCase) property naming is used so the file's keys read as "UserName", "Value", "Description", and so on.
/// </summary>
[JsonSerializable(typeof(CompelConfigurationFile))]
internal sealed partial class CompelConfigurationJSONContext : JsonSerializerContext;
