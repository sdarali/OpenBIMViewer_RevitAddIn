using System.Collections.Generic;
using System.Text;
using System.Windows;
using OpenBimViewer.Collab;
using OpenBimViewer.Revit.Services;

namespace OpenBimViewer.Revit.UI
{
    /// <summary>
    /// When Windows rejects the server's HTTPS certificate (typically a development server's
    /// self-signed one), shows what was rejected and lets the user trust that exact certificate for
    /// that server. The decision is saved; a different certificate later is asked about again.
    /// </summary>
    internal static class CertificatePrompt
    {
        /// <summary>True when the user trusted the last rejected certificate (it is then pinned and saved).</summary>
        public static bool AskAndTrust(Window? owner, AddinSettings settings, CertificateTrust trust)
        {
            var rejected = trust.LastRejected;
            if (rejected == null) return false;

            var text = new StringBuilder();
            text.AppendLine($"The server {rejected.HostKey} presented an HTTPS certificate that Windows doesn't trust:");
            text.AppendLine();
            foreach (var problem in rejected.Problems) text.AppendLine("• " + problem);
            text.AppendLine();
            text.AppendLine($"Issued to:  {rejected.Subject}");
            text.AppendLine($"Issued by:  {rejected.Issuer}");
            text.AppendLine($"Valid:      {rejected.NotBefore:d} – {rejected.NotAfter:d}");
            text.AppendLine("SHA-256 fingerprint:");
            text.AppendLine(rejected.Fingerprint);
            text.AppendLine();
            if (trust.HasPinsFor(rejected.HostKey))
            {
                text.AppendLine("This is NOT the certificate you trusted before for this server. That can be a regenerated development certificate, or someone intercepting the connection.");
                text.AppendLine();
            }
            text.AppendLine("Trust this certificate for this server?");
            text.AppendLine("Only choose Yes if you expect it (your own or your company's server), ideally after checking the fingerprint with whoever runs it.");

            const string caption = "Open BIM Viewer: untrusted server certificate";
            var answer = owner != null
                ? MessageBox.Show(owner, text.ToString(), caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(text.ToString(), caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return false;

            if (!settings.TrustedCertificates.TryGetValue(rejected.HostKey, out var pins))
            {
                pins = new List<string>();
                settings.TrustedCertificates[rejected.HostKey] = pins;
            }
            if (!pins.Contains(rejected.Fingerprint)) pins.Add(rejected.Fingerprint);
            settings.Save();
            trust.Trust(rejected.HostKey, rejected.Fingerprint);
            return true;
        }
    }
}
