using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OhMyPic.App.Text;

namespace OhMyPic.App.Dialogs;

public enum DialogKind { Info, Success, Warning, Error, Question }

/// <summary>Icon + heading + body + optional problem list + n buttons. Returns the pressed button index (close/Esc = cancelIndex).</summary>
public partial class MessageDialog : Window
{
    private int _result;

    private MessageDialog(DialogKind kind, string heading, string body, IReadOnlyList<string>? problems,
        IReadOnlyList<string> buttons, int defaultIndex, int cancelIndex)
    {
        InitializeComponent();
        Title = DialogText.App_Name;
        _result = cancelIndex;
        (IconGlyph.Text, KindText.Text) = kind switch
        {
            DialogKind.Success => ("\uE930", DialogText.Message_KindSuccess),
            DialogKind.Warning => ("\uE7BA", DialogText.Message_KindWarning),
            DialogKind.Error => ("\uEA39", DialogText.Message_KindError),
            DialogKind.Question => ("\uE9CE", DialogText.Message_KindQuestion),
            _ => ("\uE946", DialogText.Message_KindInfo),
        };
        IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, kind == DialogKind.Error ? Theme.ThemeManager.Error : Theme.ThemeManager.Text);
        HeadingText.Text = heading;
        BodyText.Text = body;
        BodyText.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
        if (problems is { Count: > 0 })
        {
            ProblemList.ItemsSource = problems;
            ProblemList.Visibility = Visibility.Visible;
        }
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var b = new Button
            {
                Content = buttons[i],
                Style = (Style)FindResource("Op.Button"),
                IsDefault = i == defaultIndex,
                IsCancel = i == cancelIndex,
            };
            AutomationProperties.SetAutomationId(b, "OP-Message-Button" + i);
            b.Click += (_, _) => { _result = index; DialogResult = true; };
            ButtonPanel.Children.Add(b);
            if (i == defaultIndex) Loaded += (_, _) => b.Focus();
        }
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    /// <summary>defaultIndex &lt; 0 = no default button (Enter does not confirm). cancelIndex &lt; 0 = the last button cancels.</summary>
    public static int Show(Window? owner, DialogKind kind, string heading, string body, IReadOnlyList<string>? problems,
        IReadOnlyList<string> buttons, int defaultIndex = 0, int cancelIndex = -1)
    {
        if (cancelIndex < 0) cancelIndex = buttons.Count - 1;
        var dlg = new MessageDialog(kind, heading, body, problems, buttons, defaultIndex, cancelIndex);
        if (owner is { IsLoaded: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg._result;
    }

    public static void Info(Window? owner, string heading, string body = "", IReadOnlyList<string>? problems = null) =>
        Show(owner, DialogKind.Info, heading, body, problems, [DialogText.Ok_Button]);

    public static void Error(Window? owner, string heading, string body, IReadOnlyList<string>? problems = null) =>
        Show(owner, DialogKind.Error, heading, body, problems, [DialogText.Ok_Button]);

    public static void Warning(Window? owner, string heading, string body, IReadOnlyList<string>? problems = null) =>
        Show(owner, DialogKind.Warning, heading, body, problems, [DialogText.Ok_Button]);

    /// <summary>Verb button + Cancel. True when the verb was chosen.</summary>
    public static bool Confirm(Window? owner, string heading, string body, string verb) =>
        Show(owner, DialogKind.Question, heading, body, null, [verb, DialogText.Cancel_Button]) == 0;
}
