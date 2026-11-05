using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OhMyPic.App.Text;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>Small alert used by the edit flow: icon + heading + body + optional problem list + buttons. Returns the index of the pressed button.</summary>
public partial class EditMessageDialog : Window
{
    public enum Kind { Question, Error }

    private int _result;

    private EditMessageDialog(Kind kind, string heading, string body, IReadOnlyList<string>? problems, IReadOnlyList<string> buttons, int defaultIndex, int cancelIndex)
    {
        InitializeComponent();
        Title = EditText.App_Name;
        _result = cancelIndex;
        string kindText = kind == Kind.Error ? EditText.Message_KindError : EditText.Message_KindQuestion;
        IconGlyph.Text = kind == Kind.Error ? "\uEA39" : "\uE9CE";
        IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, kind == Kind.Error ? "Op.Error" : "Op.Text");
        AutomationProperties.SetName(IconGlyph, kindText);
        HeadingText.Text = heading;
        AutomationProperties.SetName(this, heading);
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
            var button = new Button
            {
                Content = buttons[i],
                Style = (Style)FindResource("Op.Button"),
                IsDefault = i == defaultIndex,
                IsCancel = i == cancelIndex,
            };
            AutomationProperties.SetName(button, buttons[i]);
            AutomationProperties.SetAutomationId(button, "OP-EditMessage-Button" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            button.Click += (_, _) => { _result = index; DialogResult = true; };
            ButtonPanel.Children.Add(button);
            if (i == defaultIndex) Loaded += (_, _) => button.Focus();
        }
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    /// <summary>Shows the alert modally. Closing or Esc returns <paramref name="cancelIndex"/>.</summary>
    public static int Show(Window? owner, Kind kind, string heading, string body, IReadOnlyList<string>? problems,
        IReadOnlyList<string> buttons, int defaultIndex, int cancelIndex)
    {
        var dlg = new EditMessageDialog(kind, heading, body, problems, buttons, defaultIndex, cancelIndex);
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg._result;
    }

    /// <summary>Yes/No question. Yes is the default button; No/Esc returns false.</summary>
    public static bool AskYesNo(Window? owner, string question) =>
        Show(owner, Kind.Question, question, "", null, [EditText.Yes_Button, EditText.No_Button], 0, 1) == 0;
}
