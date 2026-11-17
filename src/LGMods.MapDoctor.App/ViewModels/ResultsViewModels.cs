using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LGMods.MapDoctor.App.Services;
using LGMods.MapDoctor.Core.Model;
using LGMods.MapDoctor.Core.Report;
using LGMods.MapDoctor.Core.Text;
using Microsoft.Win32;

namespace LGMods.MapDoctor.App.ViewModels;

public enum ResultsFilter { Problems, Warnings, All }

// Opens on "By map" showing the maps that need fixing, because most people want to know "is my map OK?" first.
public sealed class ResultsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Settings _settings;
    private FixNotice? _notice;
    private ResultsFilter _filter = ResultsFilter.Problems;
    private string _search = "";
    private bool _isMapView = true;
    private FindingViewModel? _selected;
    private MapViewModel? _selectedMap;

    public ResultsViewModel(MainViewModel main, Settings settings, ScanResult result, string folder)
    {
        _main = main;
        _settings = settings;
        Result = result;
        Folder = folder;

        Findings = result.Findings
            .Select((f, i) => new FindingViewModel(f, i, this, settings.IsFine(folder, f.Key)))
            .ToList();
        Maps = BuildMaps();

        FindingsView = new ListCollectionView(Findings.ToList()) { Filter = o => ShowFinding((FindingViewModel)o) };
        FindingsView.SortDescriptions.Add(new SortDescription(nameof(FindingViewModel.GroupOrder), ListSortDirection.Ascending));
        FindingsView.SortDescriptions.Add(new SortDescription(nameof(FindingViewModel.Order), ListSortDirection.Ascending));
        FindingsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FindingViewModel.Group)));
        FindingsView.IsLiveSorting = true;
        FindingsView.IsLiveGrouping = true;
        FindingsView.LiveSortingProperties.Add(nameof(FindingViewModel.GroupOrder));
        FindingsView.LiveGroupingProperties.Add(nameof(FindingViewModel.Group));

        // Maps arrive already sorted by status, so groups appear in the right order.
        MapsView = new ListCollectionView(Maps.ToList()) { Filter = o => ShowMap((MapViewModel)o) };
        MapsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MapViewModel.Group)));

        ScanAgainCommand = new RelayCommand(() => _ = main.ScanAsync(folder));
        SaveReportCommand = new RelayCommand(SaveReport);
        ClearSearchCommand = new RelayCommand(() => Search = "");
        ShowEverythingCommand = new RelayCommand(() => SetFilter(ResultsFilter.All));
        DismissNoticeCommand = new RelayCommand(() => Notice = null);
        PastFixes = LoadPastFixes(result.ResourcesFolder);
        OpenPastFixesCommand = new RelayCommand(OpenPastFixes);

        _selected = FindingsView.Cast<FindingViewModel>().FirstOrDefault();
        _selectedMap = MapsView.Cast<MapViewModel>().FirstOrDefault();
    }

    public ScanResult Result { get; }
    public string Folder { get; }
    public IReadOnlyList<FindingViewModel> Findings { get; }
    public IReadOnlyList<MapViewModel> Maps { get; }
    public ListCollectionView FindingsView { get; }
    public ListCollectionView MapsView { get; }

    public int ProblemCount => Findings.Count(f => f.Severity == Severity.Problem && !f.IsFine);
    public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning && !f.IsFine);
    public int MapProblemCount => Maps.Count(m => m.Status == Severity.Problem);
    public int MapWarningCount => Maps.Count(m => m.Status == Severity.Warning);

    // The headline only counts real maps (started, with world files), not scripts or ignored duplicate folders,
    // so its total matches what the server actually runs. Everything else is mentioned underneath.
    private int RealMapCount => Maps.Count(m => m.IsRealMap);
    private int RealMapProblemCount => Maps.Count(m => m.IsRealMap && m.Status == Severity.Problem);
    private int OtherProblemCount => Maps.Count(m => !m.IsRealMap && m.Status == Severity.Problem);

    public string HeadlineNumber => RealMapProblemCount == 0 ? "None" : RealMapProblemCount.ToString("N0");
    public string HeadlineRest => RealMapCount <= 1
        ? (RealMapProblemCount == 1 ? " map on your server needs fixing" : " of your maps need fixing")
        : $" of your {RealMapCount:N0} maps {(RealMapProblemCount == 1 ? "needs" : "need")} fixing";
    public Brush HeadlineBrush => Res(MapProblemCount == 0 ? "TealBrush" : "ProblemBrush");

    public string Subline
    {
        get
        {
            var check = MapWarningCount switch
            {
                0 => "Nothing else needs checking.",
                1 => "1 more is worth a quick check.",
                var n => $"{n} more are worth a quick check.",
            };
            var others = OtherProblemCount switch
            {
                0 => "",
                1 => "1 other resource needs fixing too.  ",
                var n => $"{n} other resources need fixing too.  ",
            };
            return $"{others}{check}  Checked {Result.StreamFileCount:N0} game files at {Result.ScannedAt:HH:mm}.";
        }
    }

    public bool ShowProblems { get => _filter == ResultsFilter.Problems; set { if (value) SetFilter(ResultsFilter.Problems); } }
    public bool ShowWarnings { get => _filter == ResultsFilter.Warnings; set { if (value) SetFilter(ResultsFilter.Warnings); } }
    public bool ShowAll { get => _filter == ResultsFilter.All; set { if (value) SetFilter(ResultsFilter.All); } }

    public int AllCount => Findings.Count;

    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value ?? "")) return;
            Refresh();
        }
    }

    public bool IsMapView
    {
        get => _isMapView;
        set
        {
            if (!Set(ref _isMapView, value)) return;
            RaiseAll(nameof(IsClashView), nameof(Detail), nameof(SearchHint));
            RaiseEmptyState();
        }
    }

    public bool IsClashView { get => !_isMapView; set => IsMapView = !value; }

    public string SearchHint => IsMapView ? "Search for a map" : "Search maps or files";

    public FindingViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(Detail)); }
    }

    public MapViewModel? SelectedMap
    {
        get => _selectedMap;
        set { if (Set(ref _selectedMap, value)) OnPropertyChanged(nameof(Detail)); }
    }

    public object? Detail => IsMapView ? SelectedMap : Selected;

    public bool ListIsEmpty => IsMapView ? MapsView.IsEmpty : FindingsView.IsEmpty;
    public bool CanShowEverything => IsClashView && ListIsEmpty && _filter != ResultsFilter.All && HasMatchesAnywhere;

    public string EmptyListText
    {
        get
        {
            var q = Search.Trim();
            if (IsMapView) return q.Length > 0 ? $"Nothing matches \"{q}\"." : "No maps found.";
            var what = _filter switch
            {
                ResultsFilter.Problems => "clashes to fix",
                ResultsFilter.Warnings => "things worth checking",
                _ => "results",
            };
            if (q.Length > 0)
                return _filter != ResultsFilter.All && HasMatchesAnywhere
                    ? $"None of the {what} match \"{q}\"."
                    : $"Nothing matches \"{q}\".";
            return $"No {what}.";
        }
    }

    private bool HasMatchesAnywhere
    {
        get
        {
            var q = Search.Trim();
            return IsMapView ? Maps.Any(m => m.Matches(q)) : Findings.Any(f => f.Matches(q));
        }
    }

    public ICommand ScanAgainCommand { get; }
    public ICommand SaveReportCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ShowEverythingCommand { get; }
    public ICommand DismissNoticeCommand { get; }

    public FixNotice? Notice
    {
        get => _notice;
        set { if (Set(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); }
    }

    public bool HasNotice => Notice is not null;

    public void OpenResolve(IReadOnlyList<FindingViewModel> clashes, ResourceInfo? focus)
    {
        if (clashes.Count == 0) return;
        _main.Dialog = new ResolveViewModel(this, clashes, focus);
    }

    public void CloseDialog() => _main.Dialog = null;

    public IReadOnlyList<Core.Fixing.FixRecord> PastFixes { get; private set; }
    public bool HasPastFixes => PastFixes.Count > 0;
    public string PastFixesTip => $"Something not right? Put the files back from {Plain.Count(PastFixes.Count, "earlier fix", "earlier fixes")}";
    public ICommand OpenPastFixesCommand { get; }

    // UndoFix, UndoAllFixes and ForgetFix return an error to show, or null.
    public string? UndoFix(Core.Fixing.FixRecord record)
    {
        try
        {
            Core.Fixing.Resolver.Undo(record);
        }
        catch (Core.Fixing.FixException e)
        {
            return e.Message;
        }
        _ = _main.RescanAsync(Folder, CurrentView(), FixNotice.Undone(_main, record));
        return null;
    }

    public string? UndoAllFixes()
    {
        var (undone, problems) = Core.Fixing.Resolver.UndoAll(Result.ResourcesFolder);
        if (undone == 0) return problems.FirstOrDefault() ?? "There was nothing to undo.";
        _ = _main.RescanAsync(Folder, CurrentView(), FixNotice.UndoneAll(undone, problems));
        return null;
    }

    public void OpenPastFixes() => _main.Dialog = new PastFixesViewModel(this, PastFixes);

    // Stops listing a fix that can't be undone. Its backup is kept.
    public string? ForgetFix(Core.Fixing.FixRecord record)
    {
        try
        {
            Core.Fixing.Resolver.Forget(record);
        }
        catch (Core.Fixing.FixException e)
        {
            return e.Message;
        }
        PastFixes = LoadPastFixes(Result.ResourcesFolder);
        RaiseAll(nameof(PastFixes), nameof(HasPastFixes), nameof(PastFixesTip));
        OpenPastFixes();
        return null;
    }

    public void AfterFix(Core.Fixing.FixRecord record, bool needsStartLine) =>
        _ = _main.RescanAsync(Folder, CurrentView(), FixNotice.Fixed(_main, record, needsStartLine));

    public ViewState CurrentView() => new(IsMapView, SelectedMap?.Name, Selected?.Finding.Key);

    public void Restore(ViewState view)
    {
        if (view.IsMapView)
        {
            if (Maps.FirstOrDefault(m => m.Name.Equals(view.MapName, StringComparison.OrdinalIgnoreCase)) is { } map)
                GoToMap(map.Resource);
        }
        else if (Findings.FirstOrDefault(f => f.Finding.Key == view.FindingKey) is { } finding)
        {
            GoToFinding(finding);
        }
        else
        {
            IsMapView = false;
        }
    }

    public void GoToMap(ResourceInfo resource)
    {
        var map = Maps.FirstOrDefault(m => m.Resource == resource);
        if (map is null) return;
        IsMapView = true;
        Search = "";
        SelectedMap = map;
    }

    public void GoToFinding(FindingViewModel finding)
    {
        IsMapView = false;
        Search = "";
        if (!PassesFilter(finding.IsFine ? null : finding.Severity)) SetFilter(ResultsFilter.All);
        Selected = finding;
    }

    public void SetFine(FindingViewModel finding, bool fine)
    {
        _settings.SetFine(Folder, finding.Finding.Key, fine);
        foreach (var map in Maps) map.Refresh();
        RaiseAll(nameof(ProblemCount), nameof(WarningCount), nameof(MapProblemCount), nameof(MapWarningCount),
            nameof(HeadlineNumber), nameof(HeadlineRest), nameof(HeadlineBrush), nameof(Subline));
    }

    private void SetFilter(ResultsFilter filter)
    {
        if (_filter == filter) return;
        _filter = filter;
        RaiseAll(nameof(ShowAll), nameof(ShowProblems), nameof(ShowWarnings));
        Refresh();
    }

    private void Refresh()
    {
        FindingsView.Refresh();
        MapsView.Refresh();
        RaiseEmptyState();

        // Keep something selected so the right-hand side is never blank while there are results.
        var visibleFindings = FindingsView.Cast<FindingViewModel>().ToList();
        if (Selected is null || !visibleFindings.Contains(Selected))
            Selected = visibleFindings.FirstOrDefault();
        var visibleMaps = MapsView.Cast<MapViewModel>().ToList();
        if (SelectedMap is null || !visibleMaps.Contains(SelectedMap))
            SelectedMap = visibleMaps.FirstOrDefault();
    }

    private void RaiseEmptyState() =>
        RaiseAll(nameof(ListIsEmpty), nameof(EmptyListText), nameof(CanShowEverything));

    private void RaiseAll(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
    }

    private bool PassesFilter(Severity? status) => _filter switch
    {
        ResultsFilter.Problems => status == Severity.Problem,
        ResultsFilter.Warnings => status == Severity.Warning,
        _ => true,
    };

    private bool ShowFinding(FindingViewModel f) =>
        PassesFilter(f.IsFine ? null : f.Severity) && f.Matches(Search.Trim());

    // Every map is always listed, grouped by how it's doing, with LG Mods maps at the top so buyers find theirs
    // straight away. Only searching narrows it down.
    private bool ShowMap(MapViewModel m) => m.Matches(Search.Trim());

    private List<MapViewModel> BuildMaps()
    {
        var involved = Findings.SelectMany(f => f.Finding.Resources);
        return Result.Resources.Where(r => r.IsStarted && r.ChangesTheWorld)
            .Concat(involved)
            .Concat(Result.Resources.Where(r => MapViewModel.IsLgModsName(r.Name) && r.UsedInstead is null && r.StreamFiles.Count > 0))
            .Distinct()
            .Select(r => new MapViewModel(r, Findings.Where(f => f.Finding.Resources.Contains(r)).ToList(), this))
            .OrderBy(m => m.GroupOrder)
            .ThenByDescending(m => m.SortWeight)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void SaveReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save your Map Doctor report",
            FileName = $"Map Doctor report - {Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder))} - {Result.ScannedAt:yyyy-MM-dd}.html",
            Filter = "Web page (*.html)|*.html",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, HtmlReport.Build(Result, Shell.AppVersion));
            Shell.Open(dialog.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"We couldn't save the report there: {e.Message}", "LGMods Map Doctor");
        }
    }

    private static IReadOnlyList<Core.Fixing.FixRecord> LoadPastFixes(string resourcesFolder)
    {
        try { return Core.Fixing.Resolver.FindRecords(resourcesFolder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}

public static class SeverityStyle
{
    public static Brush Brush(Severity s) => ResultsViewModel.Res(s switch
    {
        Severity.Problem => "ProblemBrush",
        Severity.Warning => "WarningBrush",
        _ => "InfoBrush",
    });

    public static string Label(Severity s) => s switch
    {
        Severity.Problem => "To fix",
        Severity.Warning => "Worth checking",
        _ => "For info",
    };

    // Phrased to follow "Players may notice …".
    public static string? Symptom(string shortTitle) => shortTitle switch
    {
        "Buildings vanishing" => "buildings vanishing",
        "Falling through floors" => "people falling through floors",
        "Missing or misplaced objects" => "missing or misplaced objects",
        "Objects not appearing" => "objects not appearing",
        "Models look wrong" => "odd-looking models",
        "Textures look wrong" => "odd-looking textures",
        "Locals and traffic" => "locals and traffic getting stuck",
        "Distant lights" => "missing distant lights",
        "Grass through floors" => "grass poking through floors",
        _ => null,
    };
}

public sealed class FindingViewModel : ObservableObject
{
    private readonly ResultsViewModel _owner;
    private bool _isFine;
    private bool _showLocations;
    private string? _feedback;
    private DispatcherTimer? _feedbackTimer;

    public FindingViewModel(Finding finding, int order, ResultsViewModel owner, bool isFine)
    {
        Finding = finding;
        Order = order;
        _owner = owner;
        _isFine = isFine;

        Maps = finding.Resources.Select((r, i) => new MapRow(r, finding.Winner, owner) { IsFirst = i == 0 }).ToList();
        Files = finding.Files.Select(f => new FileRow(f.FileName, f.Meaning)).ToList();
        Steps = finding.WhatToDo.Select((t, i) => new Step($"{i + 1}", t)).ToList();
        LocationsText = string.Join(Environment.NewLine, finding.Details);

        CopyMessageCommand = new RelayCommand(CopyMessage);
        CopySupportCommand = new RelayCommand(CopySupport);
        ToggleFineCommand = new RelayCommand(() => IsFine = !IsFine);
        ResolveCommand = new RelayCommand(() => owner.OpenResolve([this], null));
    }

    public ICommand ResolveCommand { get; }

    public Finding Finding { get; }
    public int Order { get; }
    public Severity Severity => Finding.Severity;
    public bool IsClash => Finding.Kind == FindingKind.ClashingFiles;

    public string RowTitle
    {
        get
        {
            if (!IsClash) return Finding.Title;
            var names = Finding.Resources.Select(r => r.Name).ToList();
            if (names.Count > 3) names = [names[0], names[1], $"{names.Count - 2} more"];
            return string.Join("  ⇄  ", names);
        }
    }

    public string RowSubtitle => IsClash
        ? $"{Finding.ShortTitle}  ·  {Plain.Count(Finding.Files.Count, "file")}"
        : Finding.Where;

    public Brush DotBrush => IsFine ? ResultsViewModel.Res("Muted2Brush") : SeverityStyle.Brush(Severity);
    public double RowOpacity => IsFine ? 0.55 : 1;

    public string Group => IsFine ? "Marked as fine" : SeverityStyle.Label(Severity);
    public int GroupOrder => IsFine ? 9 : (int)Severity;

    public string Title => Finding.Title;

    public string Intro => IsClash
        ? $"{(Finding.Resources.Count == 2 ? "Both maps" : $"All {Finding.Resources.Count} maps")} include " +
          $"{Plain.Count(Finding.Files.Count, "file")} with the same name, and only one version of each can load. " +
          (Finding.Winner is { StartOrder: { } n } w
              ? $"{w.Name} starts last ({MapViewModel.Ordinal(n + 1)} in your start-up list), so its versions are the ones players get."
              : "")
        : Finding.WhatsWrong;

    public string SeverityLabel => IsFine ? "Marked as fine" : SeverityStyle.Label(Severity);
    public Brush SeverityBrush => IsFine ? ResultsViewModel.Res("MutedBrush") : SeverityStyle.Brush(Severity);

    public IReadOnlyList<MapRow> Maps { get; }
    public string MapsLabel => IsClash ? "Maps involved" : "Affected";
    public bool HasMaps => Maps.Count > 0;

    public IReadOnlyList<FileRow> Files { get; }
    public bool HasFiles => Files.Count > 0;

    public IReadOnlyList<Step> Steps { get; }

    public string LocationsText { get; }
    public bool HasLocations => LocationsText.Length > 0;
    public string LocationsLabel => IsClash ? "Where the files are" : "Technical details";
    public bool ShowLocations { get => _showLocations; set => Set(ref _showLocations, value); }

    public bool CanMarkFine => Severity != Severity.Info || IsFine;
    public string FineLabel => IsFine ? "Unmark as fine" : "Mark as fine";
    public string FineHint => IsFine
        ? "Count this as a problem again"
        : "Checked it in game and it looks right? This hides it from the to-fix list.";
    public string FineIcon => IsFine ? "" : "";

    public bool IsFine
    {
        get => _isFine;
        set
        {
            if (!Set(ref _isFine, value)) return;
            _owner.SetFine(this, value);
            foreach (var name in new[] { nameof(Group), nameof(GroupOrder), nameof(DotBrush), nameof(RowOpacity),
                         nameof(SeverityLabel), nameof(SeverityBrush), nameof(FineLabel), nameof(FineHint), nameof(FineIcon), nameof(CanMarkFine) })
                OnPropertyChanged(name);
        }
    }

    public string? Feedback { get => _feedback; private set => Set(ref _feedback, value); }

    public ICommand CopyMessageCommand { get; }
    public ICommand CopySupportCommand { get; }
    public ICommand ToggleFineCommand { get; }

    public bool Matches(string query)
    {
        if (query.Length == 0) return true;
        bool Has(string s) => s.Contains(query, StringComparison.OrdinalIgnoreCase);
        return Has(Finding.Title) || Has(Finding.ShortTitle)
            || Finding.Resources.Any(r => Has(r.Name))
            || Finding.Files.Any(f => Has(f.FileName));
    }

    public string MapMakerMessage()
    {
        var sb = new StringBuilder();
        var names = Finding.Resources.Select(r => r.Name).ToList();
        var winner = Finding.Winner?.Name;
        sb.AppendLine($"Hi! I'm running {Plain.JoinAnd(names)} on my FiveM server and they clash. " +
                      "They include files with the same name, so only one version of each can load.");
        sb.AppendLine();
        sb.AppendLine("Clashing files:");
        foreach (var f in Finding.Files) sb.AppendLine($"- {f.FileName} ({f.Meaning.ToLowerInvariant()})");
        sb.AppendLine();
        if (winner is not null)
            sb.AppendLine($"Right now {winner} starts last, so its versions are the ones being used.");
        sb.AppendLine("Do you have a version that works alongside the other map(s)?");
        sb.AppendLine();
        sb.Append("(Found with LGMods Map Doctor)");
        return sb.ToString();
    }

    private void CopyMessage() => Copy(MapMakerMessage(), "Copied. Paste it in a message to the map makers.");

    private void CopySupport() => Copy(
        $"{Finding.Title}\n{Finding.Where}\n\n{Finding.WhatsWrong}\n\n{LocationsText}",
        "Copied. Paste it in a support ticket.");

    private void Copy(string text, string done)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { Feedback = "Couldn't copy. Try again."; return; }

        Feedback = done;
        _feedbackTimer?.Stop();
        _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _feedbackTimer.Tick += (_, _) => { Feedback = null; _feedbackTimer.Stop(); };
        _feedbackTimer.Start();
    }

    public sealed record Step(string Number, string Text);
    public sealed record FileRow(string FileName, string Meaning);

    public sealed class MapRow
    {
        public MapRow(ResourceInfo resource, ResourceInfo? winner, ResultsViewModel owner)
        {
            Resource = resource;
            HasStatus = winner is not null;
            IsWinner = resource == winner;
            OpenFolderCommand = new RelayCommand(() => Shell.ShowFolder(resource.Directory));
            ViewMapCommand = new RelayCommand(() => owner.GoToMap(resource));
        }

        public ResourceInfo Resource { get; }
        public bool IsFirst { get; init; }
        public string Name => Resource.Name;
        public string Subtitle => MapViewModel.Describe(Resource);
        public bool HasStatus { get; }
        public bool IsWinner { get; }
        public string Status => IsWinner ? "In use" : "Ignored";
        public string StatusHint => IsWinner
            ? "Starts later, so its versions of the clashing files are the ones players get."
            : "Starts earlier, so its versions of the clashing files are replaced.";
        public Brush StatusBrush => ResultsViewModel.Res(IsWinner ? "TealBrush" : "ProblemBrush");
        public ICommand OpenFolderCommand { get; }
        public ICommand ViewMapCommand { get; }
    }
}

public sealed class MapViewModel : ObservableObject
{
    private readonly List<FindingViewModel> _findings;

    public MapViewModel(ResourceInfo resource, List<FindingViewModel> findings, ResultsViewModel owner)
    {
        Resource = resource;
        _findings = findings;
        Cards = findings
            .Where(f => f.Severity != Severity.Info || f.Finding.Kind == FindingKind.AlreadyFixed)
            .OrderBy(f => (int)f.Severity).ThenBy(f => f.Order)
            .Select(f => new IssueCard(f, resource, owner))
            .ToList();
        OpenFolderCommand = new RelayCommand(() => Shell.ShowFolder(resource.Directory));
        ResolveCommand = new RelayCommand(() => owner.OpenResolve(
            _findings.Where(f => f.IsClash).OrderBy(f => f.IsFine).ThenBy(f => (int)f.Severity).ThenBy(f => f.Order).ToList(),
            resource));
    }

    // Any clash this map is part of can be resolved from its page, even ones marked as fine.
    public bool CanResolve => _findings.Any(f => f.IsClash);
    public ICommand ResolveCommand { get; }

    public ResourceInfo Resource { get; }
    public string Name => Resource.Name;
    public string FolderPath => Resource.Directory;
    public bool IsLgMods => IsLgModsName(Resource.Name);
    public static bool IsLgModsName(string name) => name.StartsWith("LGMods", StringComparison.OrdinalIgnoreCase)
        && !name.Equals(Core.Fixing.Resolver.FixResourceName, StringComparison.OrdinalIgnoreCase);
    public bool IsLocked => Resource.IsLocked;

    public string CategoryText => Resource.Categories.Count == 0
        ? "resources"
        : string.Join(" / ", Resource.Categories.Select(c => c.Trim('[', ']')));

    public string Meta =>
        $"{CategoryText}  ·  {(Resource.UsedInstead is not null ? "not used (another folder has this name)" : Resource.StartOrder is { } n ? $"starts {Ordinal(n + 1)}" : Resource.WontStart is not null ? "won't start" : "switched off")}" +
        $"  ·  {Resource.StreamFiles.Count:N0} game files";

    public IReadOnlyList<IssueCard> Cards { get; }

    private IEnumerable<FindingViewModel> Open => _findings.Where(f => !f.IsFine && f.Severity != Severity.Info);
    public int ProblemCount => Open.Count(f => f.Severity == Severity.Problem);
    public int WarningCount => Open.Count(f => f.Severity == Severity.Warning);
    public bool HasProblems => ProblemCount > 0;
    public bool HasWarnings => WarningCount > 0;

    public Severity? Status => HasProblems ? Severity.Problem : HasWarnings ? Severity.Warning : null;
    public bool IsClear => Status is null;

    // A map the server really runs: started, with world files, and not an ignored duplicate folder or a fix-up resource.
    public bool IsRealMap => Resource.IsStarted && Resource.ChangesTheWorld && Resource.UsedInstead is null
                             && !Core.Checks.ClashCheck.IsFixResource(Resource);

    public string Group => IsLgMods ? "LG Mods maps"
        : !IsRealMap ? "Other resources (not maps)"
        : Status switch { Severity.Problem => "Needs fixing", Severity.Warning => "Worth checking", _ => "All clear" };
    public int GroupOrder => IsLgMods ? -1 : !IsRealMap ? 6 : Status is { } s ? (int)s : 5;
    public int SortWeight => ProblemCount * 100 + WarningCount;

    public string CountTip => $"{Plain.Count(ProblemCount, "thing")} to fix, {Plain.Count(WarningCount, "thing")} worth checking";

    public string VerdictTitle
    {
        get
        {
            if (HasProblems) return $"{Count(ProblemCount, Severity.Problem)} {(ProblemCount == 1 ? "needs" : "need")} fixing";
            if (HasWarnings) return $"{Count(WarningCount, Severity.Warning)} worth checking";
            if (_findings.Any(f => f.IsFine)) return "Everything here is marked as fine";
            if (Fixer is not null) return "Already sorted";
            return "No clashes";
        }
    }

    public string VerdictText
    {
        get
        {
            if (HasProblems)
            {
                var symptoms = Open.Where(f => f.Severity == Severity.Problem && f.IsClash)
                    .Select(f => SeverityStyle.Symptom(f.Finding.ShortTitle))
                    .OfType<string>().Distinct().ToList();
                var text = symptoms.Count > 0
                    ? $"Players may notice {Plain.JoinAnd(symptoms)} around this map."
                    : "Players are likely to notice problems around this map.";
                return HasWarnings ? $"{text} {Plain.Count(WarningCount, "more thing")} worth checking too." : text;
            }
            if (HasWarnings) return "Probably fine, but worth a quick look in game.";
            if (_findings.Any(f => f.IsFine)) return "You've checked these in game and they look right.";
            if (Fixer is { } fixer) return $"It shares files with other maps, but {fixer} already combines them.";
            return "Nothing else on your server is fighting with this map.";
        }
    }

    public Brush VerdictBrush => Status is { } s ? SeverityStyle.Brush(s) : ResultsViewModel.Res("TealBrush");
    public Brush VerdictSoft => ResultsViewModel.Res(Status switch
    {
        Severity.Problem => "ProblemTintBrush",
        Severity.Warning => "WarningTintBrush",
        _ => "TealTintBrush",
    });

    public Brush VerdictEdge => ResultsViewModel.Res(Status switch
    {
        Severity.Problem => "ProblemEdgeBrush",
        Severity.Warning => "WarningEdgeBrush",
        _ => "TealEdgeBrush",
    });
    public string VerdictIcon => Status is null ? "" : "";

    private string? Fixer => _findings.FirstOrDefault(f => f.Finding.Kind == FindingKind.AlreadyFixed)?.Finding.Resources[0].Name
        is { } name && !name.Equals(Name, StringComparison.OrdinalIgnoreCase) ? name : null;

    private string Count(int n, Severity severity)
    {
        var allClashes = Open.Where(f => f.Severity == severity).All(f => f.IsClash);
        return allClashes ? Plain.Count(n, "clash", "clashes") : Plain.Count(n, "thing");
    }

    public ICommand OpenFolderCommand { get; }

    public bool Matches(string query) =>
        query.Length == 0
        || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || CategoryText.Contains(query, StringComparison.OrdinalIgnoreCase);

    public void Refresh()
    {
        foreach (var name in new[] { nameof(ProblemCount), nameof(WarningCount), nameof(HasProblems), nameof(HasWarnings),
                     nameof(Status), nameof(IsClear), nameof(CountTip), nameof(VerdictTitle), nameof(VerdictText),
                     nameof(VerdictBrush), nameof(VerdictSoft), nameof(VerdictEdge), nameof(VerdictIcon) })
            OnPropertyChanged(name);
    }

    public static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" },
    };

    public static string Describe(ResourceInfo r)
    {
        var bits = new List<string>();
        if (r.Categories.Count > 0) bits.Add(string.Join(" / ", r.Categories.Select(c => c.Trim('[', ']'))));
        bits.Add(r.StartOrder is { } n ? $"starts {Ordinal(n + 1)}" : "switched off");
        if (r.IsLocked) bits.Add("locked");
        return string.Join("  ·  ", bits);
    }
}

