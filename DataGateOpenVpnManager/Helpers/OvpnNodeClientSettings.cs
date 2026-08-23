namespace DataGateOpenVpnManager.Helpers;

/// <summary>
/// Resolves client-relevant crypto settings the same way <c>entrypoint.sh</c> does for server.conf.
/// </summary>
public static class OvpnNodeClientSettings
{
    public static OvpnClientTemplateSettings FromConfiguration(IConfiguration configuration)
    {
        var dco = IsTruthy(configuration["DCO"]);
        var cipher = NullIfWhiteSpace(configuration["CIPHER"]);
        var dataCiphers = NullIfWhiteSpace(configuration["DATA_CIPHERS"]);

        if (string.IsNullOrWhiteSpace(cipher))
            cipher = dco ? "AES-128-GCM" : "AES-256-CBC";

        if (dco && string.IsNullOrWhiteSpace(dataCiphers))
            dataCiphers = "AES-128-GCM:AES-256-GCM:CHACHA20-POLY1305";

        return new OvpnClientTemplateSettings
        {
            Proto = NullIfWhiteSpace(configuration["PROTO"]),
            Cipher = cipher,
            DataCiphers = dataCiphers,
            Auth = NullIfWhiteSpace(configuration["AUTH"]) ?? "SHA256",
            TlsVersionMin = NullIfWhiteSpace(configuration["TLS_VERSION_MIN"]) ?? "1.2",
            ClientVerb = NullIfWhiteSpace(configuration["CLIENT_VERB"]) ?? "3"
        };
    }

    public static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase)
         || value == "1"
         || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
