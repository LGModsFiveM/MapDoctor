using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LGMods.MapDoctor.App.ViewModels;
using LGMods.MapDoctor.Core;

namespace LGMods.MapDoctor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var main = new MainViewModel();
        var window = new MainWindow { DataContext = main };

        // Developer screenshot mode: LGModsMapDoctor.exe --snapshot <server folder> <output folder> [--try-fix]
        // Only --try-fix moves files, so only ever use it on a throwaway test server.
        if (e.Args.Length >= 3 && e.Args[0] == "--snapshot")
        {
            main.RememberFolders = false;
            Snapshot(window, main, e.Args[1], e.Args[2], tryFix: e.Args.Contains("--try-fix"));
            return;
        }

        window.Show();
    }

    private async void Snapshot(MainWindow window, MainViewModel main, string folder, string outDir, bool tryFix)
    {
        try
        {
            await SnapshotSteps(window, main, folder, outDir, tryFix);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString());
        }
        Shutdown();
    }

    private async Task SnapshotSteps(MainWindow window, MainViewModel main, string folder, string outDir, bool tryFix)
    {
        Directory.CreateDirectory(outDir);
        window.Width = 1280;
        window.Height = 860;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -4000;
        window.ShowActivated = false;
        window.Show();

        Save(window, Path.Combine(outDir, "1-welcome.png"));

        var result = MapDoctorScanner.Scan(folder);
        main.ShowResults(result, folder);
        Save(window, Path.Combine(outDir, "2-results.png"));

        if (main.Page is ResultsViewModel results)
        {
            // Someone looking for a map they bought.
            results.Search = "JamestownEstate";
            Save(window, Path.Combine(outDir, "3-map-search.png"));

            // "By clash": the clash with the most files.
            results.Search = "";
            results.IsMapView = false;
            results.ShowAll = true;
            results.Selected = results.Findings.Where(f => f.IsClash).OrderByDescending(f => f.Files.Count).FirstOrDefault();
            Save(window, Path.Combine(outDir, "4-clash.png"));

            // Maps worth checking.
            results.IsMapView = true;
            results.ShowWarnings = true;
            Save(window, Path.Combine(outDir, "5-worth-checking.png"));

            // Smaller window, back on the default view.
            results.ShowProblems = true;
            window.Width = 1024;
            window.Height = 720;
            Save(window, Path.Combine(outDir, "6-small-window.png"));
            window.Width = 1280;
            window.Height = 860;

            // Resolve dialog for a map someone bought. Next only plans the moves; nothing is touched.
            results.ShowAll = true;
            var map = results.Maps.FirstOrDefault(m => m.Name.Contains("Jamestown", StringComparison.OrdinalIgnoreCase) && m.CanResolve)
                      ?? results.Maps.FirstOrDefault(m => m.CanResolve);
            if (map is not null)
            {
                results.GoToMap(map.Resource);
                map.ResolveCommand.Execute(null);
                Save(window, Path.Combine(outDir, "7-resolve-choose.png"));
                if (main.Dialog is ResolveViewModel open && open.Groups.FirstOrDefault(g => g.Resources.Count > 2) is { } group)
                {
                    group.ShowFiles = true;
                    group.Files[0].Select(group.Files[0].Options[^1]);
                    window.Height = 1500;
                    Save(window, Path.Combine(outDir, "7b-resolve-expanded.png"));
                    window.Width = 1024;
                    window.Height = 720;
                    Save(window, Path.Combine(outDir, "7c-resolve-small.png"));
                    if (group.CanMerge) group.Merge = true; else group.ChooseAll(open.Focus);
                    group.ShowFiles = false;
                    window.Width = 1280;
                    window.Height = 860;
                }

                // Read-only run: also ignore the last clash, so the confirm step shows both kinds of change.
                // (Never in --try-fix, because applying "ignore" writes to the settings file.)
                if (!tryFix && main.Dialog is ResolveViewModel withIgnore && withIgnore.Groups.Count > 1)
                {
                    withIgnore.Groups[^1].Ignore = true;
                    Save(window, Path.Combine(outDir, "7d-resolve-ignore.png"));
                }

                if (main.Dialog is ResolveViewModel dialog && dialog.NextCommand.CanExecute(null))
                {
                    dialog.NextCommand.Execute(null);
                    Save(window, Path.Combine(outDir, "8-resolve-confirm.png"));

                    if (tryFix)
                    {
                        dialog.ApplyCommand.Execute(null);
                        await WaitFor(() => main.Page is ResultsViewModel { HasNotice: true });
                        Save(window, Path.Combine(outDir, "9-after-fix.png"));

                        // Undo it from "Past fixes", which reads the saved list back from the backup folder.
                        var fixedResults = (ResultsViewModel)main.Page;
                        fixedResults.OpenPastFixesCommand.Execute(null);
                        Save(window, Path.Combine(outDir, "9b-past-fixes.png"));
                        ((PastFixesViewModel)main.Dialog!).Items[0].UndoCommand.Execute(null);
                        await WaitFor(() => main.Page is ResultsViewModel r && r != fixedResults && r.HasNotice);
                        Save(window, Path.Combine(outDir, "10-after-undo.png"));

                        // Two separate fixes, then restore everything from the start screen with "Undo all".
                        var again = MapDoctorScanner.Scan(folder);
                        var copies = again.Findings.Where(f => f.Kind == Core.Model.FindingKind.ClashingFiles)
                            .SelectMany(clash => clash.Files.SelectMany(f => f.Copies).Where(c => c.Resource != clash.Winner)).ToList();
                        var earlier = DateTime.Now.AddMinutes(-5);
                        Core.Fixing.Resolver.Apply(Core.Fixing.Resolver.Plan(again.ResourcesFolder, copies.Take(1), earlier), earlier);
                        Core.Fixing.Resolver.Apply(Core.Fixing.Resolver.Plan(again.ResourcesFolder, copies.Skip(1).Take(1), DateTime.Now), DateTime.Now);

                        main.ShowWelcome([folder]);
                        Save(window, Path.Combine(outDir, "11-welcome-undo.png"));
                        ((WelcomeViewModel)main.Page).Recent[0].UndoCommand.Execute(null);
                        await WaitFor(() => main.Dialog is PastFixesViewModel);
                        Save(window, Path.Combine(outDir, "12-undo-fixes.png"));
                        var beforeUndoAll = main.Page;
                        ((PastFixesViewModel)main.Dialog!).UndoAllCommand.Execute(null);
                        await WaitFor(() => main.Page is ResultsViewModel r && r != beforeUndoAll && r.HasNotice);
                        Save(window, Path.Combine(outDir, "13-after-undo-all.png"));
                    }
                    else
                    {
                        // Show the banner with a made-up record so the real server is never changed.
                        main.Dialog = null;
                        var moves = dialog.FilesToMove()
                            .Select(f => new Core.Fixing.FileMove(f.Resource.Name, f.FileName, f.FullPath, f.FullPath + ".bak")).ToList();
                        results.Notice = FixNotice.Fixed(main, new Core.Fixing.FixRecord(DateTime.Now, dialog.BackupFolder, moves));
                        Save(window, Path.Combine(outDir, "9-after-fix.png"));
                    }
                }
            }
        }
    }

    private static async Task WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 600 && !done(); i++) await Task.Delay(50);
        if (!done()) throw new TimeoutException("Gave up waiting for the scan to finish.");
    }

    private static void Save(Window window, string path)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
