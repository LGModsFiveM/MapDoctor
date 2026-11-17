using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Text;

namespace LGMods.MapDoctor.Core.Fixing;

public sealed record FileMove(string ResourceName, string FileName, string From, string To);

// Base is the copy that loads now, and the starting point for the merge.
public sealed record MergeRequest(StreamFile Base, IReadOnlyList<StreamFile> Copies);

// Path is where the combined file goes, in Map Doctor's own resource.
public sealed record MergeSummary(string FileName, IReadOnlyList<string> Maps, MergedFile Result, string Path)
{
    public IReadOnlyList<CombinedSource> Sources { get; init; } = [];
}

// The fingerprint tells undo the file is still the one we wrote, so it's ours to take away. Sources are the map
// copies it was made from, so a scan can tell when a map update has left it out of date.
public sealed record CombinedFile(string FileName, string Path, string Fingerprint, IReadOnlyList<string> Maps)
{
    public IReadOnlyList<CombinedSource> Sources { get; init; } = [];
}

// Path is relative to the resources folder in the saved record, and full once read back.
public sealed record CombinedSource(string Map, string Path, string Fingerprint);

public sealed record FixPlan(string BackupFolder, IReadOnlyList<FileMove> Moves)
{
    public IReadOnlyList<MergeSummary> Merges { get; init; } = [];
    public string FixFolder { get; init; } = "";

    // server.cfg: where the fix resource gets started, if AddStartLine, and where its line comes out again on undo.
    public string? StartList { get; init; }
    public bool AddStartLine { get; init; }
}

// Saved next to the backups. It's written before anything moves (with Finished false), so even if the app
// or PC stops half way, the files can be put back.
public sealed record FixRecord(DateTime MadeAt, string BackupFolder, IReadOnlyList<FileMove> Moves, bool Finished = true)
{
    public const string FileName = "changes.json";
    public const string ReadMeName = "HOW-TO-PUT-BACK.txt";

    public IReadOnlyList<CombinedFile> Combined { get; init; } = [];
    public string? StartList { get; init; }
}

// Nothing was left half done; the message says what to try.
public sealed class FixException(string message, Exception? inner = null) : Exception(message, inner);

// Resolves clashes by moving the unwanted copies into a backup folder, so the chosen version is the only one
// left. Nothing is deleted, the kept version is never touched (so it works for Escrow-locked maps too), and
// every fix can be undone.
//
// Combined files go in Map Doctor's own resource instead, which starts after the maps so its versions are the
// ones that load. The maps themselves aren't touched, and once every combine is undone the resource goes too.
public static class Resolver
{
    public const string FixResourceName = "LGMods_MapDoctor";
    public const string StartLine = "ensure " + FixResourceName;
    private const string StartLineNote = "# Added by LGMods Map Doctor, so its combined map files load after your maps";

    private const string Manifest = """
        fx_version 'cerulean'
        game 'gta5'

        author 'LGMods'
        description 'LGMods Map Doctor: combined map files, so every map works'

        this_is_a_map 'yes'

        -- Keep "ensure LGMods_MapDoctor" as the last line of server.cfg, so these load after your maps.

        """;

    public static string FixFolder(string resourcesFolder) => Path.Combine(resourcesFolder, FixResourceName);

