using System.IO;
using System.Windows;
using System.Windows.Controls;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>Batch rename rule: name pattern with [%N] [%F] [%D], start number, digits, letter case, find/replace and a live sample.</summary>
public partial class RenamePanel : UserControl
{
    internal const int MaxStart = 999999;
    internal const int MaxDigits = 9;
    private const int SampleCount = 3;

    private static readonly (NameCase Value, string Label)[] Cases =
    [
        (NameCase.Keep, EditText.Rename_CaseKeep),
        (NameCase.Lower, EditText.Rename_CaseLower),
        (NameCase.Upper, EditText.Rename_CaseUpper),
    ];

    private bool _ready;
    private IReadOnlyList<string> _files = [];
    private string? _targetExtension;

    public RenamePanel()
    {
        InitializeComponent();
        CaseBox.ItemsSource = Cases.Select(c => c.Label).ToList();
        Load(DefaultRule());
    }

    /// <summary>Raised after every change; read <see cref="IsValid"/> afterwards.</summary>
    public event EventHandler? Changed;

    public bool IsValid { get; private set; } = true;

    public static RenameRule DefaultRule() => new(EditText.Rename_DefaultTemplate, 1, 0, NameCase.Keep, null, null);

    public void Load(RenameRule rule)
    {
        _ready = false;
        TemplateBox.Text = rule.Template;
        StartBox.Text = ResizePanel.Format(rule.StartNumber);
        DigitsBox.Text = ResizePanel.Format(rule.Digits);
        CaseBox.SelectedIndex = Math.Max(0, Array.FindIndex(Cases, c => c.Value == rule.Case));
        FindBox.Text = rule.Find ?? "";
        ReplaceBox.Text = rule.Replace ?? "";
        _ready = true;
        Refresh();
    }

    /// <summary>Current values as an engine rule. Only meaningful while <see cref="IsValid"/> is true.</summary>
    public RenameRule BuildRule()
    {
        ResizePanel.TryParse(StartBox.Text, 0, MaxStart, out int start);
        ResizePanel.TryParse(DigitsBox.Text, 0, MaxDigits, out int digits);
        return new RenameRule(
            TemplateBox.Text.Trim(),
            start,
            digits,
            Cases[Math.Max(0, CaseBox.SelectedIndex)].Value,
            FindBox.Text.Length > 0 ? FindBox.Text : null,
            FindBox.Text.Length > 0 ? ReplaceBox.Text : null);
    }

    /// <summary>Files the sample is built from (the first three are shown).</summary>
    public void SetFiles(IReadOnlyList<string> files)
    {
        _files = files;
        Refresh();
    }

    /// <summary>Extension of the output files (".jpg"), or null when each file keeps its own extension.</summary>
    public void SetTargetExtension(string? extension)
    {
        _targetExtension = extension;
        Refresh();
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) Refresh();
    }

    private void Refresh()
    {
        if (!_ready) return;
        bool ok = OutputOptionsControl.ShowError(NumberError,
            ResizePanel.RangeError(StartBox.Text, 0, MaxStart) ?? ResizePanel.RangeError(DigitsBox.Text, 0, MaxDigits));

        string? templateError = TemplateBox.Text.Trim().Length == 0 ? EditText.Error_TemplateRequired : null;
        var samples = new List<string>();
        if (ok && templateError is null)
        {
            var rule = BuildRule();
            try
            {
                if (_files.Count == 0)
                {
                    // No files yet: still validate the pattern against a neutral name.
                    rule.Apply("image", 0, DateTime.Now);
                }
                for (int i = 0; i < Math.Min(SampleCount, _files.Count); i++)
                {
                    var source = _files[i];
                    var newName = rule.Apply(Path.GetFileNameWithoutExtension(source), i, LastWriteTime(source));
                    var extension = _targetExtension ?? Path.GetExtension(source);
                    samples.Add(string.Format(EditText.Rename_SampleItem, Path.GetFileName(source), newName + extension));
                }
            }
            catch (ArgumentException)
            {
                templateError = EditText.Error_TemplateInvalid;
                samples.Clear();
            }
        }
        ok &= OutputOptionsControl.ShowError(TemplateError, templateError);
        SampleList.ItemsSource = samples;
        IsValid = ok;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static DateTime LastWriteTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.Now; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return DateTime.Now; }
    }
}
