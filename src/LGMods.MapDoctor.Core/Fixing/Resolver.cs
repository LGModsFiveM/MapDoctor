using System.Security.Cryptography;
using System.Text.Json;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Text;

namespace LGMods.MapDoctor.Core.Fixing;

// Written is the fingerprint of a combined version put in the file's place, so undo knows it's ours to take away.
public sealed record FileMove(string ResourceName, string FileName, string From, string To, string? Written = null)
{
    public bool IsMerge => Written is not null;
}

// Base is the copy that loads now, and the starting point for the merge.
public sealed record MergeRequest(StreamFile Base, IReadOnlyList<StreamFile> Copies);

public sealed record MergeSummary(string FileName, IReadOnlyList<string> Maps, MergedFile Result);

public sealed record FixPlan(string BackupFolder, IReadOnlyList<FileMove> Moves)
{
    // Combined files to write once the originals are in the backup, keyed by path.
    public IReadOnlyDictionary<string, byte[]> Writes { get; init; } = new Dictionary<string, byte[]>();
    public IReadOnlyList<MergeSummary> Merges { get; init; } = [];
}

// Saved next to the backups. It's written before anything moves (with Finished false), so even if the app
// or PC stops half way, the files can be put back.
public sealed record FixRecord(DateTime MadeAt, string BackupFolder, IReadOnlyList<FileMove> Moves, bool Finished = true)
{
    public const string FileName = "changes.json";
    public const string ReadMeName = "HOW-TO-PUT-BACK.txt";
}

// Nothing was left half done; the message says what to try.
public sealed class FixException(string message, Exception? inner = null) : Exception(message, inner);

