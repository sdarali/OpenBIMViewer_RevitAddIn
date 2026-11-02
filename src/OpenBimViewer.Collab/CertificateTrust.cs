using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenBimViewer.Collab
{
    /// <summary>A server certificate Windows refused, for showing to the user.</summary>
    public sealed class RejectedCertificate
    {
        /// <summary>"host:port", the key a pin is stored under.</summary>
        public string HostKey { get; internal set; } = "";
        public string Subject { get; internal set; } = "";
        public string Issuer { get; internal set; } = "";
        public DateTime NotBefore { get; internal set; }
        public DateTime NotAfter { get; internal set; }
        /// <summary>SHA-256 of the certificate as "AB:CD:...", what is compared and pinned.</summary>
        public string Fingerprint { get; internal set; } = "";
        public IReadOnlyList<string> Problems { get; internal set; } = Array.Empty<string>();
    }

    /// <summary>
    /// HTTPS certificate checks: whatever Windows trusts is accepted as usual. A certificate Windows
    /// rejects (a development server's self-signed one, an internal CA that isn't installed, a name
    /// mismatch when connecting by IP address) is accepted only when the user has pinned that exact
    /// certificate, by SHA-256 fingerprint, for that host and port. A changed certificate is asked
    /// about again.
    /// </summary>
    public sealed class CertificateTrust
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, HashSet<string>> _pins = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private RejectedCertificate? _lastRejected;

        /// <param name="pins">"host:port" → pinned fingerprints.</param>
        public CertificateTrust(IDictionary<string, List<string>>? pins = null)
        {
            if (pins == null) return;
            foreach (var pair in pins)
            {
                foreach (var fp in pair.Value) Trust(pair.Key, fp);
            }
        }

        /// <summary>The last certificate refused, until it is trusted or cleared.</summary>
        public RejectedCertificate? LastRejected
        {
            get { lock (_lock) return _lastRejected; }
        }

        public bool HasPinsFor(string hostKey)
        {
            lock (_lock) return _pins.TryGetValue(hostKey, out var set) && set.Count > 0;
        }

        public void Trust(string hostKey, string fingerprint)
        {
            var normalized = Normalize(fingerprint);
            if (normalized.Length == 0) return;
            lock (_lock)
            {
                if (!_pins.TryGetValue(hostKey, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _pins[hostKey] = set;
                }
                set.Add(normalized);
                if (_lastRejected != null && _lastRejected.Fingerprint == normalized) _lastRejected = null;
            }
        }

        public void ClearLastRejected()
        {
            lock (_lock) _lastRejected = null;
        }

        /// <summary>For <see cref="HttpClientHandler.ServerCertificateCustomValidationCallback"/>.</summary>
        public bool Validate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) return true;
            if (certificate == null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;

            var uri = request?.RequestUri;
            var hostKey = uri == null ? "" : $"{uri.Host}:{uri.Port}";
            var fingerprint = Fingerprint(certificate);
            lock (_lock)
            {
                if (_pins.TryGetValue(hostKey, out var set) && set.Contains(fingerprint)) return true;
                _lastRejected = new RejectedCertificate
                {
                    HostKey = hostKey,
                    Subject = certificate.Subject,
                    Issuer = certificate.Issuer,
                    NotBefore = certificate.NotBefore,
                    NotAfter = certificate.NotAfter,
                    Fingerprint = fingerprint,
                    Problems = Describe(certificate, chain, errors, uri?.Host),
                };
            }
            return false;
        }

        public static string Fingerprint(X509Certificate2 certificate)
        {
            using (var sha = SHA256.Create())
            {
                return string.Join(":", sha.ComputeHash(certificate.RawData).Select(b => b.ToString("X2")));
            }
        }

        public static string Normalize(string? fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return "";
            var hex = new string(fingerprint!.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            return hex.Length != 64 ? "" : string.Join(":", Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2)));
        }

        private static IReadOnlyList<string> Describe(X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors errors, string? host)
        {
            var problems = new List<string>();
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            {
                problems.Add($"It isn't issued for \"{host}\".");
            }
            if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            {
                var statuses = chain?.ChainStatus.Select(s => s.Status).Where(s => s != X509ChainStatusFlags.NoError).Distinct().ToList()
                               ?? new List<X509ChainStatusFlags>();
                var selfSigned = string.Equals(certificate.Subject, certificate.Issuer, StringComparison.OrdinalIgnoreCase);
                if (selfSigned) problems.Add("It is self-signed (typical of a development server), so Windows doesn't trust it.");
                else if (statuses.Contains(X509ChainStatusFlags.UntrustedRoot) || statuses.Contains(X509ChainStatusFlags.PartialChain))
                    problems.Add("It was issued by a certificate authority Windows doesn't trust (e.g. a company CA that isn't installed).");
                if (statuses.Contains(X509ChainStatusFlags.NotTimeValid))
                    problems.Add($"It isn't valid now (valid {certificate.NotBefore:d} to {certificate.NotAfter:d}).");
                if (statuses.Contains(X509ChainStatusFlags.Revoked)) problems.Add("It has been revoked.");
                if (problems.Count == 0 && statuses.Count > 0) problems.Add("Windows reports: " + string.Join(", ", statuses));
            }
            if (problems.Count == 0) problems.Add("Windows reports: " + errors);
            return problems;
        }
    }
}
