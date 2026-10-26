using System;
using System.IO;
using System.Security.Cryptography;
using HiSuite_Proxy;

internal static class PatchEngineTests
{
    private static string source, runRoot;
    private static int passed, failed;
    private static readonly string[] files = { "HiSuite.exe", "httpcomponent.dll" };

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: PatchEngine.Tests.exe <original-HiSuite-directory> <new-fixture-directory>");
            return 2;
        }
        source = Path.GetFullPath(args[0]);
        runRoot = Path.GetFullPath(args[1]);
        if (HiSuite14Patcher.Inspect(source).State != HiSuite14PatchState.Original)
        {
            Console.Error.WriteLine("The input must be the hash-pinned original HiSuite " + HiSuite14Patcher.SupportedVersion + " directory.");
            return 2;
        }
        if (Directory.Exists(runRoot) || File.Exists(runRoot) || IsWithin(source, runRoot) || IsWithin(runRoot, source))
        {
            Console.Error.WriteLine("The fixture directory must be new and separate from the original HiSuite directory.");
            return 2;
        }
        Directory.CreateDirectory(runRoot);
        Test("original -> apply -> idempotent -> restore -> reapply", RoundTrip);
        Test("corrupt source rejected before any backup or mutation", Corrupt);
        Test("partial HTTP patch rejected", Partial);
        Test("mixed complete file states rejected", Mixed);
        Test("wrong version rejected", WrongVersion);
        Test("conflicting existing backup is preserved and blocks mutation", ConflictingBackup);
        Test("missing original backup blocks restore", MissingBackup);
        Test("second-file failure rolls back apply", ApplyRollback);
        Test("second-file failure rolls back restore", RestoreRollback);
        Test("prepare publishes a patched copy and preserves source tree", PrepareCopy);
        Test("prepare rejects existing, equal, and nested destination", PrepareRejected);
        // Every mutation is against a fixture copy. Check the supplied originals once more after all cases.
        Assert(HiSuite14Patcher.Inspect(source).State == HiSuite14PatchState.Original, "The supplied original files changed during the suite.");
        Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed. Fixtures: " + runRoot);
        return failed == 0 ? 0 : 1;
    }

    private static bool IsWithin(string parent, string child)
    {
        string prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return child.Equals(prefix, StringComparison.OrdinalIgnoreCase) || child.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL: " + name + "\n" + error); }
    }

    private static string Fixture(string name)
    {
        string directory = Path.Combine(runRoot, name);
        Directory.CreateDirectory(directory);
        foreach (string file in files) File.Copy(Path.Combine(source, file), Path.Combine(directory, file));
        File.Copy(Path.Combine(source, "RunInfo.ini"), Path.Combine(directory, "RunInfo.ini"));
        return directory;
    }

    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (IOException) { rejected = true; }
        catch (InvalidOperationException) { rejected = true; }
        catch (UnauthorizedAccessException) { rejected = true; }
        Assert(rejected, "The operation was accepted unexpectedly.");
    }

    private static string Hash(string path)
    {
        using (SHA256 algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(File.ReadAllBytes(path)));
    }

    private static void AssertOriginalFiles(string directory)
    {
        foreach (string file in files) Assert(Hash(Path.Combine(directory, file)) == Hash(Path.Combine(source, file)), file + " no longer matches the source.");
    }

    private static void AssertNoStages(string directory)
    {
        Assert(Directory.GetFiles(directory, "*.hisuiteproxy-*.tmp").Length == 0, "A temporary replacement file was left behind.");
    }

    private static void RoundTrip()
    {
        string directory = Fixture("roundtrip");
        Assert(HiSuite14Patcher.Inspect(directory).State == HiSuite14PatchState.Original, "Original identity was not recognized.");
        Assert(HiSuite14Patcher.Apply(directory).State == HiSuite14PatchState.Patched, "Apply did not report the patched state.");
        foreach (string file in files) Assert(Hash(Path.Combine(directory, file + HiSuite14Patcher.BackupSuffix)) == Hash(Path.Combine(source, file)), "The original backup differs.");
        DateTime exeTime = File.GetLastWriteTimeUtc(Path.Combine(directory, "HiSuite.exe"));
        DateTime backupTime = File.GetLastWriteTimeUtc(Path.Combine(directory, "HiSuite.exe" + HiSuite14Patcher.BackupSuffix));
        Assert(HiSuite14Patcher.Apply(directory).State == HiSuite14PatchState.Patched, "Repeated apply failed.");
        Assert(exeTime == File.GetLastWriteTimeUtc(Path.Combine(directory, "HiSuite.exe")), "Idempotent apply rewrote the executable.");
        Assert(backupTime == File.GetLastWriteTimeUtc(Path.Combine(directory, "HiSuite.exe" + HiSuite14Patcher.BackupSuffix)), "Idempotent apply rewrote the backup.");
        Assert(HiSuite14Patcher.Restore(directory).State == HiSuite14PatchState.Original, "Restore did not report the original state.");
        AssertOriginalFiles(directory);
        Assert(HiSuite14Patcher.Restore(directory).State == HiSuite14PatchState.Original, "Repeated restore failed.");
        Assert(HiSuite14Patcher.Apply(directory).State == HiSuite14PatchState.Patched, "Reapply with existing valid backups failed.");
        AssertNoStages(directory);
    }

    private static void Corrupt()
    {
        string directory = Fixture("corrupt");
        string exe = Path.Combine(directory, "HiSuite.exe");
        byte[] bytes = File.ReadAllBytes(exe); bytes[0x400] ^= 1; File.WriteAllBytes(exe, bytes);
        string corruptHash = Hash(exe);
        Assert(HiSuite14Patcher.Inspect(directory).State == HiSuite14PatchState.Invalid, "Corruption was not detected.");
        Reject(() => HiSuite14Patcher.Apply(directory));
        Assert(Hash(exe) == corruptHash, "Rejected apply modified the corrupt input.");
        Assert(Hash(Path.Combine(directory, "httpcomponent.dll")) == Hash(Path.Combine(source, "httpcomponent.dll")), "Rejected apply changed the other file.");
        Assert(Directory.GetFiles(directory, "*" + HiSuite14Patcher.BackupSuffix).Length == 0, "Validation failure created backups.");
    }

    private static void Partial()
    {
        string directory = Fixture("partial");
        string dll = Path.Combine(directory, "httpcomponent.dll");
        byte[] bytes = File.ReadAllBytes(dll); bytes[0x5021] = 0; File.WriteAllBytes(dll, bytes);
        Assert(HiSuite14Patcher.Inspect(directory).State == HiSuite14PatchState.Invalid, "A partial edit was recognized as complete.");
        Reject(() => HiSuite14Patcher.Apply(directory));
    }

    private static void Mixed()
    {
        string patched = Fixture("mixed-patched"), mixed = Fixture("mixed");
        HiSuite14Patcher.Apply(patched);
        File.Copy(Path.Combine(patched, "httpcomponent.dll"), Path.Combine(mixed, "httpcomponent.dll"), true);
        Assert(HiSuite14Patcher.Inspect(mixed).State == HiSuite14PatchState.Invalid, "Mixed file states were recognized as valid.");
        Reject(() => HiSuite14Patcher.Apply(mixed));
        Reject(() => HiSuite14Patcher.Restore(mixed));
    }

    private static void WrongVersion()
    {
        string directory = Fixture("unsupported");
        File.WriteAllText(Path.Combine(directory, "RunInfo.ini"), "[version]\r\nversion=14.0.0.370\r\n");
        Assert(HiSuite14Patcher.Inspect(directory).State == HiSuite14PatchState.Unsupported, "Unsupported version was accepted.");
        Reject(() => HiSuite14Patcher.Apply(directory));
        AssertOriginalFiles(directory);
    }

    private static void ConflictingBackup()
    {
        string directory = Fixture("backup-conflict");
        string backup = Path.Combine(directory, "httpcomponent.dll" + HiSuite14Patcher.BackupSuffix);
        File.WriteAllText(backup, "preserve this file");
        Assert(HiSuite14Patcher.Inspect(directory).State == HiSuite14PatchState.Invalid, "Inspection accepted a conflicting original backup.");
        Reject(() => HiSuite14Patcher.Apply(directory));
        Assert(File.ReadAllText(backup) == "preserve this file", "The conflicting backup was replaced.");
        Assert(!File.Exists(Path.Combine(directory, "HiSuite.exe" + HiSuite14Patcher.BackupSuffix)), "A backup was created before all conflicts were checked.");
        AssertOriginalFiles(directory);
    }

    private static void MissingBackup()
    {
        string directory = Fixture("missing-backup");
        HiSuite14Patcher.Apply(directory);
        File.Delete(Path.Combine(directory, "httpcomponent.dll" + HiSuite14Patcher.BackupSuffix));
        Assert(HiSuite14Patcher.Inspect(directory).State == HiSuite14PatchState.Invalid, "Inspection accepted a patched installation with a missing original backup.");
        string exeHash = Hash(Path.Combine(directory, "HiSuite.exe"));
        Reject(() => HiSuite14Patcher.Restore(directory));
        Assert(Hash(Path.Combine(directory, "HiSuite.exe")) == exeHash, "Restore changed a file before validating every backup.");
    }

    private static void ApplyRollback()
    {
        string directory = Fixture("apply-rollback");
        using (FileStream locked = new FileStream(Path.Combine(directory, "httpcomponent.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => HiSuite14Patcher.Apply(directory));
        AssertOriginalFiles(directory);
        AssertNoStages(directory);
    }

    private static void RestoreRollback()
    {
        string directory = Fixture("restore-rollback");
        HiSuite14Patcher.Apply(directory);
        string exeHash = Hash(Path.Combine(directory, "HiSuite.exe")), dllHash = Hash(Path.Combine(directory, "httpcomponent.dll"));
        using (FileStream locked = new FileStream(Path.Combine(directory, "httpcomponent.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => HiSuite14Patcher.Restore(directory));
        Assert(Hash(Path.Combine(directory, "HiSuite.exe")) == exeHash && Hash(Path.Combine(directory, "httpcomponent.dll")) == dllHash, "Failed restore did not roll back to the complete patched state.");
        AssertNoStages(directory);
    }

    private static void PrepareCopy()
    {
        string directory = Fixture("prepare-source"), destination = Path.Combine(runRoot, "prepared");
        Directory.CreateDirectory(Path.Combine(directory, "driver"));
        File.WriteAllText(Path.Combine(directory, "driver", "fixture.txt"), "dependency tree");
        Assert(HiSuite14Patcher.Prepare(directory, destination).State == HiSuite14PatchState.Patched, "Prepare did not publish a patched directory.");
        AssertOriginalFiles(directory);
        Assert(File.ReadAllText(Path.Combine(destination, "driver", "fixture.txt")) == "dependency tree", "Prepare lost a dependency file.");
        Assert(Directory.GetDirectories(runRoot, "prepared.hisuiteproxy-*").Length == 0, "Prepare left a staging directory.");
    }

    private static void PrepareRejected()
    {
        string directory = Fixture("prepare-reject"), exists = Path.Combine(runRoot, "existing");
        Directory.CreateDirectory(exists);
        File.WriteAllText(Path.Combine(exists, "preserve.txt"), "existing data");
        Reject(() => HiSuite14Patcher.Prepare(directory, exists));
        Reject(() => HiSuite14Patcher.Prepare(directory, directory));
        Reject(() => HiSuite14Patcher.Prepare(directory, Path.Combine(directory, "child")));
        Assert(!Directory.Exists(Path.Combine(directory, "child")), "A nested destination was created.");
        Assert(File.ReadAllText(Path.Combine(exists, "preserve.txt")) == "existing data", "Existing output was changed.");
        AssertOriginalFiles(directory);
    }
}
