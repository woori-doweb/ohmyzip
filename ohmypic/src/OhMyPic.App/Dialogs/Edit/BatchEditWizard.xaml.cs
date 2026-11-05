using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OhMyPic.App.Text;
using OhMyPic.Core;

namespace OhMyPic.App.Dialogs.Edit;

/// <summary>One row of the batch file list.</summary>
public sealed record BatchFileItem(string Path, string Name);

/// <summary>
/// Batch edit wizard, five steps in fixed order: 1 select tasks (+ files), 2 resize, 3 rotate, 4 rename, 5 convert/save.
/// Steps of unchecked tasks are skipped; step 5 always stays because it holds the output options (without the
/// convert task it only shows where to save). On the last step Next becomes Start. Cancel asks for confirmation.
/// </summary>
public partial class BatchEditWizard : Window
{
    private const int SelectStep = 0, ResizeStepIndex = 1, RotateStepIndex = 2, RenameStepIndex = 3, SaveStepIndex = 4;

    private readonly List<BatchFileItem> _files = [];
    private readonly TextBlock[] _stepTexts;
    private readonly FrameworkElement[] _pages;
    private readonly string _defaultStep5Label;
    private bool _ready;
    private bool _started;
    private int _step;

    internal BatchEditWizard(IReadOnlyList<string> files, AppSettings settings)
    {
        InitializeComponent();
        _stepTexts = [Step1Text, Step2Text, Step3Text, Step4Text, Step5Text];
        _pages = [SelectPage, ResizeStep, RotateStep, RenameStep, SavePage];
        _defaultStep5Label = EditText.Batch_Step5;

        foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
            _files.Add(new BatchFileItem(f, Path.GetFileName(f)));

        var tasks = EditSession.BatchTasks;
        ResizeTaskBox.IsChecked = tasks[0];
        RotateTaskBox.IsChecked = tasks[1];
        RenameTaskBox.IsChecked = tasks[2];
        ConvertTaskBox.IsChecked = tasks[3];

        if (EditSession.BatchResize is { } resize) ResizeStep.Load(resize);
        RotateStep.Load(EditSession.BatchRotate ?? new RotateOptions(RotateFlip.Rotate90, settings.AutoRotateByExif), settings.AutoRotateByExif);
        RenameStep.Load(EditSession.Rename ?? RenamePanel.DefaultRule());
        ConvertStep.Load(EditSession.BatchConvert ?? new ConvertOptions(ImageFormatKind.Jpeg, ConvertPanel.NormalizeQuality(settings.JpegQuality), FrameMode.FirstOnly));
        OutputStep.Load(EditSession.BatchOutput ?? EditSession.DefaultOutput());

        ResizeStep.Changed += (_, _) => UpdateButtons();
        RenameStep.Changed += (_, _) => UpdateButtons();
        ConvertStep.Changed += (_, _) => { RenameStep.SetTargetExtension(TargetExtension()); UpdateButtons(); };
        OutputStep.Changed += (_, _) => UpdateButtons();

        _ready = true;
        FileList.ItemsSource = _files;
        FilesChanged();
        ShowStep(SelectStep);
        SourceInitialized += (_, _) => NativeMethods.ApplyDarkTitleBar(this);
    }

    public IReadOnlyList<string> Files => _files.Select(f => f.Path).ToList();

    public ResizeOptions? Resize { get; private set; }

    public RotateOptions? Rotate { get; private set; }

    public RenameRule? Rename { get; private set; }

    public ConvertOptions? Convert { get; private set; }

    public OutputOptions Output { get; private set; } = EditSession.DefaultOutput();

    private bool[] TaskStates =>
    [
        ResizeTaskBox.IsChecked == true,
        RotateTaskBox.IsChecked == true,
        RenameTaskBox.IsChecked == true,
        ConvertTaskBox.IsChecked == true,
    ];

    private string? TargetExtension() => ConvertTaskBox.IsChecked == true ? ImageFormats.DefaultExtension(ConvertStep.Format) : null;

    /// <summary>Wizard steps that are shown for the current task selection, in order.</summary>
    private List<int> VisibleSteps()
    {
        var steps = new List<int> { SelectStep };
        if (ResizeTaskBox.IsChecked == true) steps.Add(ResizeStepIndex);
        if (RotateTaskBox.IsChecked == true) steps.Add(RotateStepIndex);
        if (RenameTaskBox.IsChecked == true) steps.Add(RenameStepIndex);
        steps.Add(SaveStepIndex);
        return steps;
    }