public sealed record MapChip(string Text, bool IsSelf, ICommand? Command, string? Tip);

// One clash (or other issue) as it affects a single map, shown as a card on that map's page.
public sealed class IssueCard
{
    private const int MaxChips = 6;
    private const int MaxFiles = 3;

    public IssueCard(FindingViewModel finding, ResourceInfo self, ResultsViewModel owner)
    {
        Finding = finding;
        var f = finding.Finding;
        OpenCommand = new RelayCommand(() => owner.GoToFinding(finding));

        MapChip Chip(ResourceInfo r) => r == self
            ? new MapChip("This map", true, null, r.Name)
            : new MapChip(r.Name, false, new RelayCommand(() => owner.GoToMap(r)), $"Go to {r.Name}");

        if (IsClash && f.Winner is { } winner)
        {
            Used = [Chip(winner)];
            var losers = f.Resources.Where(r => r != winner).OrderBy(r => r == self ? 0 : 1).ToList();
            var chips = losers.Take(MaxChips).Select(Chip).ToList();
            if (losers.Count > MaxChips)
                chips.Add(new MapChip($"+{losers.Count - MaxChips} more", false, OpenCommand, "See every map in this clash"));
            Ignored = chips;
        }

        Files = finding.Files.Take(MaxFiles).ToList();
        var more = finding.Files.Count - MaxFiles;
        MoreFiles = more > 0 ? $"+ {Plain.Count(more, "more file")}" : null;

        if (IsClash)
        {
            Why = WhyText(f, self);
            ResolveCommand = new RelayCommand(() => owner.OpenResolve([finding], self));
        }
    }

