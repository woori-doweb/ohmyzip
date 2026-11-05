using System.IO;
using System.Windows;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>
/// "A file with this name already exists" prompt for the Ask duplicate policy: Overwrite / Keep Both (rename) / Skip,
/// plus "apply to all". Closing the window or Esc means Skip (nothing is overwritten).
/// </summary>
public partial class ConflictDialog : Window
{
    // Skip is the Cancel button (IsCancel), so Esc, closing and Skip all leave this default.
    private ConflictDecision _decision = ConflictDecision.Skip;

    private ConflictDialog(ConflictInfo info)
    {
        InitializeComponent();
        string proposed = info.ProposedOutput;
        HeadingText.Text = string.Format(EditText.Conflict_Heading, Path.GetFileName(proposed));
        FolderText.Text = string.Format(EditText.Conflict_Folder, Path.GetDirectoryName(proposed) ?? "");
        SourceText.Text = string.Format(EditText.Conflict_Source, Path.GetFileName(info.Input));
        Loaded += (_, _) => KeepBothButton.Focus();
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    /// <summary>Asks on the calling (UI) thread. Returns the decision and whether it applies to every remaining conflict.</summary>
    public static (ConflictDecision Decision, bool ApplyToAll) Ask(Window? owner, ConflictInfo info)
    {
        var dlg = new ConflictDialog(info);
        // The progress window is visible (shown) before its Loaded event runs; Owner only needs a shown window.
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return (dlg._decision, dlg.ApplyAllBox.IsChecked == true);
    }

    private void OnOverwrite(object sender, RoutedEventArgs e) => Finish(ConflictDecision.Overwrite);

    private void OnKeepBoth(object sender, RoutedEventArgs e) => Finish(ConflictDecision.Rename);

    private void Finish(ConflictDecision decision)
    {
        _decision = decision;
        DialogResult = true;
    }
}
