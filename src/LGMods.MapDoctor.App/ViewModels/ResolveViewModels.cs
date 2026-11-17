using System.IO;
using System.Windows.Input;
using LGMods.MapDoctor.App.Services;
using LGMods.MapDoctor.Core.Fixing;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Text;

namespace LGMods.MapDoctor.App.ViewModels;

// What's on screen on the results page, so it can come back after scanning again.
public sealed record ViewState(bool IsMapView, string? MapName, string? FindingKey);

// Step 1 chooses whose version of each clashing file to use, step 2 confirms exactly what moves.
// The versions kept are never touched; the others go to a backup folder outside resources, so the fix can be undone.
public sealed class ResolveViewModel : ObservableObject
{
    private readonly ResultsViewModel _owner;
    private bool _isConfirming;
    private string? _error;
    private FixPlan? _plan;

    public ResolveViewModel(ResultsViewModel owner, IReadOnlyList<FindingViewModel> clashes, ResourceInfo? focus)
    {
        _owner = owner;
        Focus = focus;
        Groups = clashes.Select(c => new ResolveGroup(c, focus, owner.Result, this)).ToList();

        Title = focus is not null ? $"Fix clashes for {focus.Name}" : "Fix this clash";
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(owner.Result.ResourcesFolder)) ?? "";
        var backupFolder = Path.Combine(Path.GetFileName(root), "mapdoctor-backups");
        var howItWorks = new List<string>
        {
            "Two maps can't both change the same part of the GTA world. When they do, only one shows and the other is hidden.",
            focus is not null
                ? $"For each clash, pick which map should show. We've picked {focus.Name} where it's hidden right now."
                : "Pick which map should show. Usually that's the one players visit most, or the one you just bought.",
            $"We move the other maps' copies of those files into {backupFolder} (next to server.cfg). Nothing is deleted, and you can undo it any time.",
        };
        if (Groups.Any(g => g.CanMerge))
            howItWorks.Insert(2,
                "Some clashes can be combined instead, so every map works and nothing is hidden. We've picked that wherever it's possible.");
        HowItWorks = howItWorks;