    // Backups live next to server.cfg, outside the resources folder, so FiveM never loads them.
    public static string BackupRoot(string resourcesFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(resourcesFolder)) ?? resourcesFolder, "mapdoctor-backups");

    public static FixPlan Plan(string resourcesFolder, IEnumerable<StreamFile> filesToMove, DateTime now,
        IEnumerable<MergeRequest>? merges = null, string? startList = null)
    {
        // "2026-11-16_222746": no spaces, sorts by date. A second fix in the same second gets "_2", "_3"...
        var name = now.ToString("yyyy-MM-dd_HHmmss");
        var folder = Path.Combine(BackupRoot(resourcesFolder), name);
        for (var n = 2; Directory.Exists(folder); n++)
            folder = Path.Combine(BackupRoot(resourcesFolder), $"{name}_{n}");
        FileMove Move(string resource, string path) =>
            new(resource, Path.GetFileName(path), path, Path.Combine(folder, Path.GetRelativePath(resourcesFolder, path)));

        var fixFolder = FixFolder(resourcesFolder);
        var summaries = new List<MergeSummary>();
        var moves = new List<FileMove>();
        var requests = (merges ?? []).ToList();
        // Distant-light files come in pairs that list the same lights in the same order, so combining one means
        // combining its partner too, even if the partner doesn't clash.
        foreach (var merge in requests.Where(m => LightsMerge.IsLights(m.Base.FileName)).ToList())
        {
            if (Partner(merge.Base) is not { } partner ||
                requests.Any(r => r.Base.FileName.Equals(partner.FileName, StringComparison.OrdinalIgnoreCase))) continue;
            requests.Add(new MergeRequest(partner, merge.Copies.Select(Partner).OfType<StreamFile>().ToList()));
        }
        foreach (var merge in requests)
        {
            // Always made from the maps' own copies, never from an earlier combined one.
            var sources = merge.Copies.Where(c => !IsOurs(c.Resource)).ToList();
            MergedFile result;
            try
            {
                result = FileMerge.Merge(merge.Base.FullPath, sources.Where(c => c != merge.Base).Select(c => c.FullPath).ToList());
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new FixException($"We couldn't combine {merge.Base.FileName}, so nothing was changed. Pick one map's version instead.", e);
            }
            // Combining again (say another map with this file was added): the old combined one goes to the backup.
            var path = Path.Combine(fixFolder, "stream", merge.Base.FileName);
            if (File.Exists(path)) moves.Add(Move(FixResourceName, path));
            var maps = sources.Select(c => c.Resource.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            summaries.Add(new MergeSummary(merge.Base.FileName, maps, result, path)
            {
                Sources = sources.Select(c => new CombinedSource(c.Resource.Name, Path.GetRelativePath(resourcesFolder, c.FullPath),
                    FingerprintOf(c.FullPath) ?? throw new FixException($"Map Doctor can't read {c.FullPath}, so nothing was changed."))).ToList(),
            });
        }

        moves.AddRange(filesToMove
            .DistinctBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
            .Where(f => !moves.Any(m => m.From.Equals(f.FullPath, StringComparison.OrdinalIgnoreCase)))
            .Select(f => Move(f.Resource.Name, f.FullPath)));
        return new FixPlan(folder, moves) { Merges = summaries, FixFolder = fixFolder, StartList = startList };
    }

    private static StreamFile? Partner(StreamFile file)
    {
        var partner = LightsMerge.Partner(file.FullPath);
        return file.Resource.StreamFiles.FirstOrDefault(s =>
            s.FileName.Equals(Path.GetFileName(partner), StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(s.FullPath) == Path.GetDirectoryName(partner));
    }

    public static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static bool IsOurs(ResourceInfo resource) => resource.Name.Equals(FixResourceName, StringComparison.OrdinalIgnoreCase);

    internal static string? FingerprintOf(string path)
    {
        try { return File.Exists(path) ? Fingerprint(File.ReadAllBytes(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // If any move fails, everything already moved is put back before throwing.
    public static FixRecord Apply(FixPlan plan, DateTime now)
    {
        if (plan.Moves.Count == 0 && plan.Merges.Count == 0) throw new FixException("There's nothing to change.");

        var record = new FixRecord(now, plan.BackupFolder, plan.Moves)
        {
            Combined = plan.Merges.Select(m => new CombinedFile(m.FileName, m.Path, Fingerprint(m.Result.Bytes), m.Maps) { Sources = m.Sources }).ToList(),
            StartList = plan.Merges.Count > 0 ? plan.StartList : null,
        };

        // Write down what we're about to do before touching anything. CreateNew means we never
        // overwrite another fix's list, even if two fixes somehow got the same folder.
        var recordPath = Path.Combine(plan.BackupFolder, FixRecord.FileName);
        try
        {
            Directory.CreateDirectory(plan.BackupFolder);
            using var file = new FileStream(recordPath, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(file, record with { Finished = false }, JsonOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FixException("We couldn't create the backup folder, so nothing was changed. Try again in a moment.", e);
        }

        // The how-to-put-back note goes in straight away too, so even if the app or PC stops part way through,
        // the backup folder explains itself.
        WriteReadMe(record);

        var done = new List<FileMove>();
        var written = new List<string>();
        var newResource = plan.Merges.Count > 0 && !Directory.Exists(plan.FixFolder);
        var addedLine = false;
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

            if (plan.Merges.Count > 0)
            {
                Directory.CreateDirectory(Path.Combine(plan.FixFolder, "stream"));
                var manifest = Path.Combine(plan.FixFolder, "fxmanifest.lua");
                File.WriteAllText(manifest, Manifest);
                foreach (var merge in plan.Merges)
                {
                    using (var file = new FileStream(merge.Path, FileMode.CreateNew, FileAccess.Write))
                        file.Write(merge.Result.Bytes);
                    written.Add(merge.Path);
                }
                if (plan.AddStartLine && plan.StartList is { } startList)
                    addedLine = AddStartLine(startList);
            }

            File.WriteAllText(recordPath, JsonSerializer.Serialize(record, JsonOptions));
            return record;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FixException)
        {
            // Only our own files are deleted here; anything that can't be is counted below as stuck.
            var lost = 0;
            try
            {
                if (addedLine) RemoveStartLine(plan.StartList!);
                if (newResource && Directory.Exists(plan.FixFolder)) Directory.Delete(plan.FixFolder, recursive: true);
                else foreach (var path in written) File.Delete(path);
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { lost++; }
            var stuck = PutBack(done, m => (m.To, m.From)) + lost;
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
        if (WhyCantUndo(record) is { } why) throw new FixException(why);

        var done = new List<FileMove>();
        var combined = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Take away the combined files we added (kept in memory in case we need to go back). That has to come
            // first: if this fix replaced an older combined file, it's in the backup and goes back to the same place.
            foreach (var file in record.Combined.Where(c => File.Exists(c.Path)))
            {
                combined[file.Path] = File.ReadAllBytes(file.Path);
                File.Delete(file.Path);
            }
            foreach (var move in record.Moves)
            {
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

        if (record.Combined.Count > 0) RemoveFixResourceIfEmpty(record);
        DeleteNotes(record.BackupFolder);
        Tidy(record.BackupFolder);
    }

    // Once no fix has anything left in it, the resource goes, and so does its line in server.cfg.
    private static void RemoveFixResourceIfEmpty(FixRecord record)
    {
        var folder = Path.GetDirectoryName(Path.GetDirectoryName(record.Combined[0].Path))!;
        try
        {
            if (Directory.Exists(folder))
            {
                if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Any(f => !Path.GetFileName(f).Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase))) return;
                Directory.Delete(folder, recursive: true);
            }
            if (record.StartList is { } startList && File.Exists(startList)) RemoveStartLine(startList);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The files are already back. An empty resource or a leftover line only makes FiveM print a warning.
        }
    }

    // Read and written as Latin1, which maps every byte to one character and back, so the rest of server.cfg
    // comes back byte for byte whatever encoding it's in.
    private static bool AddStartLine(string startList)
    {
        var text = File.ReadAllText(startList, Encoding.Latin1);
        if (text.Split('\n').Any(l => l.Trim().Equals(StartLine, StringComparison.OrdinalIgnoreCase))) return false;
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var gap = text.Length == 0 || text.EndsWith('\n') ? "" : nl;
        File.AppendAllText(startList, gap + nl + StartLineNote + nl + StartLine + nl, Encoding.Latin1);
        return true;
    }

    private static void RemoveStartLine(string startList)
    {
        var text = File.ReadAllText(startList, Encoding.Latin1);
        // Take out exactly what we added if it's still there, so the file ends the way it did before.
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        text = text.Replace(nl + StartLineNote + nl + StartLine + nl, "");
        var kept = text.Split('\n').Where(l => !l.Trim().Equals(StartLine, StringComparison.OrdinalIgnoreCase) && l.Trim() != StartLineNote);
        File.WriteAllText(startList, string.Join('\n', kept), Encoding.Latin1);
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
            "LGMods Map Doctor made these changes on " + record.MadeAt.ToString("d MMMM yyyy 'at' HH:mm") + ".",
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
        lines.AddRange(record.Moves.Count == 0
            ? ["  None. This fix only added combined files."]
            : record.Moves.Select(m => $"  {m.FileName}  (from {m.ResourceName})"));
        if (record.Combined.Count > 0)
        {
            lines.InsertRange(lines.IndexOf("TO PUT THEM BACK BY HAND") + 1,
            [
                $"  (This fix also added combined files to the {FixResourceName} resource. Delete them from its",
                $"   stream folder first. If that leaves it empty, delete the {FixResourceName} folder and the",
                $"   \"{StartLine}\" line in server.cfg.)",
            ]);
            lines.Add("");
            lines.Add($"Combined files added to {FixResourceName}:");
            lines.AddRange(record.Combined.Select(c => $"  {c.FileName}  (for {string.Join(", ", c.Maps)})"));
        }
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
                if (saved?.Moves is null || string.IsNullOrEmpty(saved.BackupFolder)) continue;

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

                // The fix resource is always in the same place, so only the file name is trusted.
                var combined = (saved.Combined ?? [])
                    .Where(c => c.FileName == Path.GetFileName(c.FileName))
                    .Select(c => c with
                    {
                        Path = Path.GetFullPath(Path.Combine(FixFolder(resourcesFolder), "stream", c.FileName)),
                        Sources = (c.Sources ?? [])
                            .Select(s => s with { Path = Path.GetFullPath(Path.Combine(resourcesFolder, s.Path)) })
                            .Where(s => IsInside(s.Path, resourcesFolder))
                            .ToList(),
                    })
                    .ToList();

                // A fix that stopped half way: only the files that actually reached the backup need putting back.
                if (!saved.Finished) moves = moves.Where(m => File.Exists(m.To)).ToList();
                if (moves.Count > 0 || combined.Count > 0)
                    records.Add(new FixRecord(saved.MadeAt, Path.GetFullPath(dir), moves) { Combined = combined, StartList = saved.StartList });
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
        var changed = record.Combined.FirstOrDefault(c => File.Exists(c.Path) && FingerprintOf(c.Path) != c.Fingerprint);
        if (changed is not null)
            return $"The combined {changed.FileName} in {FixResourceName} has changed since (maybe a newer fix replaced it), " +
                   "so undoing would overwrite it. Undo the newer fix first.";
        var ours = record.Combined.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var back = record.Moves.FirstOrDefault(m => File.Exists(m.From) && !ours.Contains(m.From));
        if (back is not null)
            return $"{back.ResourceName} already has a {back.FileName} again (maybe it was updated), so undoing would overwrite it.";
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
