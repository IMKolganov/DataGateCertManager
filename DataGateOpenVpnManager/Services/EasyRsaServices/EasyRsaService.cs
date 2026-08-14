using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services.EasyRsaServices.Interfaces;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DataGateOpenVpnManager.Services.Interfaces;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Cert.Responses;
using Microsoft.Extensions.Options;

namespace DataGateOpenVpnManager.Services.EasyRsaServices;

public class EasyRsaService(
    ILogger<IEasyRsaService> logger,
    IEasyRsaParseDbService easyRsaParseDbService,
    IBashCommandRunner easyRsaExecCommandService,
    IOpenVpnServerService openVpnServerService,
    IOptions<EasyRsaOptions> options,
    IEasyRsaPkiMutex pkiMutex)
    : IEasyRsaService
{
    private readonly ILogger<IEasyRsaService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    #region easyrsa build-client-full

// # ==============================================================================
// # EasyRSA build-client-full command variations
// # ==============================================================================
//
// | Command Example                                                                          | Description                                             |
// |------------------------------------------------------------------------------------------|---------------------------------------------------------|
// | `./easyrsa build-client-full client1`                                                    | Creates a client certificate with a password prompt     |
// | `./easyrsa build-client-full client1 nopass`                                             | Creates a client certificate without a password         |
// | `EASYRSA_BATCH=1 ./easyrsa build-client-full client1 nopass`                             | Skips confirmation prompts during execution             |
// | `EASYRSA_CERT_EXPIRE=3650 ./easyrsa build-client-full client1 nopass`                    | Sets the certificate expiration to 10 years (3650 days) |
// | `EASYRSA_KEY_SIZE=4096 ./easyrsa build-client-full client1 nopass`                       | Generates a 4096-bit key instead of the default 2048-bit|
// | `EASYRSA_DIGEST="sha512" ./easyrsa build-client-full client1 nopass`                     | Uses SHA-512 hashing instead of the default SHA-256     |
// | `EASYRSA_ALGO="ec" EASYRSA_CURVE="secp384r1" ./easyrsa build-client-full client1 nopass` | Uses ECDSA instead of RSA with secp384r1 curve          |
// | `EASYRSA_PASSIN="mypassword" ./easyrsa build-client-full client1`                        | Automatically provides the password instead of prompting|
// | `EASYRSA_PASSIN=file:/path/to/password.txt ./easyrsa build-client-full client1`          | Reads the password from a file                          |
// | `EASYRSA_SUBDIR=clients ./easyrsa build-client-full client1 nopass`                      | Stores the certificate and key in a custom subdirectory |
// | `EASYRSA_REQ_CN="My Custom CN" ./easyrsa build-client-full client1 nopass`               | Sets a custom Common Name (CN) in the certificate       |
// | `EASYRSA_REQ_SAN="DNS:client1.example.com" ./easyrsa build-client-full client1 nopass`   | Adds a Subject Alternative Name (SAN) to the certificate|
// | `EASYRSA_REQ_EMAIL="client1@example.com" ./easyrsa build-client-full client1 nopass`     | Specifies an email address in the certificate request   |
// | `EASYRSA_REQ_OU="MyOrgUnit" ./easyrsa build-client-full client1 nopass`                  | Adds an Organizational Unit to the certificate          |
//
// # ==============================================================================

    #endregion

    public async Task<ServerCertificate> BuildCertificateAsync(
        string easyRsaPath,
        CancellationToken cancellationToken,
        string commonName = "client1",
        int certExpireDays = 365)
    {
        easyRsaPath = Path.GetFullPath(easyRsaPath);
        await using var _ = await pkiMutex.AcquireAsync(easyRsaPath, cancellationToken);

        var pkiPath = Path.Combine(easyRsaPath, "pki");
        var reqPath = Path.Combine(pkiPath, "reqs", $"{commonName}.req");

        _logger.LogInformation("Starting certificate build for: {CommonName}", commonName);

        string command;

        if (File.Exists(reqPath))
        {
            _logger.LogWarning(
                "Request file already exists: {ReqPath}. Using existing request to sign new certificate.", reqPath);
            command = $"./easyrsa sign client {commonName}";
        }
        else
        {
            command = $"./easyrsa build-client-full {commonName} nopass";
        }

        var environmentVariables = new Dictionary<string, string>
        {
            ["EASYRSA_BATCH"] = "1",
            ["EASYRSA_CERT_EXPIRE"] = certExpireDays.ToString(),
            ["EASYRSA_PKI"] = pkiPath
        };

        _logger.LogInformation("Executing EasyRSA command: {Command}", command);

        var (output, exitCode) = await easyRsaExecCommandService.RunCommandAsync(
            command,
            environmentVariables,
            cancellationToken,
            easyRsaPath);

        if (exitCode != 0)
        {
            _logger.LogError("EasyRSA output:\n{Output}", output);
            throw new Exception($"Error while building certificate: Output: {output}");
        }

        _logger.LogInformation("Certificate generated successfully:\n{Output}", output);

        if (await UpdateCrlAsync(easyRsaPath, cancellationToken))
        {
            _logger.LogInformation("CRL updated successfully.");
        }

        var certPath = GetIssuedCertPath(easyRsaPath, commonName); 
        var serialFromOpenSsl = await CheckCertInOpensslAsync(easyRsaPath, certPath, cancellationToken);
        var serverCertificate = await MatchingCertsAsync(easyRsaPath, serialFromOpenSsl, commonName, cancellationToken);

        if (!serverCertificate.SerialNumber.Contains(serialFromOpenSsl))
        {
            throw new Exception(
                $"Certificate serial number mismatch. Expected in OpenSSL: {serialFromOpenSsl}, " +
                $"Found in Index: {serverCertificate.SerialNumber}");
        }

        var pemSerialPath = Path.Combine(pkiPath, "certs_by_serial", $"{serverCertificate.SerialNumber}.pem");
        _logger.LogInformation("Certificate PEM path: {PemSerialPath}", pemSerialPath);

        return serverCertificate;
    }

    public async Task<ServerCertificate> RevokeCertificateAsync(
        string easyRsaPath,
        string commonName,
        CancellationToken cancellationToken)
    {
        var serverCertificate = new ServerCertificate();
        var serialNumber = string.Empty;

        easyRsaPath = Path.GetFullPath(easyRsaPath);
        await using var _ = await pkiMutex.AcquireAsync(easyRsaPath, cancellationToken);

        var pkiPath = Path.Combine(easyRsaPath, "pki");
        var issuedPath = Path.Combine(pkiPath, "issued", $"{commonName}.crt");

        if (!File.Exists(issuedPath))
        {
            _logger.LogWarning("Certificate file not found: {Path}", issuedPath);
            throw new FileNotFoundException($"Certificate file not found: {issuedPath}");
        }

        _logger.LogInformation("Revoking certificate for: {CommonName}", commonName);

        var command = $"./easyrsa revoke {commonName}";
        var environmentVariables = new Dictionary<string, string>
        {
            ["EASYRSA_BATCH"] = "1",
            ["EASYRSA_PKI"] = pkiPath
        };

        var (output, exitCode) = await easyRsaExecCommandService.RunCommandAsync(
            command,
            environmentVariables,
            cancellationToken,
            easyRsaPath);

        if (exitCode == 0)
        {
            serialNumber = ExtractSerialFromRevocationOutput(output) ?? string.Empty;
            _logger.LogInformation("Certificate revoked successfully: " +
                                   "{CommonName}, SerialNumber: {SerialNumber}, " +
                                   "Output: {Output}", commonName, serialNumber, output);
        }
        else
        {
            if (output.Contains("ERROR:Already revoked", StringComparison.OrdinalIgnoreCase))
            {
                serverCertificate.Message = $"Certificate is already revoked: {commonName}";
                _logger.LogWarning("Certificate is already revoked: {CommonName}", commonName);
            }
            else if (output.Contains("ERROR: Certificate not found", StringComparison.OrdinalIgnoreCase))
            {
                serverCertificate.Message = $"Certificate not found: {commonName}";
                _logger.LogWarning("Certificate not found: {CommonName}", commonName);
            }
            else
            {
                throw new Exception($"Unknown error during revocation. ExitCode: {exitCode}, Output: {output}");
            }
        }

        _logger.LogInformation("Generating updated CRL...");
        if (await UpdateCrlAsync(easyRsaPath, cancellationToken))
        {
            _logger.LogInformation("CRL updated successfully.");
        }

        serverCertificate = await MatchingCertsAsync(easyRsaPath, serialNumber, commonName, cancellationToken);
        return serverCertificate;
    }

    public async Task<List<ServerCertificate>> GetAllCertificateInfoInIndexFileAsync(string easyRsaPath,
        CancellationToken cancellationToken)
    {
        var fullEasyRsaPath = Path.GetFullPath(easyRsaPath);
        var fullEasyRsaPkiPath = Path.Combine(fullEasyRsaPath, "pki");

        await using var _ = await pkiMutex.AcquireAsync(fullEasyRsaPath, cancellationToken);
        
        var taKeyName = options.Value.TaKeyFileName;
        if (!Directory.Exists(fullEasyRsaPkiPath) || !File.Exists(Path.Combine(easyRsaPath, "pki", taKeyName)))
        {
            await InstallEasyRsaAsync(fullEasyRsaPath, taKeyName, cancellationToken);
        }
        
        return await easyRsaParseDbService.ParseCertificateInfoInIndexFileAsync(fullEasyRsaPkiPath, cancellationToken);
    }

    private async Task InstallEasyRsaAsync(string easyRsaPath, string taKeyName, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing EasyRSA...");

        easyRsaPath = Path.GetFullPath(easyRsaPath);
        var pkiPath = Path.Combine(easyRsaPath, "pki");
        var scriptPath = Path.Combine(easyRsaPath, "easyrsa");

        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"EasyRSA script not found at: {scriptPath}");

        try
        {
            var chmodCommand = $"chmod +x ./easyrsa";
            
            var (output, exitCode) = await easyRsaExecCommandService.RunCommandAsync(
                chmodCommand,
                new Dictionary<string, string>(),
                cancellationToken,
                easyRsaPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"chmod failed: {ex.Message}");
        }

        var caPath = Path.Combine(pkiPath, "ca.crt");
        var taPath = Path.Combine(pkiPath, "ta.crt");

        var env = new Dictionary<string, string>
        {
            ["EASYRSA_BATCH"] = "1",
            ["EASYRSA_PKI"] = pkiPath
        };

        if (!Directory.Exists(pkiPath))
        {
            var initCommand = $"./easyrsa init-pki";
            _logger.LogInformation("Running EasyRSA init-pki...");

            var (initOut, initExit) = await easyRsaExecCommandService.RunCommandAsync(
                initCommand, env, cancellationToken);

            if (initExit != 0)
            {
                _logger.LogError("init-pki failed. Output: {Output}", initOut);
                throw new Exception($"Failed to initialize PKI. Error: {initOut}");
            }

            _logger.LogInformation("PKI initialized successfully");
        }

        if (!File.Exists(caPath))
        {
            var buildCaCommand = $"./easyrsa build-ca nopass";
            _logger.LogInformation("No CA certificate found. Running build-ca...");

            var (caOut, caExit) = await easyRsaExecCommandService.RunCommandAsync(
                buildCaCommand, env, cancellationToken);

            if (caExit != 0)
            {
                _logger.LogError("build-ca failed. Output: {Output}", caOut);
                throw new Exception($"Failed to build CA certificate. Error: {caOut}");
            }

            _logger.LogInformation("CA certificate created successfully");
        }
        else
        {
            _logger.LogInformation("CA certificate already exists. Skipping build-ca.");
        }

        if (!File.Exists(taPath))
        {
            await openVpnServerService.BuildTlsAuthKeyAsync(easyRsaPath, taKeyName, cancellationToken);
        }
    }

    private async Task<string> CheckCertInOpensslAsync(string easyRsaPath, string? certPath, 
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(certPath))
            throw new ArgumentException("Certificate path is null or empty");

        var opensslPath = ConvertToBashPath(certPath);
        var certPathCommand = $"openssl x509 -in \"{opensslPath}\" -serial -noout";

        var environmentVariables = new Dictionary<string, string>();

        var (certOutput, certExitCode) =
            await easyRsaExecCommandService.RunCommandAsync(
                certPathCommand, 
                environmentVariables,  
                cancellationToken,
                easyRsaPath);
        
        if (certExitCode != 0)
        {
            throw new Exception($"Error occurred while retrieving certificate serial: {certOutput}");
        }

        var serial = certOutput.Split('=')[1].Trim();
        _logger.LogInformation("Certificate serial retrieved:\n{Serial}\nFull OpenSSL response:\n{Output}",
            serial, certOutput);

        return serial;
    }

    private async Task<bool> UpdateCrlAsync(string easyRsaPath, CancellationToken cancellationToken)
    {
        easyRsaPath = Path.GetFullPath(easyRsaPath);
        var pkiPath = Path.Combine(easyRsaPath, "pki");
        var crlPath = Path.Combine(pkiPath, "crl.pem");

        var command = $"./easyrsa gen-crl";

        var environmentVariables = new Dictionary<string, string>
        {
            ["EASYRSA_BATCH"] = "1",
            ["EASYRSA_PKI"] = pkiPath
        };

        _logger.LogInformation("Executing EasyRSA CRL generation command: {Command}", command);

        var (output, exitCode) = await easyRsaExecCommandService.RunCommandAsync(
            command,
            environmentVariables,
            cancellationToken,
            easyRsaPath);

        if (exitCode != 0)
        {
            _logger.LogError("CRL generation failed. Output: {Output}", output);
            throw new Exception($"Failed to generate CRL. Error: {output}");
        }

        if (!File.Exists(crlPath))
        {
            throw new FileNotFoundException($"Generated CRL not found at {crlPath}. Command output: {output}");
        }

        _logger.LogInformation("CRL successfully generated at: {CrlPath}", crlPath);
        await SetCrlPermissionsAsync(easyRsaPath, crlPath, cancellationToken);
        return true;
    }
    
    private async Task SetCrlPermissionsAsync(string easyRsaPath, string crlPath, CancellationToken cancellationToken,
        bool throwIfFail = true)
    {
        File.SetAttributes(crlPath, FileAttributes.Normal);

        var chmodCommand = $"chmod 644 \"{crlPath}\" && chmod o+rx \"{Path.GetDirectoryName(crlPath)}\"";
        var (_, chmodExit) = await easyRsaExecCommandService.RunCommandAsync(
            chmodCommand,
            new Dictionary<string, string>(),
            cancellationToken,
            easyRsaPath);

        if (chmodExit != 0)
        {
            if (throwIfFail)
                throw new Exception("Failed to set correct permissions on crl.pem. OpenVPN might not be able to read it.");
            else
                _logger.LogWarning("⚠️ Failed to chmod crl.pem, OpenVPN might not be able to read it.");
        }
    }

    private async Task<ServerCertificate> MatchingCertsAsync(string easyRsaPath, string serialNumber, string commonName,
        CancellationToken cancellationToken)
    {
        // Parse index directly — do not call GetAll (would re-enter Install under the same PKI lock).
        var pkiPath = Path.Combine(Path.GetFullPath(easyRsaPath), "pki");
        var certificateInfoInIndexFile = await easyRsaParseDbService.ParseCertificateInfoInIndexFileAsync(
            pkiPath, cancellationToken);
        var matchingCerts = certificateInfoInIndexFile
            .Where(x => x.SerialNumber == serialNumber& x.CommonName == commonName)
            .ToList();

        if (!matchingCerts.Any())
        {
            throw new Exception($"Certificate not found in index.txt after generation.");
        }

        return matchingCerts.First();
    }
    
    private static bool IsRunningInWsl()
    {
        var os = RuntimeInformation.OSDescription.ToLower();
        return os.Contains("microsoft") || os.Contains("wsl");
    }

    private static readonly Regex BashPathRegex = new(@"^(/mnt/[a-z]|/[a-z])/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string ConvertToBashPath(string windowsPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return windowsPath;
        
        if (BashPathRegex.IsMatch(windowsPath))
            return windowsPath;

        var driveLetter = char.ToLower(windowsPath[0]);
        var pathWithoutDrive = windowsPath.Substring(2).Replace('\\', '/');

        return IsRunningInWsl()
            ? $"/mnt/{driveLetter}{pathWithoutDrive}"
            : $"/{driveLetter}{pathWithoutDrive}";
    }
    
    private static string? ExtractSerialFromRevocationOutput(string stderr)
    {
        var match = Regex.Match(stderr, @"Revoking Certificate (\w{16,})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
    private static string GetIssuedCertPath(string easyRsaDir, string commonName)
    {
        return Path.Combine(easyRsaDir, "pki", "issued", $"{commonName}.crt");
    }
}