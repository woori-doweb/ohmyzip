using System.Windows;
using System.Windows.Automation;
using OhMyPic.App.Text;

namespace OhMyPic.App.Dialogs;

/// <summary>Shown once after an update: the release notes of the running version in the UI language.</summary>
public partial class WhatsNewWindow : Window
{
    /// <param name="version">Version key (major.minor.patch) of the running version.</param>
    /// <param name="notes">Notes section for the UI language (not empty).</param>
    public WhatsNewWindow(string version, string notes)
    {
        InitializeComponent();
        Title = HeadingText.Text = string.Format(UpdateText.WhatsNew_Title, version);
        NotesText.Text = notes;
        // The element's name is the label; screen readers get the notes themselves as its help text.
        AutomationProperties.SetHelpText(NotesText, notes);
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }
}