        CancelCommand = new RelayCommand(owner.CloseDialog);
        NextCommand = new RelayCommand(Next, () => MoveCount > 0 || MergeCount > 0 || FineChanges.Any());
        BackCommand = new RelayCommand(() => { Error = null; IsConfirming = false; });
        ApplyCommand = new RelayCommand(Apply);
        LeaveAllCommand = new RelayCommand(() => { foreach (var g in Groups) g.ChooseAll(null); });
        UseFocusForAllCommand = new RelayCommand(() => { foreach (var g in Groups) g.ChooseAll(focus); });
    }

    public ResourceInfo? Focus { get; }
    public bool HasFocus => Focus is not null;
    public string FocusName => Focus?.Name ?? "";
    public string Title { get; }
    public IReadOnlyList<string> HowItWorks { get; }
    public IReadOnlyList<ResolveGroup> Groups { get; }

    public bool IsChoosing => !IsConfirming;

    public bool IsConfirming
    {
        get => _isConfirming;
        private set
        {
            if (!Set(ref _isConfirming, value)) return;
            OnPropertyChanged(nameof(IsChoosing));
            OnPropertyChanged(nameof(StepText));
        }
    }

    public string StepText => IsConfirming ? "Step 2 of 2  ·  Check and confirm" : "Step 1 of 2  ·  Choose";

    private IEnumerable<ResolveFile> AllFiles => Groups.SelectMany(g => g.Files);

    public List<StreamFile> FilesToMove() => AllFiles.SelectMany(f => f.FilesToMove()).ToList();

    public int MoveCount => FilesToMove().Count;

    public List<MergeRequest> MergeRequests() => AllFiles.Select(f => f.MergeRequest).OfType<MergeRequest>().ToList();
    public int MergeCount => MergeRequests().Count;

    public int ChangedGroups => Groups.Count(g => g.IsChanged);
    public int IgnoredGroups => Groups.Count(g => g.Ignore);

    // Clashes whose "marked as fine" state will change: newly ignored, or no longer ignored.
    public IEnumerable<ResolveGroup> FineChanges => Groups.Where(g => g.Ignore != g.Finding.IsFine);

    public string Summary
    {
        get
        {
            var changed = ChangedGroups;
            var ignored = IgnoredGroups;
            var left = Groups.Count - changed - ignored;
            if (changed == 0 && !FineChanges.Any())
                return "Nothing is set to change yet. Pick a map, or ignore a clash.";
            var parts = new List<string>();
            if (changed > 0) parts.Add(Groups.Count == 1 ? "This clash will be fixed" : $"{changed} of {Groups.Count} clashes will be fixed");
            if (ignored > 0) parts.Add(Groups.Count == 1 ? "This clash will be ignored" : $"{ignored} ignored");
            if (left > 0 && Groups.Count > 1) parts.Add($"{left} left as {(left == 1 ? "it is" : "they are")}");
            return string.Join(", ", parts) + ".";
        }
    }

    public string NextLabel => "Review changes";

    internal void ChoiceChanged()
    {
        foreach (var name in new[] { nameof(MoveCount), nameof(MergeCount), nameof(Summary), nameof(ChangedGroups), nameof(IgnoredGroups) })
            OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
    }

    private int PlannedMoves => Moves.Sum(m => m.Files.Count);
    private int PlannedMerges => Merges.Count;

    public string ConfirmTitle
    {
        get
        {
            var parts = new List<string>();
            if (PlannedMerges > 0) parts.Add($"combine {Plain.Count(PlannedMerges, "file")}");
            if (PlannedMoves > 0) parts.Add(PlannedMerges > 0 || Ignoring.Count > 0
                ? $"move {Plain.Count(PlannedMoves, "file")}"
                : $"move {Plain.Count(PlannedMoves, "file")} to a backup");
            if (Ignoring.Count > 0) parts.Add($"ignore {Plain.Count(Ignoring.Count, "clash", "clashes")}");
            var text = Plain.JoinAnd(parts);
            return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..] + "?";
        }
    }

    public string ConfirmText => (PlannedMerges > 0, PlannedMoves > 0) switch
    {
        (true, _) => "Combined files go into every map involved, so it doesn't matter which one loads. The originals, and " +
                     "any copies taken out, are kept in a backup. Nothing is deleted.",
        (false, true) => "These copies will be taken out of your resources so the versions you chose are the only ones left. " +
                         "The versions you keep aren't touched, and nothing is deleted.",
        _ => "No files will be moved. These clashes will be marked as fine, so they stop showing as problems. " +
             "You can unmark them any time.",
    };

    // Anything going to the backup folder (moved copies, or originals of combined files).
    public bool HasMoves => _plan is not null;
    public bool HasMovedOut => Moves.Count > 0;
    public IReadOnlyList<MergeLine> Merges { get; private set; } = [];
    public bool HasMerges => Merges.Count > 0;

    public IReadOnlyList<string> Ignoring { get; private set; } = [];
    public bool HasIgnoring => Ignoring.Count > 0;

    public IReadOnlyList<MoveGroup> Moves { get; private set; } = [];
    public string BackupFolder => _plan?.BackupFolder ?? "";

    public string EffectText { get; private set; } = "";
    public string? LockedText { get; private set; }
    public bool HasLockedText => LockedText is not null;
    public string ApplyLabel => (PlannedMerges > 0, PlannedMoves > 0) switch
    {
        (true, false) => $"Combine {Plain.Count(PlannedMerges, "file")}",
        (true, true) => "Make these changes",
        (false, true) => $"Move {Plain.Count(PlannedMoves, "file")}",
        _ => "Ignore them",
    };

    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    public ICommand CancelCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand LeaveAllCommand { get; }
    public ICommand UseFocusForAllCommand { get; }

    private void Next()
    {
        var files = FilesToMove();
        var merges = MergeRequests();
        Ignoring = Groups.Where(g => g.Ignore && !g.Finding.IsFine)
            .Select(g => $"{g.Title}  ·  {g.With.Replace("Also changed by ", "with ")}").ToList();
        if (files.Count == 0 && merges.Count == 0 && !FineChanges.Any()) return;
        try
        {
            _plan = files.Count > 0 || merges.Count > 0
                ? Resolver.Plan(_owner.Result.ResourcesFolder, files, DateTime.Now, merges)
                : null;
        }
        catch (FixException e)
        {
            Error = e.Message;
            return;
        }
        if (_plan is null)
        {
            Moves = [];
            Merges = [];
            EffectText = "";
            LockedText = null;
            RaiseConfirm();
            Error = null;
            IsConfirming = true;
            return;
        }

        Merges = _plan.Merges.Select(m => new MergeLine(m.FileName, $"for {Plain.JoinAndShort(m.Maps)}", KeptText(m.Result))).ToList();
        var byPath = files.ToDictionary(f => f.FullPath, StringComparer.OrdinalIgnoreCase);
        Moves = _plan.Moves
            .Where(m => !m.IsMerge)
            .GroupBy(m => m.ResourceName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var resource = byPath[g.First().From].Resource;
                return new MoveGroup(
                    resource.Name,
                    resource == Focus,
                    resource.IsLocked,
                    Plain.Count(g.Count(), "file"),
                    g.Select(m => new MoveLine(m.FileName, Path.GetRelativePath(resource.Directory, m.From))).ToList());
            })
            .OrderBy(g => g.IsFocus ? 0 : 1).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var losers = Moves.Select(m => m.Name).ToList();
        const string undo = "If anything looks wrong afterwards, press \"Undo fixes\" (at the top of your results, or on the start screen) and every file goes back.";
        EffectText = losers.Count == 0
            ? $"Every map involved gets the combined version, so none of their buildings are hidden. {undo}"
            : losers.Count == 1
            ? $"Around {losers[0]}, players will get the versions you chose instead. If anything there looks wrong " +
              "afterwards, press \"Undo fixes\" (at the top of your results, or on the start screen) and every file goes back."
            : $"Around {Plain.JoinAndShort(losers)}, players will get the versions you chose instead. {undo}";

        var lockedNames = _owner.Result.Resources.Where(r => r.IsLocked).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var locked = Moves.Where(m => m.IsLocked).Select(m => m.Name)
            .Concat(_plan.Merges.SelectMany(m => m.Maps).Where(lockedNames.Contains))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LockedText = locked.Count == 0
            ? null
            : $"{Plain.JoinAndShort(locked)} {(locked.Count == 1 ? "is" : "are")} locked with Escrow. We never open locked files. " +
              "We only move map files, or swap in a combined occlusion file, which Escrow doesn't lock. That normally works. " +
              "If the map stops starting, press Undo.";

        RaiseConfirm();
        Error = null;
        IsConfirming = true;
    }

    private void RaiseConfirm()
    {
        foreach (var name in new[] { nameof(Moves), nameof(Merges), nameof(HasMerges), nameof(HasMovedOut), nameof(BackupFolder),
                     nameof(EffectText), nameof(LockedText), nameof(HasLockedText), nameof(ConfirmTitle), nameof(ConfirmText),
                     nameof(ApplyLabel), nameof(HasMoves), nameof(Ignoring), nameof(HasIgnoring) })
            OnPropertyChanged(name);
    }

    internal static string KeptText(MergedFile m) =>
        m.Total == 0 || m.Kept == m.Total
            ? "Nothing lost: every map already agreed here"
            : m.Kept == 0
                ? $"These maps' versions have nothing in common, so none of the {m.Total} hiding spots are kept. Nothing vanishes, but frame rate nearby may dip slightly"
            : m.Kept * 2 >= m.Total
                ? $"Keeps {m.Kept} of {m.Total} hiding spots, so GTA draws a tiny bit more here"
                : $"The maps disagree a lot here, so only {m.Kept} of {m.Total} hiding spots are kept. Nothing vanishes, but frame rate nearby may dip slightly";

    private void Apply()
    {
        FixRecord? record = null;
        if (_plan is not null)
        {
            try
            {
                record = Resolver.Apply(_plan, DateTime.Now);
            }
            catch (FixException e)
            {
                Error = e.Message;
                return;
            }
        }

        // Ignoring is the same as "Mark as fine" on the results page, and can be undone there.
        foreach (var group in FineChanges.ToList()) group.Finding.IsFine = group.Ignore;

        if (record is not null) _owner.AfterFix(record);
        else _owner.CloseDialog();
    }
}

