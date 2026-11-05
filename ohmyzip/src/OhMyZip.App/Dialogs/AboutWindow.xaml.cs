using System.Reflection;
using System.Windows;
using OhMyZip.App.Text;

namespace OhMyZip.App.Dialogs;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString(3) ?? "";
        VersionText.Text = string.Format(DialogText.About_Version, version);
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }
}
