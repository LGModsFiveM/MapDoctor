using System.IO;
using System.Windows.Input;
using LGMods.MapDoctor.App.Services;
using LGMods.MapDoctor.Core;
using LGMods.MapDoctor.Core.Model;
using Microsoft.Win32;

namespace LGMods.MapDoctor.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Settings _settings = Settings.Load();
    private object _page;
    private object? _dialog;
    private string? _serverPath;
    private CancellationTokenSource? _cancel;

    public MainViewModel()
    {
        _page = new WelcomeViewModel(this, _settings.RecentFolders);
        NewScanCommand = new RelayCommand(() => ShowWelcome());
        OpenDiscordCommand = new RelayCommand(() => Shell.Open(Links.Discord));
        OpenStoreCommand = new RelayCommand(() => Shell.Open(Links.Store));
    }

    public object Page { get => _page; private set => Set(ref _page, value); }

    // Off in screenshot mode, so test servers don't end up in the recent list.
    public bool RememberFolders { get; set; } = true;

    public object? Dialog { get => _dialog; set => Set(ref _dialog, value); }

    public string? ServerPath
    {
        get => _serverPath;
        private set
        {
            if (!Set(ref _serverPath, value)) return;
            OnPropertyChanged(nameof(ServerName));
            OnPropertyChanged(nameof(HasServer));
        }
    }

    public string ServerName => ServerPath is null ? "" : WelcomeViewModel.FriendlyName(ServerPath);
    public bool HasServer => ServerPath is not null;

    public ICommand NewScanCommand { get; }
    public ICommand OpenDiscordCommand { get; }
    public ICommand OpenStoreCommand { get; }

    public void ShowWelcome(string? error = null) => ShowWelcome(_settings.RecentFolders, error);

    // Screenshot mode passes its own list of recent servers.
    public void ShowWelcome(IEnumerable<string> recent, string? error = null)
    {
        ServerPath = null;
        Dialog = null;
        Page = new WelcomeViewModel(this, recent) { Error = error };
    }

    public void PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Pick your server folder (the one with server.cfg in it)" };
        if (dialog.ShowDialog() == true) _ = ScanAsync(dialog.FolderName);
    }

    public Task ScanAsync(string folder) => ScanAsync(folder, null);

    public Task RescanAsync(string folder, ViewState view, FixNotice notice)
    {
        Dialog = null;
        return ScanAsync(folder, results =>
        {
            results.Restore(view);
            results.Notice = notice;
        });
    }

    public async Task ScanAsync(string folder, Action<ResultsViewModel>? then)
    {
        if (MapDoctorScanner.FindResourcesFolder(folder) is null)
        {
            ShowWelcome(
                $"We couldn't find any FiveM resources in \"{folder}\". Pick the folder that has server.cfg " +
                "and a resources folder inside it.");
            return;
        }

        _cancel = new CancellationTokenSource();
        var scanning = new ScanningViewModel(folder, () => _cancel.Cancel());
        ServerPath = folder;
        Page = scanning;

        try
        {
            var progress = new Progress<ScanProgress>(scanning.Update);
            var token = _cancel.Token;
            var result = await Task.Run(() => MapDoctorScanner.Scan(folder, progress, token), token);
            if (RememberFolders) _settings.Remember(folder);
            ShowResults(result, folder);
            if (Page is ResultsViewModel results) then?.Invoke(results);
        }
        catch (OperationCanceledException)
        {
            ShowWelcome();
        }
        catch (Exception e)
        {
            ShowWelcome($"Something went wrong while checking that folder: {e.Message}");
        }
    }

    public void ShowResults(ScanResult result, string folder)
    {
        ServerPath = folder;
        Page = new ResultsViewModel(this, _settings, result, folder);
    }
}

public sealed class WelcomeViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string? _error;

    public WelcomeViewModel(MainViewModel main, IEnumerable<string> recent)
    {
        _main = main;
        PickFolderCommand = new RelayCommand(main.PickFolder);
        Recent = recent.Where(Directory.Exists).Select((path, i) =>
        {
            var fixes = FixesIn(path);
            return new RecentFolder(
                path,
                FriendlyName(path),
                new RelayCommand(() => _ = main.ScanAsync(path)),
                IsFirst: i == 0,
                fixes,
                // Check the server first so the list of fixes is up to date, then open it.
                new RelayCommand(() => _ = main.ScanAsync(path, results => results.OpenPastFixes())));
        }).ToList();
    }

    public ICommand PickFolderCommand { get; }
    public string Version => "v" + Shell.AppVersion;
    public IReadOnlyList<RecentFolder> Recent { get; }
    public bool HasRecent => Recent.Count > 0;

    public string? Error { get => _error; set { Set(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);

    public void Dropped(string path) => _ = _main.ScanAsync(path);

    // "D:\FiveM\server" → "FiveM / server", so similar folders are easy to tell apart.
    public static string FriendlyName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "");
        return string.IsNullOrEmpty(parent) ? name : $"{parent} / {name}";
    }

    // How many fixes on this server can be undone right now (0 if we can't tell).
    private static int FixesIn(string path)
    {
        try
        {
            return MapDoctorScanner.FindResourcesFolder(path) is { } resources
                ? Core.Fixing.Resolver.FindRecords(resources).Count(r => Core.Fixing.Resolver.WhyCantUndo(r) is null)
                : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public sealed record RecentFolder(string Path, string Name, ICommand ScanCommand, bool IsFirst, int Fixes, ICommand UndoCommand)
    {
        public bool HasFixes => Fixes > 0;
        public string UndoLabel => Fixes == 1 ? "Undo a fix" : $"Undo fixes ({Fixes})";
    }
}

public sealed class ScanningViewModel(string folder, Action cancel) : ObservableObject
{
    private string _message = "Getting started…";
    private double _percent;

    public string Folder { get; } = folder;
    public string Message { get => _message; private set => Set(ref _message, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public ICommand CancelCommand { get; } = new RelayCommand(cancel);

    public void Update(ScanProgress p)
    {
        Message = p.Message;
        Percent = p.Fraction * 100;
    }
}
