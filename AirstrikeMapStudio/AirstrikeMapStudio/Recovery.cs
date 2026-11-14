using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// One recovery snapshot: the map bytes, the map's source path (empty
    /// for a new unsaved map), the assets folder, and the environment
    /// override.
    /// </summary>
    public sealed class RecoveryData
    {
        public string Source;
        public string Assets;
        public string Environment;
        public byte[] Bytes;
    }

    /// <summary>
    /// Read/write of the binary recovery file format.
    /// </summary>
    /// <remarks>
    /// Format (little-endian):
    ///   String "AirstrikeRecovery1"
    ///   String Source
    ///   String Assets
    ///   Bool   HasEnvironment
    ///   [String Environment]     (only if HasEnvironment)
    ///   i32    BytesLength
    ///   Bytes  Bytes[BytesLength]
    ///
    /// Strings use BinaryReader's 7-bit-length-prefixed UTF-8 encoding.
    ///
    /// <see cref="Read"/> validates the map by re-loading it and comparing
    /// bytes; a corrupt recovery file is rejected at read time, not silently
    /// loaded into the editor.
    /// </remarks>
    public static class RecoveryStore
    {
        public static void Write(string path, RecoveryData data)
        {
            // Validate before touching disk.
            Map.Load(data.Bytes);

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write("AirstrikeRecovery1");
                    writer.Write(data.Source ?? "");
                    writer.Write(data.Assets ?? "");
                    writer.Write(data.Environment != null);
                    if (data.Environment != null)
                        writer.Write(data.Environment);
                    writer.Write(data.Bytes.Length);
                    writer.Write(data.Bytes);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            finally
            {
                // Cleanup must not hide the original failure, and must not
                // touch another writer's file.
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public static RecoveryData Read(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.ReadString() != "AirstrikeRecovery1")
                    throw new InvalidDataException("Unknown recovery format.");

                var data = new RecoveryData
                {
                    Source = reader.ReadString(),
                    Assets = reader.ReadString(),
                };

                if (reader.ReadBoolean())
                    data.Environment = reader.ReadString();

                int length = reader.ReadInt32();
                if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                    throw new InvalidDataException("Incomplete recovery copy.");

                data.Bytes = reader.ReadBytes(length);

                if (!Map.Load(data.Bytes).Bytes().SequenceEqual(data.Bytes))
                    throw new InvalidDataException("Recovery map failed validation.");

                return data;
            }
        }
    }

    public partial class Studio
    {
        /// <summary>
        /// Auto-save cadence for the recovery file. 5 minutes, chosen as a
        /// balance between "enough protection on a crash" and "not churning
        /// disk on a big map". Was 60 s in the original tool; changed here
        /// per a deliberate decision to reduce disk I/O.
        /// </summary>
        private const int RecoveryIntervalMs = 5 * 60 * 1000;

        private Timer recoveryTimer;
        private FileStream recoveryLock;
        private string recoveryPath;
        private string recoveredFrom;
        private string recoveryStatus = "Recovery: waiting for unsaved changes";

        private string RecoveryRoot =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recovery");

        private void SetupRecovery()
        {
            try
            {
                Directory.CreateDirectory(RecoveryRoot);

                // One recovery file per editor instance. The .lock file is held
                // open for the whole session; a crash releases the OS handle,
                // so the next launch can offer to restore from it.
                recoveryPath = Path.Combine(RecoveryRoot, Guid.NewGuid().ToString("N") + ".recovery");
                recoveryLock = new FileStream(
                    recoveryPath + ".lock",
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                recoveryTimer = new Timer { Interval = RecoveryIntervalMs };
                recoveryTimer.Tick += (s, e) => SaveRecovery();
                recoveryTimer.Start();

                Disposed += (s, e) =>
                {
                    recoveryTimer.Dispose();
                    recoveryLock.Dispose();
                };
            }
            catch (Exception ex)
            {
                recoveryStatus = "Recovery unavailable: " + ex.Message;
            }
        }

        private void SaveRecovery()
        {
            if (map == null || !dirty || recoveryLock == null) return;

            try
            {
                RecoveryStore.Write(recoveryPath, new RecoveryData
                {
                    Source = file,
                    Assets = assets.Root,
                    Environment = environmentOverride,
                    Bytes = map.Bytes(),
                });

                recoveryStatus = "Recovery copy saved " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception ex)
            {
                recoveryStatus = "Recovery save failed: " + ex.Message;
            }

            status.Text = recoveryStatus;
        }

        private void ClearRecovery()
        {
            try
            {
                if (recoveryPath != null && File.Exists(recoveryPath))
                    File.Delete(recoveryPath);

                // Also remove the source of a restored recovery, so the
                // crashed instance's file isn't offered again.
                if (recoveredFrom != null && File.Exists(recoveredFrom))
                    File.Delete(recoveredFrom);

                recoveredFrom = null;
                recoveryStatus = "Recovery: waiting for unsaved changes";
            }
            catch (Exception ex)
            {
                recoveryStatus = "Old recovery copy retained: " + ex.Message;
            }
        }

        /// <summary>
        /// If any recovery file from a previous session exists, offer to
        /// restore it. Returns true if a recovery was accepted (the caller
        /// should stop loading anything else).
        /// </summary>
        private bool OfferRecovery()
        {
            if (recoveryLock == null) return false;

            foreach (string candidate in Directory
                .GetFiles(RecoveryRoot, "*.recovery")
                .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                // Skip our own in-session recovery file.
                if (candidate == recoveryPath) continue;

                // Try to acquire the candidate's lock. If another editor
                // currently holds it, skip this file.
                FileStream guard = null;
                try
                {
                    guard = new FileStream(
                        candidate + ".lock",
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException)
                {
                    continue;
                }

                using (guard)
                {
                    try
                    {
                        var data = RecoveryStore.Read(candidate);

                        var answer = MessageBox.Show(
                            this,
                            "Restore recovery copy from " + File.GetLastWriteTime(candidate) + "?\r\n" +
                            (data.Source.Length == 0 ? "Unsaved new map" : data.Source) +
                            "\r\n\r\nRestores as an unsaved map. " +
                            "No leaves the recovery copy for later.",
                            "Recover map",
                            MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Question);

                        if (answer == DialogResult.Cancel) return false;
                        if (answer != DialogResult.Yes) continue;

                        if (Directory.Exists(data.Assets))
                            LoadAssets(data.Assets);

                        AdoptNewMap(Map.Load(data.Bytes), data.Environment);
                        recoveredFrom = candidate;

                        // Save the recovered map into this instance's recovery
                        // file so if *this* session crashes, we still have it.
                        SaveRecovery();
                        OpenGlWorkspace();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            this,
                            "Recovery copy could not be opened; it has been retained.\r\n" + ex.Message,
                            "Recovery");
                    }
                }
            }

            return false;
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private Timer recoveryDisplay;

        /// <summary>
        /// Polls the recovery status string every second and appends it to
        /// the document status label.
        /// </summary>
        public void ConnectRecoveryStatus(Func<string> get)
        {
            recoveryDisplay = new Timer { Interval = 1000 };
            recoveryDisplay.Tick += (s, e) =>
            {
                RefreshDocumentTitle();
                documentStatus.Text += "  |  " + get();
            };
            recoveryDisplay.Start();
            Disposed += (s, e) => recoveryDisplay.Dispose();
        }
    }
}