namespace COMPEL.Endpoints;

/// <summary>
///     Validates the shared bearer token presented against the control plane's management endpoints.
///     The token is compared with a fixed-time comparison so a mismatch does not leak the configured token's contents through timing.
/// </summary>
internal static class ControlPlaneAuthentication
{
    /// <summary>
    ///     Returns <see langword="null"/> when the request is authorised, or the failing <see cref="IResult"/> otherwise.
    /// </summary>
    public static IResult? Validate(HttpContext httpContext)
    {
        ControlPlaneOptions options = httpContext.RequestServices.GetRequiredService<IOptions<ControlPlaneOptions>>().Value;

        if (string.IsNullOrEmpty(options.AuthenticationToken))
            return Results.Text("The Control Plane Authentication Token Is Not Configured", statusCode: StatusCodes.Status503ServiceUnavailable);

        if (TryGetBearerToken(httpContext.Request, out string? presentedToken) is false)
            return Results.Unauthorized();

        byte[] expected = Encoding.UTF8.GetBytes(options.AuthenticationToken);
        byte[] presented = Encoding.UTF8.GetBytes(presentedToken);

        return CryptographicOperations.FixedTimeEquals(expected, presented) ? null : Results.Unauthorized();
    }

    private static bool TryGetBearerToken(HttpRequest request, out string presentedToken)
    {
        presentedToken = string.Empty;

        string? header = request.Headers.Authorization;

        if (string.IsNullOrWhiteSpace(header))
            return false;

        const string scheme = "Bearer ";

        if (header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) is false)
            return false;

        presentedToken = header[scheme.Length..].Trim();

        return string.IsNullOrEmpty(presentedToken) is false;
    }
}
