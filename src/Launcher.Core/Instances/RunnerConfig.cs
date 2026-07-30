using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Protocol;

namespace Launcher.Core.Instances;

/// <summary>Runner-level settings: where the local control plane listens, and whether this box offers
/// itself for official orchestration.</summary>
public sealed record RunnerConfig
{
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = ManagementProtocol.DefaultWebServerPort;

    /// <summary>The local control plane this runner dials out to. Outbound even on the same box: a
    /// local inbound exception would mean two auth models and two reconnect paths.</summary>
    public string? WebServerUrl { get; init; }

    /// <summary>Opt in to official orchestration. Sets available_for_control on every announce this
    /// box's servers send, which is what puts an offer in Conductor's adoption queue.</summary>
    public bool ConductorControl { get; init; }

    public string? ConductorUrl { get; init; }

    /// <summary>sha256 of the public key, lowercase hex. Announced alongside the offer so that
    /// accepting in a panel binds to this box and nothing else.</summary>
    public string? ControlKeyFingerprint { get; init; }

    /// <summary>Scopes the linked Conductor was granted at accept time. The runner enforces these and
    /// ignores whatever a command claims for itself.</summary>
    public IReadOnlyList<string>? GrantedScopes { get; init; }

    /// <summary>Where content packages are fetched from. Content-addressed, so this is normally the
    /// CDN in front of the store rather than the store itself.</summary>
    public string ContentBaseUrl { get; init; } = "https://master.vortexfps.org/content";

    public int PortPoolFirst { get; init; } = 26000;
    public int PortPoolLast { get; init; } = 26099;
}

/// <summary>Loads and saves <see cref="RunnerConfig"/>, and owns the runner's identity keypair.
///
/// The private key never leaves the box. Conductor stores only the public half, which is why
/// acceptance in a panel grants nothing on its own: control begins when this runner dials out and
/// proves possession, and an attacker who replayed somebody else's fingerprint cannot.</summary>
public sealed class RunnerConfigStore(LauncherPaths paths)
{
    private string ConfigPath => Path.Combine(paths.RunnerDir, "runner.json");
    private string PrivateKeyPath => Path.Combine(paths.RunnerDir, "control-key.pem");
    private string PublicKeyPath => Path.Combine(paths.RunnerDir, "control-key.pub");

    public RunnerConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new RunnerConfig();
            return ManagementProtocol.Deserialize<RunnerConfig>(File.ReadAllText(ConfigPath))
                   ?? new RunnerConfig();
        }
        catch (JsonException)
        {
            return new RunnerConfig();
        }
    }

    public void Save(RunnerConfig config)
    {
        Directory.CreateDirectory(paths.RunnerDir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, ManagementProtocol.Serialize(config));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    /// <summary>Create the keypair if this box does not have one. ECDSA P-256: small keys, signatures
    /// that fit comfortably in a WS frame, and nothing exotic on either end.</summary>
    public string EnsureKeyPair()
    {
        Directory.CreateDirectory(paths.RunnerDir);

        if (File.Exists(PrivateKeyPath))
            return Fingerprint();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(PrivateKeyPath, key.ExportECPrivateKeyPem());
        File.WriteAllText(PublicKeyPath, key.ExportSubjectPublicKeyInfoPem());
        RestrictToOwner(PrivateKeyPath);
        return Fingerprint();
    }

    public string Fingerprint()
    {
        using var key = LoadKey();
        var spki = key.ExportSubjectPublicKeyInfo();
        return Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
    }

    /// <summary>Sign the challenge a control plane issued at link time. This is the second half of the
    /// binding: the announce proved something answers at the endpoint, and this proves the same box
    /// holds the key it announced.</summary>
    public byte[] Sign(ReadOnlySpan<byte> challenge)
    {
        using var key = LoadKey();
        return key.SignData(challenge, HashAlgorithmName.SHA256);
    }

    public string PublicKeyPem() => File.ReadAllText(PublicKeyPath);

    public void DeleteKeyPair()
    {
        foreach (var path in new[] { PrivateKeyPath, PublicKeyPath })
            if (File.Exists(path))
                File.Delete(path);
    }

    private ECDsa LoadKey()
    {
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(PrivateKeyPath));
        return key;
    }

    /// <summary>Best effort. On Unix the private key must not be world-readable; on Windows the
    /// per-user data root already is not, and there is no chmod to apply.</summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { }
        catch (PlatformNotSupportedException) { }
    }
}