// One clash, asked as one question: which map should show here? Picking the map showing now moves nothing.
public sealed class ResolveGroup : ObservableObject
{
    private readonly ResolveViewModel _dialog;
    private readonly ResourceInfo? _focus;
    private readonly string _symptom;
    private bool _showFiles;
    private bool _ignore;

    public ResolveGroup(FindingViewModel finding, ResourceInfo? focus, ScanResult result, ResolveViewModel dialog)
    {
        _dialog = dialog;
        _focus = focus;
        Finding = finding;
        var f = finding.Finding;
        Winner = f.Winner ?? f.Resources[^1];
        Resources = f.Resources
            .OrderBy(r => r == focus ? 0 : r == Winner ? 1 : 2)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Title = f.Title;
        DotBrush = finding.DotBrush;
        _symptom = SeverityStyle.Symptom(f.ShortTitle) ?? "problems";
        var others = Resources.Where(r => r != focus).Select(r => r.Name).ToList();
        With = focus is null
            ? $"Between {Plain.JoinAndShort(Resources.Select(r => r.Name).ToList())}"
            : $"Also changed by {Plain.JoinAndShort(others)}";

        Options = Resources.Select(r => new GroupOption(this, r, r == focus, r == Winner)).ToList();

        // Out of the box: when fixing "your" map, show it wherever it's hidden right now.
        // Anything already working for that map, or marked as fine, is left as it is.
        // Where files can be combined, that's the best answer (every map works), so it's picked to start with.
        var useFocus = focus is not null && Winner != focus && !finding.IsFine;
        Files = f.Files.Select(file => new ResolveFile(file, Resources, Winner, focus, this, useFocus ? focus : null,
            merge: !finding.IsFine)).ToList();
        if (CanMerge && !finding.IsFine)
            foreach (var file in Files.Where(x => !x.CanMerge)) file.StartLeft();

        // Already marked as fine: start on "Ignore", so nothing changes unless you choose otherwise.
        _ignore = finding.IsFine;
    }

