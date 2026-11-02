using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenBimViewer.Collab;

namespace OpenBimViewer.Revit.Services
{
    /// <summary>Per-user folders: settings and the session in %APPDATA%, the offline cache and log in %LOCALAPPDATA%.</summary>
    internal static class AddinStorage
    {
        public static string RoamingFolder => Ensure(Environment.SpecialFolder.ApplicationData);
        public static string LocalFolder => Ensure(Environment.SpecialFolder.LocalApplicationData);
        public static string CacheFolder => Path.Combine(LocalFolder, "cache");

        private static string Ensure(Environment.SpecialFolder root)
        {
            var dir = Path.Combine(Environment.GetFolderPath(root), "IfcCollab", "Revit");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Where BCF coordinates (the IFC file's) sit in the Revit model.</summary>
    internal enum CoordinateBase
    {
        /// <summary>The model's shared coordinates (Revit's IFC export default, "Shared Coordinates").</summary>
        Shared,
        /// <summary>Revit's internal origin.</summary>
        Internal,
        /// <summary>The project base point.</summary>
        ProjectBasePoint,
    }

    /// <summary>Non-secret settings (settings.json). The session is stored separately, encrypted; see <see cref="SessionStore"/>.</summary>
    internal sealed class AddinSettings
    {
        public string LastProjectId { get; set; } = "";

        /// <summary>
        /// The 3D view cameras are shown in. It is reused (never duplicated), so opening viewpoint
        /// after viewpoint doesn't fill the project browser.
        /// </summary>
        public string ViewName { get; set; } = "Open-BIM-View";

        /// <summary>
        /// Must match the "Coordinate base" the model's IFC was exported with, or cameras land in the
        /// wrong place. Shared coordinates is Revit's default.
        /// </summary>
        public CoordinateBase CoordinateBase { get; set; } = CoordinateBase.Shared;

        /// <summary>Vertical field of view (degrees) sent with captured views; Revit doesn't expose a camera's.</summary>
        public double FieldOfView { get; set; } = 60;

        /// <summary>Width in pixels of screenshots taken from Revit.</summary>
        public int SnapshotWidth { get; set; } = 1600;

        /// <summary>"host:port" → SHA-256 fingerprints of certificates the user chose to trust.</summary>
        public Dictionary<string, List<string>> TrustedCertificates { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private static string FilePath => Path.Combine(AddinStorage.RoamingFolder, "settings.json");

        public static AddinSettings Load()
        {
            var s = new AddinSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var o = Json.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8));
                s.LastProjectId = o.Str("lastProjectId");
                if (o.Str("viewName").Trim().Length > 0) s.ViewName = o.Str("viewName").Trim();
                if (Enum.TryParse<CoordinateBase>(o.Str("coordinateBase"), true, out var b)) s.CoordinateBase = b;
                var fov = o.Num("fieldOfView", s.FieldOfView);
                if (fov >= 10 && fov <= 120) s.FieldOfView = fov;
                var width = (int)o.Num("snapshotWidth", s.SnapshotWidth);
                if (width >= 320 && width <= 8000) s.SnapshotWidth = width;
                foreach (var pair in o.Obj("trustedCertificates") ?? new Dictionary<string, object?>())
                {
                    if (pair.Value is List<object?> list) s.TrustedCertificates[pair.Key] = list.OfType<string>().ToList();
                }
            }
            catch (Exception ex)
            {
                // A broken settings file shouldn't stop Revit loading the panel.
                Log.Warning("settings.json couldn't be read, using defaults: " + ex.Message);
            }
            return s;
        }

        public void Save()
        {
            var o = new Dictionary<string, object?>
            {
                ["lastProjectId"] = LastProjectId,
                ["viewName"] = ViewName,
                ["coordinateBase"] = CoordinateBase.ToString(),
                ["fieldOfView"] = FieldOfView,
                ["snapshotWidth"] = SnapshotWidth,
                ["trustedCertificates"] = TrustedCertificates.ToDictionary(p => p.Key, p => (object?)p.Value.Cast<object?>().ToList()),
            };
            File.WriteAllText(FilePath, Json.Write(o), new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// The Core's session (server, e-mail, refresh token, recent servers), encrypted with DPAPI for
    /// the current Windows user. The password is never stored.
    /// </summary>
    internal static class SessionStore
    {
        // Ties the ciphertext to this add-in; not a secret.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OpenBimViewer.Revit.session.v1");

        private static string FilePath => Path.Combine(AddinStorage.RoamingFolder, "session.bin");

        public static string? Load()
        {
            try
            {
                return File.Exists(FilePath) ? Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(FilePath), Entropy)) : null;
            }
            catch (Exception ex)
            {
                // Another Windows user's file, or corrupted: start signed out.
                Log.Warning("The saved session couldn't be read: " + ex.Message);
                return null;
            }
        }

        public static void Save(string json) => File.WriteAllBytes(FilePath, Dpapi.Protect(Encoding.UTF8.GetBytes(json), Entropy));
    }

    /// <summary>%LOCALAPPDATA%\IfcCollab\Revit\addin.log, trimmed when it grows past 1 MB.</summary>
    internal static class Log
    {
        private static readonly object Lock = new object();

        public static string FilePath => Path.Combine(AddinStorage.LocalFolder, "addin.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warning(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Core(int level, string message)
        {
            if (level <= 0) return; // the Core's verbose chatter
            Write(level >= 3 ? "ERROR" : level == 2 ? "WARN" : "INFO", "[core] " + message);
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Lock)
                {
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 1024 * 1024) info.Delete();
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
                }
            }
            catch (IOException)
            {
                // Logging must never fail an action.
            }
        }
    }
}
