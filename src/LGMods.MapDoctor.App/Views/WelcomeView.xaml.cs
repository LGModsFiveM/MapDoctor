using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LGMods.MapDoctor.App.ViewModels;

namespace LGMods.MapDoctor.App.Views;

public partial class WelcomeView : UserControl
{
    public WelcomeView()
    {
        InitializeComponent();
        DragEnter += (_, e) => Highlight(e, true);
        DragOver += (_, e) => Highlight(e, true);
        DragLeave += (_, _) => SetOutline(false);
        Drop += OnDrop;
    }

    private static string? DroppedFolder(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return null;
        var path = paths[0];
        if (Directory.Exists(path)) return path;
        // Dropping server.cfg itself is fine too.
        return File.Exists(path) ? Path.GetDirectoryName(path) : null;
    }

    private void Highlight(DragEventArgs e, bool on)
    {
        var ok = DroppedFolder(e) is not null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        SetOutline(on && ok);
    }

    private void SetOutline(bool active) =>
        DropOutline.Stroke = (Brush)FindResource(active ? "TealBrush" : "LineStrongBrush");

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetOutline(false);
        if (DroppedFolder(e) is { } folder && DataContext is WelcomeViewModel vm) vm.Dropped(folder);
    }
}