    public FindingViewModel Finding { get; }

    // Nothing moves, and the clash is marked as fine so it stops counting as a problem.
    public bool Ignore
    {
        get => _ignore;
        set { if (value) SetIgnore(); }
    }

    private void SetIgnore()
    {
        if (_ignore) return;
        foreach (var file in Files) file.Choose(null);
        _ignore = true;
        ChoiceChanged();
    }

    public ResourceInfo Winner { get; }
    public IReadOnlyList<ResourceInfo> Resources { get; }
    public IReadOnlyList<GroupOption> Options { get; }
    public IReadOnlyList<ResolveFile> Files { get; }
    public string Title { get; }
    public System.Windows.Media.Brush DotBrush { get; }
    public string With { get; }

    public string FilesLabel => ShowFiles
        ? "Hide the files"
        : Files.Count == 1 ? "Show the file" : $"Show the {Files.Count} files (choose one by one)";

    public bool ShowFiles
    {
        get => _showFiles;
        set { if (Set(ref _showFiles, value)) OnPropertyChanged(nameof(FilesLabel)); }
    }

    // The map every file agrees on, or null when files were chosen one by one differently.
    public GroupOption? Selected
    {
        get
        {
            if (_ignore || Files.Any(f => f.Choice.IsMerge)) return null;
            var picks = Files.Select(f => f.Choice.Resource ?? Winner).Distinct().ToList();
            return picks.Count == 1 ? Options.First(o => o.Resource == picks[0]) : null;
        }
    }

