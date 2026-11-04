using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OhMyZip.App.Settings;
using OhMyZip.App.Text;
using OhMyZip.App.Theme;

namespace OhMyZip.App.Dialogs;

/// <summary>
/// 항목 몇 개짜리 작은 대화상자(툴바 버튼 설정·색상 설정·압축 파일 설명 편집). 같은 틀: 제목 + 본문 + 확정(동사)/취소.
/// </summary>
public static class SimpleDialogs
{
    private static (Window Window, StackPanel Body) Frame(Window owner, string title, string automationId, double width, string okLabel)
    {
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)owner.FindResource("Oz.WindowStyle"),
        };
        AutomationProperties.SetAutomationId(window, automationId);
        window.SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(window);
        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        var body = new StackPanel();
        root.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = okLabel, IsDefault = true, Style = (Style)owner.FindResource("Oz.Button") };
        var cancel = new Button { Content = DialogText.Cancel_Button, IsCancel = true, Style = (Style)owner.FindResource("Oz.Button") };
        AutomationProperties.SetAutomationId(ok, automationId + "-Ok");
        AutomationProperties.SetAutomationId(cancel, automationId + "-Cancel");
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        window.Content = root;
        return (window, body);
    }

    private static CheckBox Check(string text, bool value, string automationId)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
        box.SetResourceReference(Control.ForegroundProperty, ThemeManager.Text);
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    /// <summary>툴바 버튼 설정: 버튼마다 표시/숨김. 반환값은 숨길 명령 키 목록(취소면 null).</summary>
    public static List<string>? ToolbarButtons(Window owner, IReadOnlyList<(string Key, string Label)> buttons, IReadOnlyCollection<string> hidden)
    {
        var (window, body) = Frame(owner, DialogText.Toolbar_Title, "OZ-ToolbarButtonsDialog", 360, DialogText.Apply_Button);
        body.Children.Add(new TextBlock { Text = DialogText.Toolbar_Prompt, TextWrapping = TextWrapping.Wrap });
        var boxes = new List<(string Key, CheckBox Box)>();
        foreach (var (key, label) in buttons)
        {
            var box = Check(label, !hidden.Contains(key), "OZ-ToolbarButton-" + key);
            boxes.Add((key, box));
            body.Children.Add(box);
        }
        return window.ShowDialog() == true ? boxes.Where(b => b.Box.IsChecked != true).Select(b => b.Key).ToList() : null;
    }

    /// <summary>색상 설정: 앱 내부 테마 재정의(환경 설정 일반과 같은 값). 취소면 null.</summary>
    public static AppTheme? ColorTheme(Window owner, AppTheme current)
    {
        var (window, body) = Frame(owner, DialogText.Theme_Title, "OZ-ThemeDialog", 360, DialogText.Apply_Button);
        body.Children.Add(new TextBlock
        {
            Text = DialogText.Theme_Prompt,
            TextWrapping = TextWrapping.Wrap,
        });
        var options = new[] { (AppTheme.System, DialogText.Theme_System, "OZ-Theme-System"), (AppTheme.Light, DialogText.Theme_Light, "OZ-Theme-Light"), (AppTheme.Dark, DialogText.Theme_Dark, "OZ-Theme-Dark") };
        var radios = new List<(AppTheme Value, RadioButton Radio)>();
        foreach (var (value, label, id) in options)
        {
            var radio = new RadioButton { Content = label, GroupName = "theme", IsChecked = value == current, Margin = new Thickness(0, 8, 0, 0) };
            radio.SetResourceReference(Control.ForegroundProperty, ThemeManager.Text);
            AutomationProperties.SetAutomationId(radio, id);
            radios.Add((value, radio));
            body.Children.Add(radio);
        }
        if (window.ShowDialog() != true) return null;
        return radios.First(r => r.Radio.IsChecked == true).Value;
    }

    /// <summary>압축 파일 설명 편집(여러 줄). 취소면 null, 빈 문자열은 설명 삭제.</summary>
    public static string? Comment(Window owner, string archiveName, string? current)
    {
        var (window, body) = Frame(owner, DialogText.Comment_Title, "OZ-CommentDialog", 520, DialogText.Save_Button);
        body.Children.Add(new TextBlock { Text = string.Format(DialogText.Comment_Prompt, archiveName), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var box = new TextBox
        {
            Text = current ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 4, 6, 4),
        };
        box.SetResourceReference(Control.BackgroundProperty, ThemeManager.Background);
        box.SetResourceReference(Control.ForegroundProperty, ThemeManager.Text);
        box.SetResourceReference(Control.BorderBrushProperty, ThemeManager.Border);
        box.SetResourceReference(TextBox.CaretBrushProperty, ThemeManager.Text);
        AutomationProperties.SetName(box, DialogText.Comment_Name);
        AutomationProperties.SetAutomationId(box, "OZ-Comment-Text");
        body.Children.Add(box);
        window.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