    public FindingViewModel Finding { get; }
    public bool IsClash => Finding.IsClash;
    public string Title => IsClash ? Finding.Finding.ShortTitle : Finding.Finding.Title;
    public string? Body => IsClash ? Why : Finding.Finding.WhatsWrong;
    public bool HasBody => Body is not null;
    public string? Why { get; }
    public ICommand? ResolveCommand { get; }

    // Why these maps clash and which one wins, from the point of view of the map being looked at.
    public static string WhyText(Finding f, ResourceInfo self)
    {
        var others = f.Resources.Where(r => r != self).ToList();
        var all = others.Count == 1 ? "both" : "all";
        var who = others.Count == 1 ? $"This map and {others[0].Name} {all}" : $"This map and {others.Count} others {all}";

        var world = f.Files.Any(x => FileTypes.IsWorldFile(x.Extension)
                                     || x.Extension.Equals(".ynv", StringComparison.OrdinalIgnoreCase)
                                     || x.Extension.Equals(".ynd", StringComparison.OrdinalIgnoreCase));
        var what = world
            ? $"{who} change the same part of the GTA world, so each one ships its own copy of the same game files."
            : $"{who} include {Plain.JoinAnd(f.Files.Select(x => FileTypes.Things(x.Extension)).Distinct())} with the same names.";

        if (f.Winner is not { } winner) return $"{what} Only one copy of each file can load.";

        string Pos(ResourceInfo r) => r.StartOrder is { } n ? MapViewModel.Ordinal(n + 1) : "not started";
        string load;
        if (winner == self)
        {
            var runnerUp = others.OrderByDescending(r => r.StartOrder ?? -1).First();
            load = others.Count == 1
                ? $"This map starts later ({Pos(self)} vs {Pos(runnerUp)}), so its copies are used and {runnerUp.Name}'s are ignored."
                : $"This map starts last ({Pos(self)}), so its copies are used and the others' are ignored.";
        }
        else
        {
            load = $"{winner.Name} starts later ({Pos(winner)} vs this map's {Pos(self)}), so its copies are used and this map's are ignored.";
        }
        return $"{what} Only one copy of each file can load. {load}";
    }

    public IReadOnlyList<MapChip> Used { get; } = [];
    public IReadOnlyList<MapChip> Ignored { get; } = [];
    public IReadOnlyList<FindingViewModel.FileRow> Files { get; }
    public string? MoreFiles { get; }
    public bool HasMoreFiles => MoreFiles is not null;

    public string OpenLabel => IsClash ? "Full details" : "Details";
    public ICommand OpenCommand { get; }
}