    public bool IsMixed => !_ignore && !Merge && Selected is null;
    public bool IsChanged => Files.Any(f => f.IsChanged);

    public bool CanMerge => Files.Any(f => f.CanMerge);
    public bool AllCanMerge => Files.All(f => f.CanMerge);
    public string MergeLabel => Resources.Count == 2 ? "Combine them" : $"Combine all {Resources.Count}";
    public string MergeTag => AllCanMerge ? "every map works" : "where possible";

    // Combines every file that can be, and leaves the rest as they are.
    public bool Merge
    {
        get => !_ignore && CanMerge && Files.All(f => f.CanMerge ? f.Choice.IsMerge : !f.IsChanged);
        set
        {
            if (!value) return;
            _ignore = false;
            foreach (var file in Files)
                if (file.CanMerge) file.ChooseMerge();
                else file.Choose(null);
            ChoiceChanged();
        }
    }

    public string Effect
    {
        get
        {
            var moving = Files.SelectMany(f => f.FilesToMove()).ToList();
            var merging = Files.Where(f => f.Choice.IsMerge).ToList();
            if (_ignore)
                return "Nothing moves. This clash is marked as fine, so it stops showing as a problem. You can unmark it any time.";
            if (merging.Count > 0)
            {
                var all = Resources.Count == 2 ? "both maps" : $"all {Resources.Count} maps";
                var text = $"Combines {(merging.Count == 1 ? merging[0].FileName : Plain.Count(merging.Count, "file"))} so {all} work: " +
                           "only the hiding spots every map agrees on are kept, so none of their buildings vanish. The originals go to the backup folder.";
                if (moving.Count > 0)
                    text += $" Also moves {Plain.Count(moving.Count, "file")} out of {Plain.JoinAndShort(moving.Select(m => m.Resource.Name).Distinct().ToList())}.";
                var left = Files.Count(f => !f.IsChanged);
                if (left > 0) text += $" {Plain.Count(left, "other file")} can't be combined and {(left == 1 ? "stays" : "stay")} as {(left == 1 ? "it is" : "they are")}.";
                return text;
            }
            if (moving.Count == 0)
            {
                if (_focus is not null && Winner == _focus) return $"Nothing moves. {_focus.Name} already shows here.";
                return $"Nothing moves. {Winner.Name} keeps showing here.";
            }
            var from = moving.Select(m => m.Resource.Name).Distinct().ToList();
            var start = IsMixed ? "Chosen file by file: moves " : "Moves ";
            return $"{start}{Plain.Count(moving.Count, "file")} out of {Plain.JoinAndShort(from)} into the backup folder.";
        }
    }

    public string TradeOff
    {
        get
        {
            var moving = Files.SelectMany(f => f.FilesToMove()).Select(m => m.Resource.Name).Distinct().ToList();
            var merged = Files.Where(f => f.Choice.IsMerge && f.Preview is not null).Select(f => f.Preview!).ToList();
            if (merged.Count > 0 && moving.Count == 0)
            {
                var kept = merged.Sum(m => m.Kept);
                var total = merged.Sum(m => m.Total);
                return kept * 2 >= total
                    ? "Trade-off: hardly any. GTA may draw a tiny bit more around here."
                    : "Trade-off: these maps disagree a lot here, so GTA will draw more around them. Nothing vanishes, but frame rate nearby may dip slightly.";
            }
            if (moving.Count == 0)
            {
                if (_focus is not null && Winner != _focus)
                    return $"Players may still notice {_symptom} around {_focus.Name}.";
                return "";
            }
            return $"Trade-off: players may notice {_symptom} around {Plain.JoinAndShort(moving)} instead.";
        }
    }

    public bool HasTradeOff => TradeOff.Length > 0;

    public void ChooseAll(ResourceInfo? resource)
    {
        _ignore = false;
        foreach (var file in Files) file.Choose(resource);
        ChoiceChanged();
    }

