using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Launcher.WebServer;

/// <summary>What <see cref="BindingGate.Evaluate"/> decided: either a refusal to hand the operator,
/// or the certificate (possibly none) Kestrel should serve.</summary>
public sealed record BindingDecision(X509Certificate2? Certificate, string? Refusal);

/// <summary>Stands between <see cref="WebServerOptions.AllowRemoteBinding"/> and a management API on
/// a public interface.
///
/// One boolean is not enough authority to move this process off loopback. The API it exposes can
/// start, stop and reconfigure every game server on the box, and the only thing in front of it is a
/// bearer token, so leaving loopback has to be paired with either TLS terminated here or a stated
/// reverse proxy terminating it in front. Neither is something this code can infer, which is exactly
/// why both are settings an operator has to write down.
///
/// The check refuses rather than warns. Nobody reads the startup log of a service that came up fine,
/// so a warning would be recorded precisely once, in the place least likely to be looked at, about a
/// mistake whose whole character is that it is discovered afterward by somebody else. Refusing to
/// start is the only version of this that cannot be missed.</summary>
public static class BindingGate
{
    /// <summary>EX_CONFIG from sysexits.h. Restarting cannot fix a configuration error, so the exit
    /// code says which kind of failure this is: a unit file can set RestartPreventExitStatus=78 and
    /// get one loud stop instead of a crash loop that buries the message.</summary>
    public const int ConfigExitCode = 78;

    /// <summary>Decide whether this configuration may listen beyond loopback, and load the
    /// certificate if one is configured. A non-null <see cref="BindingDecision.Refusal"/> means the
    /// process must print it and exit.</summary>
    public static BindingDecision Evaluate(WebServerOptions options)
    {
        X509Certificate2? certificate = null;

        if (!string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            var (loaded, problem) = LoadCertificate(
                options.CertificatePath, options.CertificatePassword);

            if (problem is not null)
                return new BindingDecision(null, CertificateRefusal(options.CertificatePath, problem));

            certificate = loaded;
        }

        // The certificate satisfies the requirement by existing and working; the proxy acknowledgement
        // satisfies it by being an assertion somebody made deliberately. Nothing here can verify a
        // proxy is really in front, and pretending otherwise would be worse than admitting it: the
        // setting's value is that it cannot be arrived at by accident.
        if (options.AllowRemoteBinding && certificate is null && !options.BehindReverseProxy)
            return new BindingDecision(null, UnprotectedRefusal);

        return new BindingDecision(certificate, null);
    }

    private static (X509Certificate2? Certificate, string? Problem) LoadCertificate(
        string path, string? password)
    {
        if (!File.Exists(path))
            return (null, "the file does not exist");

        X509Certificate2 certificate;
        try
        {
            certificate = new X509Certificate2(path, password);
        }
        catch (CryptographicException ex)
        {
            // The wrong password and the not-actually-a-PKCS#12-file cases both arrive here, and the
            // platform's own text distinguishes them better than anything this code could invent.
            return (null, ex.Message.TrimEnd('.'));
        }

        // A public-key-only certificate loads perfectly happily and then fails at the handshake, one
        // connection at a time, long after anyone is watching startup. Rejecting it here keeps the
        // failure where somebody is still looking at it.
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            return (null, "it carries no private key, so it cannot terminate TLS");
        }

        return (certificate, null);
    }

    private const string UnprotectedRefusal = """
        refusing to start: WebServer:AllowRemoteBinding is set, which would put the management API
        on a public interface with nothing in front of it but a bearer token.

        Binding beyond loopback needs one of these set as well:

          WebServer:CertificatePath      a PKCS#12 (.pfx) file this process serves HTTPS with,
                                         plus WebServer:CertificatePassword if it is protected
          WebServer:BehindReverseProxy   true, to state that a reverse proxy in front of this
                                         process terminates TLS. Set ASPNETCORE_URLS to the
                                         address that proxy reaches this process on

        Or unset WebServer:AllowRemoteBinding and keep listening on 127.0.0.1 only, which is the
        default and needs no configuration at all.

        Environment variable form: WebServer__AllowRemoteBinding, WebServer__CertificatePath,
        WebServer__CertificatePassword, WebServer__BehindReverseProxy.
        """;

    private static string CertificateRefusal(string path, string problem) =>
        $"""
        refusing to start: WebServer:CertificatePath is set to '{path}', but no usable
        certificate could be loaded from it: {problem}.

        Serving plain HTTP instead would silently undo the TLS this setting asked for, so a
        certificate that does not load is fatal rather than something to fall back from.

        Fix the file or WebServer:CertificatePassword, or unset WebServer:CertificatePath and set
        WebServer:BehindReverseProxy=true if a reverse proxy terminates TLS in front of this
        process.
        """;
}
