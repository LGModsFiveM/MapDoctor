using System.Windows;
using LGMods.MapDoctor.App.Services;

namespace LGMods.MapDoctor.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => TitleBar.Brand(this);
    }
}