    // Picking a file one by one means you're not ignoring the clash any more.
    internal void FileChosen()
    {
        _ignore = false;
        ChoiceChanged();
    }

    internal void ChoiceChanged()
    {
        foreach (var o in Options) o.Changed();
        foreach (var name in new[] { nameof(Selected), nameof(IsMixed), nameof(IsChanged), nameof(Effect), nameof(TradeOff), nameof(HasTradeOff), nameof(Ignore), nameof(Merge) })
            OnPropertyChanged(name);
        _dialog.ChoiceChanged();
    }
}

public sealed class GroupOption(ResolveGroup group, ResourceInfo resource, bool isFocus, bool isShowing) : ObservableObject
{
    public ResourceInfo Resource { get; } = resource;
    public string Name => Resource.Name;
    public bool IsFocus { get; } = isFocus;
    public bool IsShowing { get; } = isShowing;
    public string? Tag => IsFocus && IsShowing ? "this map · showing now" : IsFocus ? "this map" : IsShowing ? "showing now" : null;
    public bool HasTag => Tag is not null;

    public bool IsSelected
    {
        get => group.Selected == this;
        set { if (value) group.ChooseAll(Resource); }
    }

    internal void Changed() => OnPropertyChanged(nameof(IsSelected));
}

public sealed class ResolveFile : ObservableObject
{
    private readonly ResolveGroup _group;
    private readonly ResourceInfo _winner;
    private ResolveOption _choice;

