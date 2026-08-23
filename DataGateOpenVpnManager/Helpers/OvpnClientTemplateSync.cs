using System.Text.RegularExpressions;

namespace DataGateOpenVpnManager.Helpers;

/// <summary>
/// Live node settings used to align client .ovpn directives with the OpenVPN server.
/// </summary>
public sealed class OvpnClientTemplateSettings
{
    public string? Proto { get; init; }
    public string? Cipher { get; init; }
    public string? DataCiphers { get; init; }
    public string? Auth { get; init; }
    public string? TlsVersionMin { get; init; }
    public string? ClientVerb { get; init; }
}

/// <summary>
/// Aligns client .ovpn directives with live node settings (proto/cipher/auth/tls/verb).
/// Does not touch PEM blocks, <c>remote</c>, or <c>setenv FRIENDLY_NAME</c>.
/// </summary>
public static class OvpnClientTemplateSync
{
    public static string Apply(string template, OvpnClientTemplateSettings settings)
    {
        if (string.IsNullOrWhiteSpace(template) || settings is null)
            return template;

        var result = template;

        if (!string.IsNullOrWhiteSpace(settings.Proto))
        {
            var proto = settings.Proto.Trim().ToLowerInvariant();
            if (proto is "tcp" or "udp")
                result = ReplaceOrInsertDirective(result, "proto", proto);
        }

        if (!string.IsNullOrWhiteSpace(settings.Cipher))
            result = ReplaceOrInsertDirective(result, "cipher", settings.Cipher.Trim());

        if (!string.IsNullOrWhiteSpace(settings.DataCiphers))
            result = ReplaceOrInsertDirective(result, "data-ciphers", settings.DataCiphers.Trim());
        else if (!string.IsNullOrWhiteSpace(settings.Cipher))
            result = RemoveDirective(result, "data-ciphers");

        if (!string.IsNullOrWhiteSpace(settings.Auth))
            result = ReplaceOrInsertDirective(result, "auth", settings.Auth.Trim());

        if (!string.IsNullOrWhiteSpace(settings.TlsVersionMin))
            result = ReplaceOrInsertDirective(result, "tls-version-min", settings.TlsVersionMin.Trim());

        if (!string.IsNullOrWhiteSpace(settings.ClientVerb))
            result = ReplaceOrInsertDirective(result, "verb", settings.ClientVerb.Trim());

        return result;
    }

    private static string RemoveDirective(string template, string name)
    {
        var pattern = $@"^\s*{Regex.Escape(name)}\s+\S+.*\r?\n?";
        return Regex.Replace(template, pattern, string.Empty, RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    private static string ReplaceOrInsertDirective(string template, string name, string value)
    {
        // Match the named directive only when the next char is whitespace (not "auth-nocache").
        var pattern = $@"^\s*{Regex.Escape(name)}\s+\S+.*$";
        if (Regex.IsMatch(template, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
            return Regex.Replace(
                template,
                pattern,
                $"{name} {value}",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);

        // Prefer inserting data-ciphers after cipher.
        if (string.Equals(name, "data-ciphers", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(template, @"^\s*cipher\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            return Regex.Replace(
                template,
                @"(^\s*cipher\s+\S+.*$)",
                $"$1\n{name} {value}",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        // Prefer inserting tls-version-min after remote-cert-tls when present.
        if (string.Equals(name, "tls-version-min", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(template, @"^\s*remote-cert-tls\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            return Regex.Replace(
                template,
                @"(^\s*remote-cert-tls\s+\S+.*$)",
                $"$1\n{name} {value}",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        return $"{name} {value}\n{template}";
    }
}

/// <summary>Backward-compatible cipher-only wrapper. </summary>
public static class OvpnClientTemplateCipher
{
    public static string Apply(string template, string? cipher, string? dataCiphers) =>
        OvpnClientTemplateSync.Apply(template, new OvpnClientTemplateSettings
        {
            Cipher = cipher,
            DataCiphers = dataCiphers
        });
}