// Resolves clashes by moving the unwanted copies into a backup folder, so the chosen version is the only one
// left. Nothing is deleted, the kept version is never touched (so it works for Escrow-locked maps too), and
// every fix can be undone.
public static class Resolver
{
    // Backups live next to server.cfg, outside the resources folder, so FiveM never loads them.
    public static string BackupRoot(string resourcesFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(resourcesFolder)) ?? resourcesFolder, "mapdoctor-backups");

    // Combining moves every copy to the backup too, then puts the combined version where each copy was, so every
    // map has the same file and it doesn't matter which one loads (or if one is removed later).
    public static FixPlan Plan(string resourcesFolder, IEnumerable<StreamFile> filesToMove, DateTime now,
        IEnumerable<MergeRequest>? merges = null)
    {
        // "2026-11-16_222746": no spaces, sorts by date. A second fix in the same second gets "_2", "_3"...
        var name = now.ToString("yyyy-MM-dd_HHmmss");
        var folder = Path.Combine(BackupRoot(resourcesFolder), name);
        for (var n = 2; Directory.Exists(folder); n++)
            folder = Path.Combine(BackupRoot(resourcesFolder), $"{name}_{n}");
        FileMove Move(StreamFile f, string? written = null) => new(
            f.Resource.Name,
            f.FileName,
            f.FullPath,
            Path.Combine(folder, Path.GetRelativePath(resourcesFolder, f.FullPath)),
            written);

        var writes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var summaries = new List<MergeSummary>();
        var moves = new List<FileMove>();
        foreach (var merge in merges ?? [])
        {
            MergedFile result;
            try
            {
                result = OcclusionMerge.Merge(merge.Base.FullPath,
                    merge.Copies.Where(c => c != merge.Base).Select(c => c.FullPath).ToList());
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new FixException($"We couldn't combine {merge.Base.FileName}, so nothing was changed. Pick one map's version instead.", e);
            }
            var hash = Fingerprint(result.Bytes);
            foreach (var copy in merge.Copies.DistinctBy(c => c.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                moves.Add(Move(copy, hash));
                writes[copy.FullPath] = result.Bytes;
            }
            summaries.Add(new MergeSummary(merge.Base.FileName,
                merge.Copies.Select(c => c.Resource.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), result));
        }

        // A file that's being combined isn't also moved out.
        moves.AddRange(filesToMove
            .DistinctBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
            .Where(f => !writes.ContainsKey(f.FullPath))
            .Select(f => Move(f)));
        return new FixPlan(folder, moves) { Writes = writes, Merges = summaries };
    }

    public static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string? FingerprintOf(string path)
    {
        try { return File.Exists(path) ? Fingerprint(File.ReadAllBytes(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsOurs(FileMove m) => m.Written is not null && FingerprintOf(m.From) == m.Written;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // If any move fails, everything already moved is put back before throwing.
    public static FixRecord Apply(FixPlan plan, DateTime now)
    {
        if (plan.Moves.Count == 0) throw new FixException("There's nothing to change.");

        // Write down what we're about to do before touching anything. CreateNew means we never
        // overwrite another fix's list, even if two fixes somehow got the same folder.
        var recordPath = Path.Combine(plan.BackupFolder, FixRecord.FileName);
        try
        {
            Directory.CreateDirectory(plan.BackupFolder);
            using var file = new FileStream(recordPath, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(file, new FixRecord(now, plan.BackupFolder, plan.Moves, Finished: false), JsonOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FixException("We couldn't create the backup folder, so nothing was changed. Try again in a moment.", e);
        }

        // The how-to-put-back note goes in straight away too, so even if the app or PC stops part way through,
        // the backup folder explains itself.
        WriteReadMe(new FixRecord(now, plan.BackupFolder, plan.Moves));

        var done = new List<FileMove>();
        var written = new List<string>();
        try
        {
            foreach (var move in plan.Moves)
            {
                if (!File.Exists(move.From))
                    throw new FixException($"{move.FileName} isn't in {move.ResourceName} any more. Scan again and try once more.");
                Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
                File.Move(move.From, move.To);
                done.Add(move);
            }

            // The originals are safe in the backup; now put the combined versions where they were.
            foreach (var (path, bytes) in plan.Writes)
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    file.Write(bytes);
                written.Add(path);
            }

            var record = new FixRecord(now, plan.BackupFolder, plan.Moves);
            File.WriteAllText(recordPath, JsonSerializer.Serialize(record, JsonOptions));
            return record;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FixException)
        {
            foreach (var path in written)
            {
                try { File.Delete(path); }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException) { } // PutBack then can't restore that original, and counts it
            }
            var stuck = PutBack(done, m => (m.To, m.From));
            if (stuck > 0)
                throw new FixException(
                    $"Something went wrong part way through, and we couldn't put {Plain.Count(stuck, "file")} back. " +
                    "They're safe in the backup folder: press \"Undo fixes\" and Undo to put them back.", e);

            DeleteNotes(plan.BackupFolder);
            Tidy(plan.BackupFolder);
            if (e is FixException) throw;
            throw new FixException(
                "We couldn't move one of the files, so nothing was changed. If your server is running, stop it and try again.", e);
        }
    }

    public static void Undo(FixRecord record)
    {
        var blocked = record.Moves.Where(m => File.Exists(m.From) && !IsOurs(m)).ToList();
        if (blocked.Count > 0)
            throw new FixException(blocked[0].IsMerge
                ? $"The combined {blocked[0].FileName} in {blocked[0].ResourceName} has been changed since, so we didn't overwrite it. " +
                  $"The backup is still in {record.BackupFolder}."
                : $"{blocked[0].ResourceName} already has a {blocked[0].FileName} again, so we didn't overwrite it. " +
                  $"The backup is still in {record.BackupFolder}.");

        var done = new List<FileMove>();
        var combined = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var move in record.Moves)
            {
                // Take away the combined file we put there (kept in memory in case we need to go back),
                // then the original goes back in its place.
                if (move.IsMerge && File.Exists(move.From))
                {
                    combined[move.From] = File.ReadAllBytes(move.From);
                    File.Delete(move.From);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(move.From)!);
                File.Move(move.To, move.From);
                done.Add(move);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var stuck = PutBack(done, m => (m.From, m.To));
            foreach (var (path, bytes) in combined)
            {
                try { if (!File.Exists(path)) File.WriteAllBytes(path, bytes); }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException) { stuck++; }
            }
            throw new FixException(stuck == 0
                ? "We couldn't put the files back, so nothing was changed. If your server is running, stop it and try again."
                : $"We couldn't put all the files back, and {Plain.Count(stuck, "file")} got stuck half way. " +
                  "Stop your server if it's running, then press Undo again.", e);
        }

        DeleteNotes(record.BackupFolder);
        Tidy(record.BackupFolder);
    }

    public static (int Undone, IReadOnlyList<string> Problems) UndoAll(string resourcesFolder)
    {
        var undone = 0;
        var problems = new List<string>();
        foreach (var record in FindRecords(resourcesFolder))
        {
            if (WhyCantUndo(record) is { } why)
            {
                problems.Add(why);
                continue;
            }
            try
            {
                Undo(record);
                undone++;
            }
            catch (FixException e)
            {
                problems.Add(e.Message);
            }
        }
        return (undone, problems);
    }

    private static void DeleteNotes(string backupFolder)
    {
        foreach (var name in new[] { FixRecord.FileName, FixRecord.ReadMeName })
        {
            try { File.Delete(Path.Combine(backupFolder, name)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // a leftover note is harmless
        }
    }

    private static void WriteReadMe(FixRecord record)
    {
        var lines = new List<string>
        {
            "LGMods Map Doctor moved these files here on " + record.MadeAt.ToString("d MMMM yyyy 'at' HH:mm") + ".",
            "Nothing was deleted. Your server doesn't load anything in this folder.",
            "",
            "TO PUT THEM BACK (the easy way)",
            "  Open LGMods Map Doctor, scan this server and press \"Undo fixes\" at the top.",
            "",
            "TO PUT THEM BACK BY HAND",
            "  1. Stop your server.",
            "  2. Copy everything in this folder (apart from this file and changes.json) into your server's",
            "     resources folder. The folders inside match your resources folder, so each file lands back",
            "     where it came from. If Windows asks about replacing a file, choose \"Skip\".",
            "  3. Start your server again.",
            "",
            "Files in this backup:",
        };
        if (record.Moves.Any(m => m.IsMerge))
            lines.InsertRange(lines.IndexOf("TO PUT THEM BACK BY HAND") + 1,
            [
                "  (Some files were combined: Map Doctor put a combined version where each original was.",
                "   Before copying, delete those combined files from your resources. They're marked below.)",
            ]);
        lines.AddRange(record.Moves.Select(m => $"  {m.FileName}  (from {m.ResourceName}){(m.IsMerge ? "  - combined version put in its place" : "")}"));
        try { File.WriteAllLines(Path.Combine(record.BackupFolder, FixRecord.ReadMeName), lines); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // only a helper; the fix still worked
    }

    // Newest first. Returns how many couldn't be reversed.
    private static int PutBack(List<FileMove> done, Func<FileMove, (string From, string To)> direction)
    {
        var stuck = 0;
        foreach (var move in Enumerable.Reverse(done))
        {
            var (from, to) = direction(move);
            try { File.Move(from, to); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { stuck++; }
        }
        return stuck;
    }

    // Removes mapdoctor-backups too once the last backup is gone.
    private static void Tidy(string backupFolder)
    {
        DeleteEmptyFolders(backupFolder);
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(backupFolder)) is { } root
            && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
        {
            try { Directory.Delete(root); } catch (IOException) { } // in use or no longer empty: leave it
        }
    }

    // Newest first. Paths are rebuilt from where the backup actually is, so it still works if the server folder
    // was moved, and anything that would point outside the resources or backup folder is ignored.
    public static IReadOnlyList<FixRecord> FindRecords(string resourcesFolder)
    {
        var root = BackupRoot(resourcesFolder);
        if (!Directory.Exists(root)) return [];

        var records = new List<FixRecord>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                var file = Path.Combine(dir, FixRecord.FileName);
                if (!File.Exists(file)) continue;
                var saved = JsonSerializer.Deserialize<FixRecord>(File.ReadAllText(file));
                if (saved?.Moves is not { Count: > 0 } || string.IsNullOrEmpty(saved.BackupFolder)) continue;

                var moves = new List<FileMove>();
                foreach (var m in saved.Moves)
                {
                    var relative = Path.GetRelativePath(saved.BackupFolder, m.To);
                    var to = Path.GetFullPath(Path.Combine(dir, relative));
                    var from = Path.GetFullPath(Path.Combine(resourcesFolder, relative));
                    if (!IsInside(to, dir) || !IsInside(from, resourcesFolder)) { moves = null; break; }
                    moves.Add(m with { From = from, To = to });
                }
                if (moves is null) continue;

                // A fix that stopped half way: only the files that actually reached the backup need putting back.
                if (!saved.Finished) moves = moves.Where(m => File.Exists(m.To)).ToList();
                if (moves.Count > 0) records.Add(new FixRecord(saved.MadeAt, Path.GetFullPath(dir), moves));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                          or ArgumentException or NotSupportedException)
            {
                // Not a fix we can read; leave it alone.
            }
        }
        return records.OrderByDescending(r => r.MadeAt).ToList();
    }

    public const string ForgottenName = "changes-forgotten.json";

    // Nothing is deleted: the backed-up files stay, and only the list of changes is renamed so Map Doctor
    // stops offering to undo it.
    public static void Forget(FixRecord record)
    {
        try
        {
            File.Move(Path.Combine(record.BackupFolder, FixRecord.FileName), Path.Combine(record.BackupFolder, ForgottenName), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FixException("We couldn't update the backup folder. Close anything that has it open and try again.", e);
        }
    }

    public static string? WhyCantUndo(FixRecord record)
    {
        var missing = record.Moves.FirstOrDefault(m => !File.Exists(m.To));
        if (missing is not null)
            return $"The backup of {missing.FileName} from {missing.ResourceName} isn't there any more, so this can't be undone.";
        var back = record.Moves.FirstOrDefault(m => File.Exists(m.From) && !IsOurs(m));
        if (back is not null)
            return back.IsMerge
                ? $"The combined {back.FileName} in {back.ResourceName} has changed since (maybe the map was updated), so undoing would overwrite it."
                : $"{back.ResourceName} already has a {back.FileName} again (maybe it was updated), so undoing would overwrite it.";
        return null;
    }

    private static bool IsInside(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteEmptyFolders(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var sub in Directory.GetDirectories(folder)) DeleteEmptyFolders(sub);
        if (!Directory.EnumerateFileSystemEntries(folder).Any())
        {
            try { Directory.Delete(folder); } catch (IOException) { } // in use or no longer empty: leave it
        }
    }
}
