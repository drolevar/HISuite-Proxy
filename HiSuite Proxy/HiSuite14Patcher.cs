using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace HiSuite_Proxy
{
    public enum HiSuite14PatchState { Original, Patched, Unsupported, Invalid }

    public sealed class HiSuite14PatchStatus
    {
        public string Version { get; private set; }
        public HiSuite14PatchState State { get; private set; }
        public string Message { get; private set; }
        public bool IsSupported { get { return State == HiSuite14PatchState.Original || State == HiSuite14PatchState.Patched; } }

        internal HiSuite14PatchStatus(string version, HiSuite14PatchState state, string message)
        {
            Version = version;
            State = state;
            Message = message;
        }

        public override string ToString() { return "HiSuite " + Version + ": " + State + ". " + Message; }
    }

    // This profile is deliberately limited to the original official 14.0.0.380 files.
    // It never treats a partial byte-pattern match as proof of compatibility.
    public static class HiSuite14Patcher
    {
        public const string SupportedVersion = "14.0.0.380";
        public const string BackupSuffix = ".hisuite-proxy.original";

        private sealed class FilePatch
        {
            internal readonly string Name, OriginalHash, PatchedHash;
            internal readonly int[] Offsets;
            internal readonly byte[] OldValues, NewValues;
            internal FilePatch(string name, string originalHash, string patchedHash, int[] offsets, byte[] oldValues, byte[] newValues)
            {
                Name = name; OriginalHash = originalHash; PatchedHash = patchedHash;
                Offsets = offsets; OldValues = oldValues; NewValues = newValues;
            }
        }

        private static readonly FilePatch[] Files =
        {
            // .380 checksum-failure path in FUN_0057b790 (CheckAllFileResult diagnostics).
            // This changes its local result from false to true, matching the released .610
            // patch. Pin the complete image: the same short signature occurs in unrelated code.
            new FilePatch("HiSuite.exe",
                "4549da99080922facf3349b78179dee975c1d5d259fe4ea41a533da46697d304",
                "d9cbc7713b923881a7c09cbb797fc30b32e333f250d6b34860c37f9e1d695eb8",
                new[] { 0x17ae5b }, new byte[] { 0 }, new byte[] { 1 }),
            new FilePatch("httpcomponent.dll",
                "302e710b21fcdabb7835b5cb12693f4eb1bd40a68f29df98e474ccb0e87f2ab3",
                "074960fa8ab2795a1592b6367e8413690255abf031483dc4c2b888a70edbbea2",
                new[] { 0x5021, 0x5096 }, new byte[] { 1, 1 }, new byte[] { 0, 5 })
        };

        private sealed class DirectoryState
        {
            internal string Directory, Version;
            internal HiSuite14PatchState State;
            internal readonly List<byte[]> Bytes = new List<byte[]>();
            internal HiSuite14PatchStatus Status()
            {
                return new HiSuite14PatchStatus(Version, State, State == HiSuite14PatchState.Original
                    ? "Both files match the supported original build."
                    : "Both files match the complete HiSuite Proxy patch.");
            }
        }

        public static HiSuite14PatchStatus Inspect(string directory)
        {
            try
            {
                DirectoryState state = ReadDirectory(directory);
                ValidateBackups(state, state.State == HiSuite14PatchState.Patched);
                return state.Status();
            }
            catch (UnsupportedVersionException error)
            {
                return new HiSuite14PatchStatus(error.Version, HiSuite14PatchState.Unsupported, error.Message);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException)
            {
                return new HiSuite14PatchStatus("unknown", HiSuite14PatchState.Invalid, error.Message);
            }
        }

        public static HiSuite14PatchStatus Apply(string directory)
        {
            DirectoryState state = ReadDirectory(directory);
            ValidateBackups(state, state.State == HiSuite14PatchState.Patched);
            if (state.State != HiSuite14PatchState.Patched)
            {
                EnsureBackups(state);
                List<byte[]> patched = new List<byte[]>();
                for (int i = 0; i < Files.Length; i++)
                {
                    byte[] bytes = (byte[])state.Bytes[i].Clone();
                    for (int j = 0; j < Files[i].Offsets.Length; j++)
                    {
                        int offset = Files[i].Offsets[j];
                        if (bytes[offset] != Files[i].OldValues[j]) throw new InvalidOperationException("The verified patch profile is inconsistent for " + Files[i].Name + ".");
                        bytes[offset] = Files[i].NewValues[j];
                    }
                    if (Hash(bytes) != Files[i].PatchedHash) throw new InvalidOperationException("The patched hash does not match the profile for " + Files[i].Name + ".");
                    patched.Add(bytes);
                }
                ReplaceFiles(state, patched, HiSuite14PatchState.Patched);
            }
            ApplyOptionalFirmwareUpdatePatch(state.Directory);
            return ReadDirectory(state.Directory).Status();
        }

        private static void ApplyOptionalFirmwareUpdatePatch(string directory)
        {
            // Patch FirmwareUpdate.dll (14.0.0.380) at file offset 0x1AA1D (RVA 0x0001B61D: 0x75 -> 0xEB)
            // so USB Update Mode / Recovery OS skips UpdateAuth and flashes UPDATE.APP directly.
            const string origFwHash = "4d9684629001460b6d0ec0543b2a5590cce9240bcef6d23033ab1313f47ee42c";
            const int fwOffset = 0x1AA1D;
            string fwPath = Path.Combine(directory, "FirmwareUpdate.dll");
            if (!PathExists(fwPath)) return;
            byte[] fwBytes = ReadRegularFile(fwPath);
            if (fwBytes.Length > fwOffset && fwBytes[fwOffset] == 0x75 && Hash(fwBytes) == origFwHash)
            {
                string backupPath = fwPath + BackupSuffix;
                if (!PathExists(backupPath)) WriteNew(backupPath, fwBytes);
                byte[] patchedFw = (byte[])fwBytes.Clone();
                patchedFw[fwOffset] = 0xEB;
                string tmpPath = fwPath + ".hisuiteproxy-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    WriteNew(tmpPath, patchedFw);
                    File.Replace(tmpPath, fwPath, null);
                }
                finally
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
            }
        }

        public static HiSuite14PatchStatus Restore(string directory)
        {
            DirectoryState state = ReadDirectory(directory);
            ValidateBackups(state, state.State == HiSuite14PatchState.Patched);
            if (state.State != HiSuite14PatchState.Original)
            {
                List<byte[]> original = new List<byte[]>();
                foreach (FilePatch file in Files) original.Add(ReadRegularFile(Path.Combine(state.Directory, file.Name + BackupSuffix)));
                ReplaceFiles(state, original, HiSuite14PatchState.Original);
            }
            string fwPath = Path.Combine(state.Directory, "FirmwareUpdate.dll");
            string fwBackup = fwPath + BackupSuffix;
            if (PathExists(fwPath) && PathExists(fwBackup))
            {
                byte[] origFw = ReadRegularFile(fwBackup);
                string tmpPath = fwPath + ".hisuiteproxy-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    WriteNew(tmpPath, origFw);
                    File.Replace(tmpPath, fwPath, null);
                }
                finally
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
            }
            return ReadDirectory(state.Directory).Status();
        }

        public static HiSuite14PatchStatus Prepare(string sourceDirectory, string destinationDirectory)
        {
            DirectoryState source = ReadDirectory(sourceDirectory);
            if (source.State != HiSuite14PatchState.Original) throw new InvalidOperationException("Prepare requires an original HiSuite directory.");
            string destination = FullDirectory(destinationDirectory);
            if (ContainsPath(source.Directory, destination) || ContainsPath(destination, source.Directory))
                throw new InvalidOperationException("Source and destination must be separate, non-nested directories.");
            if (PathExists(destination)) throw new IOException("The destination already exists: " + destination);
            string parent = Path.GetDirectoryName(destination);
            if (String.IsNullOrEmpty(parent) || !Directory.Exists(parent)) throw new DirectoryNotFoundException("The destination parent directory must already exist.");
            RejectReparsePath(parent);
            string staging = destination + ".hisuiteproxy-" + Guid.NewGuid().ToString("N");
            try
            {
                CopyDirectory(source.Directory, staging);
                Apply(staging);
                // Directory.Move refuses an existing destination; the finished copy is published only here.
                Directory.Move(staging, destination);
                return ReadDirectory(destination).Status();
            }
            finally
            {
                // Only our uniquely named sibling staging directory is eligible for cleanup.
                if (Directory.Exists(staging) && Path.GetDirectoryName(staging) == parent && staging.StartsWith(destination + ".hisuiteproxy-", StringComparison.OrdinalIgnoreCase))
                {
                    RejectReparsePath(staging);
                    Directory.Delete(staging, true);
                }
            }
        }

        private static DirectoryState ReadDirectory(string directory)
        {
            string full = FullDirectory(directory);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("HiSuite directory not found: " + full);
            RejectReparsePath(full);
            string version = ReadVersion(Path.Combine(full, "RunInfo.ini"));
            if (version != SupportedVersion) throw new UnsupportedVersionException(version);
            DirectoryState state = new DirectoryState { Directory = full, Version = version };
            int originals = 0, patched = 0;
            foreach (FilePatch file in Files)
            {
                byte[] bytes = ReadRegularFile(Path.Combine(full, file.Name));
                string hash = Hash(bytes);
                if (hash == file.OriginalHash) originals++;
                else if (hash == file.PatchedHash) patched++;
                else throw new InvalidOperationException(file.Name + " is not the supported original or complete patched file. SHA-256: " + hash);
                state.Bytes.Add(bytes);
            }
            if (originals == Files.Length) state.State = HiSuite14PatchState.Original;
            else if (patched == Files.Length) state.State = HiSuite14PatchState.Patched;
            else throw new InvalidOperationException("HiSuite files are in a mixed patch state. Restore a matching complete original build before continuing.");
            return state;
        }

        private static void ValidateBackups(DirectoryState state, bool required)
        {
            foreach (FilePatch file in Files)
            {
                string path = Path.Combine(state.Directory, file.Name + BackupSuffix);
                if (!PathExists(path))
                {
                    if (required) throw new InvalidOperationException("The original backup is missing: " + path);
                }
                else if (Hash(ReadRegularFile(path)) != file.OriginalHash)
                    throw new InvalidOperationException("The existing backup conflicts with the supported original file: " + path);
            }
        }

        private static void EnsureBackups(DirectoryState state)
        {
            for (int i = 0; i < Files.Length; i++)
            {
                string path = Path.Combine(state.Directory, Files[i].Name + BackupSuffix);
                if (!PathExists(path)) WriteNew(path, state.Bytes[i]);
            }
            ValidateBackups(state, true);
        }

        // Per-file replacement is atomic. If a later replacement fails, roll back earlier
        // replacements from the captured bytes. This is not a power-loss transaction.
        private static void ReplaceFiles(DirectoryState before, List<byte[]> after, HiSuite14PatchState expectedState)
        {
            List<string> staged = new List<string>();
            int changed = 0;
            try
            {
                for (int i = 0; i < Files.Length; i++)
                {
                    string path = Path.Combine(before.Directory, Files[i].Name + ".hisuiteproxy-" + Guid.NewGuid().ToString("N") + ".tmp");
                    staged.Add(path);
                    WriteNew(path, after[i]);
                }
                DirectoryState current = ReadDirectory(before.Directory);
                if (current.State != before.State) throw new InvalidOperationException("The HiSuite files changed while the patch was being prepared.");
                for (int i = 0; i < Files.Length; i++)
                {
                    File.Replace(staged[i], Path.Combine(before.Directory, Files[i].Name), null);
                    changed++;
                }
                if (ReadDirectory(before.Directory).State != expectedState) throw new IOException("The completed files failed verification.");
            }
            catch (Exception failure)
            {
                List<Exception> rollbackFailures = new List<Exception>();
                for (int i = changed - 1; i >= 0; i--)
                {
                    try
                    {
                        string target = Path.Combine(before.Directory, Files[i].Name);
                        if (Hash(ReadRegularFile(target)) != Hash(after[i])) throw new IOException("The file changed externally; rollback refused to overwrite it: " + target);
                        string rollback = target + ".hisuiteproxy-" + Guid.NewGuid().ToString("N") + ".tmp";
                        staged.Add(rollback);
                        WriteNew(rollback, before.Bytes[i]);
                        File.Replace(rollback, target, null);
                        if (Hash(ReadRegularFile(target)) != Hash(before.Bytes[i])) throw new IOException("Rollback verification failed for " + target);
                    }
                    catch (Exception rollbackFailure) { rollbackFailures.Add(rollbackFailure); }
                }
                if (rollbackFailures.Count != 0)
                {
                    rollbackFailures.Insert(0, failure);
                    throw new AggregateException("The patch failed and automatic rollback was incomplete. The original backups were retained.", rollbackFailures);
                }
                throw new IOException("The patch failed. Any completed replacements were rolled back; original backups were retained.", failure);
            }
            finally
            {
                foreach (string path in staged) if (File.Exists(path)) File.Delete(path);
            }
        }

        private static string ReadVersion(string path)
        {
            ReadRegularFile(path); // Reject a linked INI before parsing it.
            bool inVersion = false;
            string version = null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal)) inVersion = line.Equals("[version]", StringComparison.OrdinalIgnoreCase);
                else if (inVersion && line.StartsWith("version=", StringComparison.OrdinalIgnoreCase))
                {
                    if (version != null) throw new InvalidOperationException("RunInfo.ini contains more than one version entry.");
                    version = line.Substring(8).Trim();
                }
            }
            if (String.IsNullOrEmpty(version)) throw new InvalidOperationException("RunInfo.ini does not declare a HiSuite version.");
            return version;
        }

        private static byte[] ReadRegularFile(string path)
        {
            RejectReparsePath(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("Expected a regular file: " + path);
            return File.ReadAllBytes(path);
        }

        private static void WriteNew(string path, byte[] bytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            RejectReparsePath(source);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                RejectReparsePath(file);
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
            }
            foreach (string child in Directory.GetDirectories(source)) CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }

        private static string FullDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A HiSuite directory path is required.", "path");
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            return full.Length == root.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool ContainsPath(string parent, string child)
        {
            return child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectReparsePath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked/reparse paths are not accepted: " + current);
                current = Path.GetDirectoryName(current);
            }
        }

        private static bool PathExists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static string Hash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private sealed class UnsupportedVersionException : InvalidOperationException
        {
            internal readonly string Version;
            internal UnsupportedVersionException(string version) : base("Only the verified HiSuite " + SupportedVersion + " build is supported; found " + version + ".") { Version = version; }
        }
    }
}
