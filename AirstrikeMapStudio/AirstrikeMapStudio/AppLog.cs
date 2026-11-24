using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace AirstrikeStudio
{
    /// <summary>
    /// Minimal, failure-proof diagnostic log. Best-effort operations (temp-file
    /// cleanup, icon loading, cosmetic reflection tweaks) use it instead of an
    /// empty catch block, so the failure is visible in studio.log and in the
    /// debugger output without interrupting the user. It never throws.
    /// Log file: %LOCALAPPDATA%\AirstrikeStudio\studio.log (rotated at 512 KB).
    /// </summary>
    public static class AppLog
    {
        private const long MaxBytes = 512 * 1024;
        private static readonly object Gate = new object();
        private static string path;

        /// <summary>Full path of the log file (may not exist yet).</summary>
        public static string LogPath
        {
            get
            {
                if (path == null)
                {
                    string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
                    path = Path.Combine(root, "AirstrikeStudio", "studio.log");
                }
                return path;
            }
        }

        /// <summary>
        /// Records a handled (non-fatal) exception. The calling file and member are
        /// filled in by the compiler, so call sites are simply <c>AppLog.Warn(ex)</c>.
        /// </summary>
        public static void Warn(Exception ex,
            [CallerMemberName] string member = "",
            [CallerFilePath] string file = "")
        {
            string where = Path.GetFileNameWithoutExtension(file) + "." + member;
            Write("WARN", where + ": " + (ex == null ? "(no exception)" : ex.GetType().Name + ": " + ex.Message));
        }

        /// <summary>Records an informational message.</summary>
        public static void Info(string message)
        {
            Write("INFO", message);
        }

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message;
            try { Debug.WriteLine("[AirstrikeStudio] " + line); } catch { /* logging must never throw */ }

            lock (Gate)
            {
                try
                {
                    string file = LogPath;
                    string dir = Path.GetDirectoryName(file);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    var info = new FileInfo(file);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = file + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(file, old);
                    }

                    File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception)
                {
                    // Disk full / read-only profile / locked file: the Debug output above
                    // is the only sink left. Deliberately swallowed: a logger must not
                    // become a new source of failures.
                }
            }
        }
    }
}
