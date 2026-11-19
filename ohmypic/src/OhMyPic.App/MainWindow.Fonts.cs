using System.Windows;
using OhMyPic.App.Dialogs;

namespace OhMyPic.App;

/// <summary>Tools &gt; Fonts…: one non-modal font list window per main window, re-activated when it is already open.</summary>
public partial class MainWindow
{
    private FontListWindow? _fontWindow;

    private void OpenFontList()
    {
        if (_fontWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var window = new FontListWindow { Owner = this };
        window.Closed += (_, _) => _fontWindow = null;
        _fontWindow = window;
        window.Show();
    }
}