    public ResolveFile(FileClash file, IReadOnlyList<ResourceInfo> resources, ResourceInfo winner, ResourceInfo? focus,
        ResolveGroup group, ResourceInfo? initial, bool merge)
    {
        _group = group;
        _winner = winner;
        FileName = file.FileName;
        Meaning = file.Meaning;

        // Every copy of this file in every map involved, including a second copy in the same map, so nothing is missed.
        AllCopies = resources
            .SelectMany(r => r.StreamFiles.Where(s => s.FileName.Equals(file.FileName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // One pill per map. The one showing now stands for "leave it", so it moves nothing.
        Options = resources
            .Select(r => new ResolveOption(this, r == winner ? null : r,
                r.Name + (r == winner ? " (showing now)" : r == focus ? " (this map)" : "")))
            .ToList();
        // Can the copies be combined so every map works? Try it now (in memory only) so we know for sure.
        Base = AllCopies.FirstOrDefault(c => c.Resource == winner)
               ?? AllCopies.OrderByDescending(c => c.Resource.StartOrder ?? -1).First();
        if (OcclusionMerge.CanMerge(AllCopies))
        {
            try
            {
                Preview = OcclusionMerge.Merge(Base.FullPath, AllCopies.Where(c => c != Base).Select(c => c.FullPath).ToList());
                var maps = AllCopies.Select(c => c.Resource).Distinct().Count();
                Options = Options.Prepend(new ResolveOption(this, null, maps == 2 ? "Combine both" : $"Combine all {maps}", isMerge: true)).ToList();
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Can't be combined after all, so there's just no Combine option.
            }
        }

        _choice = (merge && CanMerge ? Options.First(o => o.IsMerge) : null)
                  ?? Options.FirstOrDefault(o => o.Resource is not null && o.Resource == initial)
                  ?? Leave;
    }

    // The copy that loads now: the starting point for a combined version.
    public StreamFile Base { get; }

    // What combining would keep, worked out in memory (null if it can't be combined).
    public MergedFile? Preview { get; }
    public bool CanMerge => Preview is not null;

    private ResolveOption Leave => Options.First(o => o.Resource is null && !o.IsMerge);

    public bool IsChanged => _choice.Resource is not null || _choice.IsMerge;

    public MergeRequest? MergeRequest => _choice.IsMerge ? new MergeRequest(Base, AllCopies) : null;

    // Starts on "leave it" without raising any change (only while the dialog is being built).
    internal void StartLeft() => _choice = Leave;

    public void ChooseMerge()
    {
        if (Options.FirstOrDefault(o => o.IsMerge) is { } option) Select(option);
    }

    public string FileName { get; }
    public string Meaning { get; }
    public IReadOnlyList<StreamFile> AllCopies { get; }
    public IReadOnlyList<ResolveOption> Options { get; }
    public ResolveOption Choice => _choice;
    public string KeptText => Preview is { } p ? ResolveViewModel.KeptText(p) : "";

    // Choosing the map that's showing now is the same as leaving the file alone.
    public void Choose(ResourceInfo? resource) =>
        Select(Options.FirstOrDefault(o => o.Resource is not null && o.Resource == resource && resource != _winner)
               ?? Leave);

    public void Select(ResolveOption option)
    {
        if (_choice == option) return;
        var old = _choice;
        _choice = option;
        old.Changed();
        option.Changed();
        OnPropertyChanged(nameof(Choice));
        _group.FileChosen();
    }

    public IEnumerable<StreamFile> FilesToMove() =>
        _choice.Resource is { } keep ? AllCopies.Where(c => c.Resource != keep) : [];
}

// A null resource means "leave it" (the map showing now), or Combine when IsMerge is set.
public sealed class ResolveOption(ResolveFile file, ResourceInfo? resource, string label, bool isMerge = false) : ObservableObject
{
    public ResourceInfo? Resource { get; } = resource;
    public string Label { get; } = label;
    public bool IsMerge { get; } = isMerge;

    public bool IsSelected
    {
        get => file.Choice == this;
        set { if (value) file.Select(this); }
    }

    internal void Changed() => OnPropertyChanged(nameof(IsSelected));
}

public sealed record MoveGroup(string Name, bool IsFocus, bool IsLocked, string Count, IReadOnlyList<MoveLine> Files)
{
    public string FileList => string.Join(", ", Files.Select(f => f.FileName));
    public string Paths => string.Join(Environment.NewLine, Files.Select(f => f.Path));
}
public sealed record MoveLine(string FileName, string Path);
public sealed record MergeLine(string FileName, string Maps, string Kept);
public sealed record PastFileLine(string FileName, string MapName);

public sealed class FixNotice : ObservableObject
{
    private string? _error;

    private FixNotice(string title, string text, FixRecord? record, MainViewModel main)
    {
        Title = title;
        Text = text;
        Record = record;
        if (record is not null)
        {
            UndoCommand = new RelayCommand(() => Undo(main, record));
            OpenBackupCommand = new RelayCommand(() => Shell.ShowFolder(record.BackupFolder));
        }
    }

    public static FixNotice Fixed(MainViewModel main, FixRecord record) => new(
        FixedTitle(record),
        "Restart your server (or the maps involved) so players get the change. Changed your mind? Undo puts every file back.",
        record, main);

    private static string FixedTitle(FixRecord record)
    {
        var combined = record.Moves.Where(m => m.IsMerge).Select(m => m.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var moved = record.Moves.Count(m => !m.IsMerge);
        return (combined > 0, moved > 0) switch
        {
            (true, false) => $"Combined {Plain.Count(combined, "file")} so every map works",
            (true, true) => $"Combined {Plain.Count(combined, "file")} and moved {Plain.Count(moved, "file")} to a backup",
            _ => $"Moved {Plain.Count(moved, "file")} to a backup",
        };
    }

    public static FixNotice Undone(MainViewModel main, FixRecord record) => new(
        $"Put {Plain.Count(record.Moves.Count, "file")} back",
        "Everything is back the way it was. Restart your server so players get the change.",
        null, main);

    public static FixNotice UndoneAll(int count, IReadOnlyList<string> problems) => new(
        $"Undid {Plain.Count(count, "fix", "fixes")}",
        problems.Count == 0
            ? "Every file Map Doctor moved is back where it was. Restart your server so players get the change."
            : $"Restart your server so players get the change. {Plain.Count(problems.Count, "fix", "fixes")} couldn't be undone: {problems[0]}",
        null, null!);

    public string Title { get; }
    public string Text { get; }
    public FixRecord? Record { get; }
    public bool CanUndo => Record is not null;
    public ICommand? UndoCommand { get; }
    public ICommand? OpenBackupCommand { get; }

    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => Error is not null;

    private void Undo(MainViewModel main, FixRecord record)
    {
        if (main.Page is ResultsViewModel results) Error = results.UndoFix(record);
    }
}

// Every fix still in the backup folder, including ones made before the app was last closed.
public sealed class PastFixesViewModel : ObservableObject
{
    private string? _error;

    public PastFixesViewModel(ResultsViewModel owner, IReadOnlyList<FixRecord> records)
    {
        Items = records.Select(r => new PastFix(r, owner)).ToList();
        CloseCommand = new RelayCommand(owner.CloseDialog);
        UndoAllCommand = new RelayCommand(() => Error = owner.UndoAllFixes(), () => CanUndoAll);
    }

    public IReadOnlyList<PastFix> Items { get; }
    public string Title => "Undo fixes";
    public string Subtitle =>
        "Does something not look right after a fix? Press Undo and every file from that fix goes back exactly where it was, " +
        "as if the fix never happened. Restart your server afterwards so players get the change.";
    public string Empty => "Nothing to undo. Map Doctor hasn't changed anything on this server.";
    public bool IsEmpty => Items.Count == 0;

    public int UndoableCount => Items.Count(i => i.CanUndo);
    public bool CanUndoAll => UndoableCount > 1;
    public string UndoAllLabel => $"Undo all ({UndoableCount})";
    public string UndoAllTip => "Put back everything Map Doctor has moved on this server, newest first";
    public ICommand UndoAllCommand { get; }
    public ICommand CloseCommand { get; }

    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => Error is not null;
}

public sealed class PastFix : ObservableObject
{
    private const int MaxFiles = 4;
    private string? _error;

    public PastFix(FixRecord record, ResultsViewModel owner)
    {
        var day = record.MadeAt.Date == DateTime.Today ? "Today"
            : record.MadeAt.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
            : record.MadeAt.ToString("d MMM yyyy");
        When = $"{day} at {record.MadeAt:HH:mm}";

        var maps = record.Moves.Select(m => m.ResourceName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = record.Moves.Select(m => m.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var combined = record.Moves.Where(m => m.IsMerge).Select(m => m.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var what = names.Count == 1 && record.Moves.Count > 1
            ? $"{record.Moves.Count} copies of {names[0]}"
            : Plain.Count(record.Moves.Count, "file");
        Summary = combined.Count == names.Count
            ? $"Combined {(combined.Count == 1 ? combined[0] : Plain.Count(combined.Count, "file"))} for {Plain.JoinAndShort(maps)}"
            : combined.Count > 0
                ? $"Combined {Plain.Count(combined.Count, "file")} and moved others out of {Plain.JoinAndShort(maps)}"
                : $"Moved {what} out of {Plain.JoinAndShort(maps)}";
        Files = record.Moves.Take(MaxFiles).Select(m => new PastFileLine(m.FileName, m.ResourceName)).ToList();
        MoreFiles = record.Moves.Count > MaxFiles ? $"+ {Plain.Count(record.Moves.Count - MaxFiles, "more file")}" : null;

        Problem = Resolver.WhyCantUndo(record);
        UndoCommand = new RelayCommand(() => Error = owner.UndoFix(record), () => CanUndo);
        ForgetCommand = new RelayCommand(() => Error = owner.ForgetFix(record));
        OpenFolderCommand = new RelayCommand(() => Shell.ShowFolder(record.BackupFolder));
    }

    public string When { get; }
    public string Summary { get; }
    public IReadOnlyList<PastFileLine> Files { get; }
    public string? MoreFiles { get; }
    public bool HasMoreFiles => MoreFiles is not null;
    public string? Problem { get; }
    public bool HasProblem => Problem is not null;
    public bool CanUndo => Problem is null;

    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => Error is not null;
    public ICommand UndoCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ForgetCommand { get; }
    public string ForgetTip => "Stop showing this fix here. The backed-up files stay in the backup folder.";
}
