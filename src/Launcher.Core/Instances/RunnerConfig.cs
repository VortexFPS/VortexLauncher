using System.Text;
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

    /// <summary>The master this box's servers announce to, or null for the one the game already points
    /// at. Only self-hosters running their own directory need to set it.
    ///
    /// Null rather than a copy of the official address on purpose: the game's sv_master_url default is
    /// the protocol's own, and restating it here would be a second place for it to drift. Nothing is
    /// pinned at launch unless this says otherwise.</summary>
    public string? MasterUrl { get; init; }

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

    /// <summary>The bearer token for this box's own control plane, stored as a hash plus a short
    /// clear prefix. Null on a runner that has never had one minted, which is what makes the panel
    /// reject everything until `vortex runner install-service` or `vortex runner new-token` runs.
    ///
    /// The token itself is shown once and never written anywhere, so there is no recovery path other
    /// than minting a new one. That is the point: a config file an operator can read the live token
    /// out of is a config file a backup, a support bundle or a screen share leaks it through.</summary>
    public RunnerWebToken? WebToken { get; init; }

    /// <summary>Where content packages are fetched from. Content-addressed, so this is normally the
    /// CDN in front of the store rather than the store itself.</summary>
    public string ContentBaseUrl { get; init; } = "https://master.vortexfps.org/content";

    public int PortPoolFirst { get; init; } = 26000;
    public int PortPoolLast { get; init; } = 26099;

    /// <summary>Where the Prometheus scrape endpoint binds. Loopback by default: the numbers describe
    /// how busy a host's servers are and how many of them there are, which is not something to publish
    /// on every interface because a monitoring stack somewhere wants it.
    ///
    /// An operator scraping from another box changes this deliberately, and at that point the endpoint
    /// is reachable by whatever else can reach that interface, which is the boundary the endpoint is
    /// designed around (see <see cref="Metrics.MetricsEndpoint"/> for why there is no token).</summary>
    public string MetricsBindAddress { get; init; } = "127.0.0.1";

    /// <summary>Port for the scrape endpoint, or 0 to run no listener at all.</summary>
    public int MetricsPort { get; init; } = Metrics.MetricsEndpoint.DefaultPort;
}

/// <summary>Loads and saves <see cref="RunnerConfig"/>, and owns the runner's identity keypair.
///
/// The private key never leaves the box. Conductor stores only the public half, which is why
/// acceptance in a panel grants nothing on its own: control begins when this runner dials out and
/// proves possession, and an attacker who replayed somebody else's fingerprint cannot.</summary>
public sealed class RunnerConfigStore(LauncherPaths paths)
{
    // Through RunnerLayout rather than composed here, because Launcher.WebServer reads this same file
    // for the token hash and cannot reference this project to find out where it is.
    private string ConfigPath => RunnerLayout.RunnerConfigPath(paths.Root);
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

    /// <summary>Mint a control plane token, store its hash, and hand back the clear value.
    ///
    /// The caller has exactly one chance to show it. Nothing here keeps a copy, so a caller that
    /// drops the return value has rotated the operator out of their own panel.</summary>
    public string IssueWebToken()
    {
        var clear = RunnerToken.Issue();
        Save(Load() with { WebToken = RunnerToken.Describe(clear) });
        return clear;
    }

    /// <summary>Mint one only if this runner has none.
    ///
    /// Null means there already was one, and there is nothing to print: a stored hash cannot produce
    /// the token back. Re-running an install must not silently invalidate the token the operator
    /// already has in their password manager, so replacing an existing one is only ever explicit.</summary>
    public string? EnsureWebToken() => Load().WebToken is null ? IssueWebToken() : null;

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

    /// <summary>Replace this box's identity key, proving continuity by signing the new public key
    /// with the old private one.
    ///
    /// That signature is what makes rotation safe without re-adoption. A Conductor holding the old
    /// public key can check that whoever is presenting a new key is the same box it already
    /// accepted, so there is no window where the server is unmanaged and no second trip through the
    /// adoption queue. An attacker who has neither key cannot produce it.
    ///
    /// The old key is kept until <see cref="CommitRotation"/>, so a rotation that fails partway
    /// leaves the runner able to authenticate with what it had.</summary>
    public RotationRequest BeginRotation()
    {
        if (!File.Exists(PrivateKeyPath))
            throw new InvalidOperationException(
                "this runner has no identity key; `vortex runner link` creates one");

        using var replacement = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var newPublicPem = replacement.ExportSubjectPublicKeyInfoPem();

        using var current = LoadKey();
        var signature = current.SignData(
            Encoding.UTF8.GetBytes(newPublicPem), HashAlgorithmName.SHA256);

        File.WriteAllText(PendingPrivateKeyPath, replacement.ExportECPrivateKeyPem());
        RestrictToOwner(PendingPrivateKeyPath);

        var fingerprint = Convert.ToHexString(
            SHA256.HashData(replacement.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

        return new RotationRequest(fingerprint, newPublicPem, Convert.ToBase64String(signature));
    }

    /// <summary>Promote the pending key once the control plane has accepted it. Only then does the
    /// old key stop being this runner's identity.</summary>
    public void CommitRotation()
    {
        if (!File.Exists(PendingPrivateKeyPath))
            throw new InvalidOperationException("no rotation is in progress");

        using var replacement = ECDsa.Create();
        replacement.ImportFromPem(File.ReadAllText(PendingPrivateKeyPath));

        File.WriteAllText(PrivateKeyPath, replacement.ExportECPrivateKeyPem());
        File.WriteAllText(PublicKeyPath, replacement.ExportSubjectPublicKeyInfoPem());
        RestrictToOwner(PrivateKeyPath);
        File.Delete(PendingPrivateKeyPath);
    }

    public void AbandonRotation()
    {
        if (File.Exists(PendingPrivateKeyPath))
            File.Delete(PendingPrivateKeyPath);
    }

    private string PendingPrivateKeyPath => Path.Combine(paths.RunnerDir, "control-key.pending.pem");

    /// <summary>What the runner sends to have a new key accepted.</summary>
    public sealed record RotationRequest(
        string NewFingerprint, string NewPublicKeyPem, string SignatureByCurrentKey);

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
