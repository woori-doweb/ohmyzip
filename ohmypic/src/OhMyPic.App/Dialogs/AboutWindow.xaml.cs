using System.Reflection;
using System.Windows;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString(3) ?? "";
        VersionText.Text = string.Format(DialogText.About_Version, version);
        FormatsText.Text = string.Join(" ", ImageFormats.ReadableExtensions);
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }
}
