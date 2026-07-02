namespace COMPEL.Serialisation;

/// <summary>
///     Source-generated serialisation metadata for the control plane's request and response types.
///     Required because the application is published with Native AOT, which strips the reflection-based <see cref="JsonSerializer"/> paths that minimal APIs would otherwise use.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PingResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(PortAllocationResponse))]
[JsonSerializable(typeof(ActionResponse))]
internal sealed partial class ControlPlaneJSONContext : JsonSerializerContext;