    private void ShowStep(int step)
    {
        _step = step;
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;

        bool convert = ConvertTaskBox.IsChecked == true;
        ConvertStep.Visibility = convert ? Visibility.Visible : Visibility.Collapsed;
        KeepFormatNote.Visibility = convert ? Visibility.Collapsed : Visibility.Visible;
        Step5Text.Text = convert ? _defaultStep5Label : EditText.Batch_Step5Save;

        var visible = VisibleSteps();
        for (int i = 0; i < _stepTexts.Length; i++)
        {
            var text = _stepTexts[i];
            bool shown = visible.Contains(i);
            text.FontWeight = i == step ? FontWeights.SemiBold : FontWeights.Normal;
            text.SetResourceReference(TextBlock.ForegroundProperty,
                i == step ? "Op.Accent" : shown ? "Op.Text" : "Op.DisabledText");
            string state = i == step ? EditText.Batch_StepCurrent : shown ? "" : EditText.Batch_StepSkipped;
            AutomationProperties.SetName(text, state.Length == 0 ? text.Text : string.Format(CultureInfo.CurrentCulture, EditText.Batch_StepState, text.Text, state));
        }

        int position = visible.IndexOf(step);
        StepCountText.Text = string.Format(CultureInfo.CurrentCulture, EditText.Batch_StepIndicator, position + 1, visible.Count);
        HeadingText.Text = _stepTexts[step].Text;

        var first = FirstPath();
        switch (step)
        {
            case ResizeStepIndex: ResizeStep.SetPreviewFile(first); break;
            case RotateStepIndex: RotateStep.SetPreviewFile(first); break;
            case RenameStepIndex:
                RenameStep.SetFiles(Files);
                RenameStep.SetTargetExtension(TargetExtension());
                break;
            case SaveStepIndex: ConvertStep.SetPreviewFile(first); break;
        }

        bool last = step == SaveStepIndex;
        NextButton.Content = last ? EditText.Batch_Start : EditText.Batch_Next;
        AutomationProperties.SetName(NextButton, last ? EditText.Batch_Start : EditText.Batch_Next);
        BackButton.IsEnabled = step != SelectStep;
        UpdateButtons();
    }

    private string? FirstPath() => _files.Count > 0 ? _files[0].Path : null;

    private void UpdateButtons()
    {
        if (!_ready) return;
        bool ok = _step switch
        {
            SelectStep => ValidateSelectPage(),
            ResizeStepIndex => ResizeStep.IsValid,
            RenameStepIndex => RenameStep.IsValid,
            SaveStepIndex => (ConvertTaskBox.IsChecked != true || ConvertStep.IsValid) && OutputStep.IsValid,
            _ => true,
        };
        NextButton.IsEnabled = ok;
    }

    private bool ValidateSelectPage()
    {
        bool anyTask = TaskStates.Any(t => t);
        bool ok = OutputOptionsControl.ShowError(TaskError, anyTask ? null : EditText.Error_NoTasks);
        ok &= OutputOptionsControl.ShowError(FilesError, _files.Count > 0 ? null : EditText.Error_NoFiles);
        return ok;
    }

    private void OnTaskChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ShowStep(_step);
    }

    private void FilesChanged()
    {
        FileList.Items.Refresh();
        FilesLabel.Text = string.Format(CultureInfo.CurrentCulture, EditText.Batch_FilesLabel, _files.Count);
        RemoveFilesButton.IsEnabled = FileList.SelectedItems.Count > 0;
        UpdateButtons();
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveFilesButton.IsEnabled = FileList.SelectedItems.Count > 0;

    private void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || FileList.SelectedItems.Count == 0) return;
        RemoveSelected();
        e.Handled = true;
    }

    private void OnRemoveFiles(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        var selected = FileList.SelectedItems.Cast<BatchFileItem>().ToList();
        foreach (var item in selected) _files.Remove(item);
        FilesChanged();
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        string patterns = string.Join(";", ImageFormats.ReadableExtensions.Select(x => "*" + x));
        var picker = new OpenFileDialog
        {
            Title = EditText.Batch_AddFilesTitle,
            Multiselect = true,
            CheckFileExists = true,
            Filter = string.Format(CultureInfo.CurrentCulture, EditText.Batch_ImageFilter, patterns),
        };
        var first = FirstPath();
        if (first is not null)
        {
            try
            {
                var dir = Path.GetDirectoryName(first);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) picker.InitialDirectory = dir;
            }
            catch (ArgumentException) { }
        }
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FileNames)
        {
            if (!ImageFormats.IsReadable(path)) continue;
            if (_files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            _files.Add(new BatchFileItem(path, Path.GetFileName(path)));
        }
        FilesChanged();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        var visible = VisibleSteps();
        int position = visible.IndexOf(_step);
        if (position > 0) ShowStep(visible[position - 1]);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (!NextButton.IsEnabled) return;
        if (_step == SaveStepIndex)
        {
            Start();
            return;
        }
        var visible = VisibleSteps();
        int position = visible.IndexOf(_step);
        if (position >= 0 && position + 1 < visible.Count) ShowStep(visible[position + 1]);
    }

    private void Start()
    {
        var tasks = TaskStates;
        Resize = tasks[0] ? ResizeStep.BuildOptions() : null;
        Rotate = tasks[1] ? RotateStep.BuildOptions() : null;
        Rename = tasks[2] ? RenameStep.BuildRule() : null;
        Convert = tasks[3] ? ConvertStep.BuildOptions() : null;
        Output = OutputStep.BuildOptions();

        EditSession.BatchTasks = tasks;
        if (Resize is not null) EditSession.BatchResize = Resize;
        if (Rotate is not null) EditSession.BatchRotate = Rotate;
        if (Rename is not null) EditSession.Rename = Rename;
        if (Convert is not null) EditSession.BatchConvert = Convert;
        EditSession.BatchOutput = Output;

        _started = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Esc = Cancel. Open drop-downs handle Esc themselves before it bubbles here.
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_started && !EditMessageDialog.AskYesNo(this, EditText.Batch_CancelQuestion))
            e.Cancel = true;
        base.OnClosing(e);
    }
}
