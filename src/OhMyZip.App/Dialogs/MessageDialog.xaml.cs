using System.Windows;
using System.Windows.Controls;
using OhMyZip.App.Text;
using OhMyZip.Core;

namespace OhMyZip.App.Dialogs;

public enum DialogKind { Info, Success, Warning, Error, Question }

/// <summary>아이콘 + 제목 + 본문 + (선택) 문제 목록 + 버튼 n개. 반환값은 누른 버튼 번호(닫기·Esc 는 cancelIndex).</summary>
public partial class MessageDialog : Window
{
    private int _result;

    private MessageDialog(DialogKind kind, string title, string heading, string body, IReadOnlyList<string>? problems,
        IReadOnlyList<string> buttons, int defaultIndex, int cancelIndex)
    {
        InitializeComponent();
        Title = title;
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
        HeadingText.Text = (kind is DialogKind.Error or DialogKind.Warning ? KindText.Text + ": " : "") + heading;
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
                Style = (Style)FindResource("Oz.Button"),
                IsDefault = i == defaultIndex,
                IsCancel = i == cancelIndex,
            };
            b.Click += (_, _) => { _result = index; DialogResult = true; };
            ButtonPanel.Children.Add(b);
            if (i == defaultIndex) Loaded += (_, _) => b.Focus();
        }
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    /// <summary>defaultIndex &lt; 0 = 기본 단추 없음(Enter 로 확정하지 않음). cancelIndex &lt; 0 이면 마지막 단추가 취소.</summary>
    public static int Show(Window? owner, DialogKind kind, string heading, string body, IReadOnlyList<string>? problems,
        IReadOnlyList<string> buttons, int defaultIndex = 0, int cancelIndex = -1)
    {
        if (cancelIndex < 0) cancelIndex = buttons.Count - 1;
        var dlg = new MessageDialog(kind, "ohmyZip", heading, body, problems, buttons, defaultIndex, cancelIndex);
        if (owner is { IsLoaded: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg._result;
    }

    public static void Info(Window? owner, string heading, string body = "", IReadOnlyList<string>? problems = null) =>
        Show(owner, DialogKind.Info, heading, body, problems, [DialogText.Ok_Button]);

    public static void Error(Window? owner, string heading, string body, IReadOnlyList<string>? problems = null) =>
        Show(owner, DialogKind.Error, heading, body, problems, [DialogText.Ok_Button]);

    /// <summary>엔진 결과를 상태에 맞는 아이콘으로 보여 준다.</summary>
    public static void Result(Window? owner, string heading, OperationResult result)
    {
        var kind = result.Status switch
        {
            OperationStatus.Succeeded => result.Problems.Count > 0 ? DialogKind.Warning : DialogKind.Success,
            OperationStatus.Cancelled => DialogKind.Info,
            _ => DialogKind.Error,
        };
        Show(owner, kind, heading, result.Message, result.Problems, [DialogText.Ok_Button]);
    }
}
