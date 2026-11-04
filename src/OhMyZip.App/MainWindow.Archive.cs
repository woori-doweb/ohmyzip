using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using OhMyZip.App.Dialogs;
using OhMyZip.App.Models;
using OhMyZip.App.Settings;
using OhMyZip.App.Text;
using OhMyZip.Core;

namespace OhMyZip.App;

/// <summary>압축 파일·항목 명령: 열기(암호)·테스트·풀기·알아서 풀기·새 압축·수정·설명·클립보드·파일 작업·도움말.</summary>
public partial class MainWindow
{
    private string? _clipboardTemp;
    private int _clipboardSeq;

    // ───────────────────────── 열기·닫기 ─────────────────────────

    private async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = MainText.OpenArchive_Title,
            Filter = MainText.Filter_Open,
            CheckFileExists = true,
        };
        if (_archive is not null) dialog.InitialDirectory = Path.GetDirectoryName(_archive.FilePath);
        if (dialog.ShowDialog(this) == true) await OpenArchiveAsync(dialog.FileName);
    }

    /// <summary>
    /// 압축 파일을 열어 목록을 보여 준다. 엔진이 PasswordRequired 를 돌려주면 암호를 물어 같은 형식 엔진으로 다시 연다.
    /// 암호는 ReadOptions 로 엔진·ArchiveModel 에만 두고(메모리), 닫으면 버린다.
    /// </summary>
    private async Task OpenArchiveAsync(string path, string? folder = null, string? selectName = null, ReadOptions? read = null)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, MainText.OpenArchiveFailed_Title, ex.Message);
            return;
        }
        var name = Path.GetFileName(full);
        if (!File.Exists(full))
        {
            MessageDialog.Error(this, string.Format(MainText.OpenNamedFailed_Title, name), Directory.Exists(full)
                ? MainText.Open_FolderNotArchive
                : string.Format(MainText.Open_FileNotFound, full));
            return;
        }

        bool sameArchive = _archive is not null && string.Equals(_archive.FilePath, full, StringComparison.OrdinalIgnoreCase);
        var options = read;
        bool tried = read?.Password is not null;
        while (true)
        {
            var format = ArchiveFormat.Unknown;
            IArchiveEngine? engine = null;
            var current = options;
            var listed = await RunAsync(MainText.Opening_Title, name, (_, cancel) =>
            {
                try
                {
                    format = Archives.Detect(full);
                    if (format == ArchiveFormat.Unknown)
                        return new ListResult(OperationResult.Fail(MainText.Open_UnknownFormat), []);
                    engine = Archives.For(format, current);
                    return engine.List(full, cancel);
                }
                catch (OperationCanceledException)
                {
                    return new ListResult(OperationResult.Cancelled(), []);
                }
                catch (Exception ex)
                {
                    return new ListResult(OperationResult.Fail(ex.Message), []);
                }
            });

            if (listed.Result.Status == OperationStatus.PasswordRequired && engine is not null)
            {
                var password = PasswordDialog.Ask(this, name, tried || current?.Password is not null);
                if (password is null)
                {
                    // 암호 입력 취소: 열지 않는다(다시 읽던 중이면 낡은 목록도 남기지 않음).
                    if (sameArchive) CloseArchive();
                    return;
                }
                options = (options ?? new ReadOptions()) with { Password = password };
                tried = true;
                continue;
            }

            if (!listed.Result.Succeeded || engine is null)
            {
                // 다시 읽기에 실패하면 낡은 목록을 남기지 않는다.
                if (sameArchive) CloseArchive();
                if (listed.Result.Status != OperationStatus.Cancelled)
                    MessageDialog.Result(this, string.Format(MainText.OpenNamedFailed_Title, name), listed.Result);
                return;
            }

            if (!sameArchive) SearchBox.Text = "";
            _archive = new ArchiveModel(full, format, engine, options, listed.Entries, listed.Comment);
            _root = FolderNode.Build(_archive);
            _selectedNode = null;
            FolderTree.ItemsSource = new[] { _root };
            _showStart = false;
            _folder = folder ?? "";
            Navigate(folder ?? "", selectName);
            UpdateFind();
            FocusList();
            if (listed.Result.Problems.Count > 0)
                MessageDialog.Show(this, DialogKind.Warning, string.Format(MainText.OpenedWithProblems_Title, name), listed.Result.Message, listed.Result.Problems, [MainText.OK_Button]);
            return;
        }
    }

    private Task ReloadAsync(string? selectName = null) =>
        _archive is null ? Task.CompletedTask : OpenArchiveAsync(_archive.FilePath, _folder, selectName, _archive.Read);

    private void CloseArchive()
    {
        _archive = null; // 엔진·암호도 함께 버린다.
        _root = null;
        _selectedNode = null;
        _view = null;
        _folder = "";
        _showStart = false;
        FolderTree.ItemsSource = null;
        EntryGrid.ItemsSource = null;
        EntryList.ItemsSource = null;
        SearchBox.Text = "";
        PathText.Text = "";
        UpdateUi();
    }

    /// <summary>
    /// 열린 압축 파일에 대한 엔진 작업. PasswordRequired 면 암호를 물어 그 암호로 묶인 엔진으로 다시 시도하고,
    /// 통과한 엔진은 열린 압축 파일에 남긴다(같은 세션에서 다시 묻지 않음). 암호 입력을 취소하면 Cancelled.
    /// </summary>
    private async Task<OperationResult> RunArchiveOperationAsync(ArchiveModel a, string heading, string idleText,
        Func<IArchiveEngine, IProgress<ProgressInfo>, CancellationToken, OperationResult> work)
    {
        var engine = a.Engine;
        var read = a.Read;
        bool tried = read?.Password is not null;
        while (true)
        {
            var current = engine;
            var result = await RunOperationAsync(heading, idleText, (p, c) => work(current, p, c));
            if (result.Status != OperationStatus.PasswordRequired)
            {
                if (!ReferenceEquals(current, a.Engine) && read is not null) a.UsePassword(current, read);
                return result;
            }
            var password = PasswordDialog.Ask(this, a.FileName, tried);
            if (password is null) return OperationResult.Cancelled(MainText.PasswordCancelled);
            read = (a.Read ?? new ReadOptions()) with { Password = password };
            engine = Archives.For(a.Format, read);
            tried = true;
        }
    }

    private void RevealArchive()
    {
        if (_archive is null) return;
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("/select," + _archive.FilePath);
        StartProcess(psi, MainText.ExplorerFailed_Title);
    }

    private void OpenFolderInExplorer(string folder)
    {
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(folder);
        StartProcess(psi, MainText.ExplorerFailed_Title);
    }

    private bool StartProcess(ProcessStartInfo psi, string failHeading)
    {
        try
        {
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageDialog.Error(this, failHeading, ex.Message);
            return false;
        }
    }

    // ───────────────────────── 테스트·풀기 ─────────────────────────

    private async Task TestAsync()
    {
        if (_archive is null) return;
        var a = _archive;
        var result = await RunArchiveOperationAsync(a, MainText.Testing_Title, a.FileName, (e, p, c) => e.Test(a.FilePath, p, c));
        switch (result.Status)
        {
            case OperationStatus.Succeeded when result.Problems.Count == 0:
                MessageDialog.Show(this, DialogKind.Success, MainText.TestOk_Title,
                    string.IsNullOrWhiteSpace(result.Message) ? string.Format(MainText.TestOk_Body, a.FileName, a.FileCount) : result.Message, null, [MainText.OK_Button]);
                break;
            case OperationStatus.Succeeded:
                MessageDialog.Result(this, MainText.TestProblems_Title, result);
                break;
            case OperationStatus.Cancelled:
                ShowNotice(MainText.TestCancelled);
                break;
            default:
                MessageDialog.Result(this, MainText.TestFailed_Title, result);
                break;
        }
    }

    /// <summary>풀기(Ctrl+E): 풀기 대화상자 → 대상·범위·충돌 정책.</summary>
    private async Task ExtractAsync()
    {
        if (_archive is null) return;
        var a = _archive;
        var selected = SelectedRows();
        var dlg = ExtractDialog.Ask(this, a.FilePath, selected.Count, S);
        if (dlg is null) return;
        IReadOnlyCollection<string>? only = dlg.SelectedOnly && selected.Count > 0 ? selected.Select(r => r.Path).ToList() : null;
        await ExtractToAsync(a, dlg.Destination, only, dlg.Conflict, dlg.OpenAfter);
    }

    /// <summary>
    /// 알아서 풀기(OZ-F027): 최상위 항목이 하나면 압축 파일 폴더에 바로, 아니면 압축 파일 이름 폴더(있으면 "이름 (2)")에 푼다.
    /// 충돌 정책·풀고 나서 폴더 열기는 환경 설정 기본값.
    /// </summary>
    private async Task SmartExtractAsync()
    {
        if (_archive is null) return;
        var a = _archive;
        var dir = Path.GetDirectoryName(a.FilePath) ?? "";
        var destination = a.TopLevelCount == 1 ? dir : UniqueFolder(Path.Combine(dir, ExtractDialog.ArchiveBaseName(a.FilePath)));
        await ExtractToAsync(a, destination, null, S.ExtractConflict, S.OpenFolderAfterExtract);
    }

    private static string UniqueFolder(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return path;
        for (int i = 2; ; i++)
        {
            var candidate = $"{path} ({i})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    private async Task ExtractToAsync(ArchiveModel a, string destination, IReadOnlyCollection<string>? only, ConflictChoice choice, bool openAfter)
    {
        ConflictPolicy policy;
        if (choice == ConflictChoice.Ask)
        {
            // 묻기: 대상에 같은 이름이 있으면 먼저 묻는다(기본은 덮어쓰지 않음).
            var conflicts = await RunAsync(MainText.CheckingDestination_Title, destination, (_, cancel) =>
            {
                var hits = new List<string>();
                if (!Directory.Exists(destination)) return hits;
                foreach (var entry in a.FilesUnder(only))
                {
                    cancel.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.Exists(Path.Combine(destination, entry.Path.Replace('/', Path.DirectorySeparatorChar)))) hits.Add(entry.Path);
                    }
                    catch (ArgumentException) { }
                }
                return hits;
            });
            policy = ConflictPolicy.Skip;
            if (conflicts.Count > 0)
            {
                var sample = conflicts.Take(50).ToList();
                if (conflicts.Count > sample.Count) sample.Add(string.Format(MainText.More_Items, conflicts.Count - sample.Count));
                int answer = MessageDialog.Show(this, DialogKind.Question, MainText.Conflict_Title,
                    string.Format(MainText.Conflict_Body, conflicts.Count), sample,
                    [MainText.Replace_Button, MainText.Skip_Button, MainText.Rename_Button, MainText.Cancel_Button], defaultIndex: 1, cancelIndex: 3);
                switch (answer)
                {
                    case 0: policy = ConflictPolicy.Overwrite; break;
                    case 1: policy = ConflictPolicy.Skip; break;
                    case 2: policy = ConflictPolicy.Rename; break;
                    default: return;
                }
            }
        }
        else
        {
            policy = choice switch
            {
                ConflictChoice.Overwrite => ConflictPolicy.Overwrite,
                ConflictChoice.Rename => ConflictPolicy.Rename,
                _ => ConflictPolicy.Skip,
            };
        }

        var options = new ExtractOptions { Conflict = policy, Only = only };
        var result = await RunArchiveOperationAsync(a, MainText.Extracting_Title, a.FileName, (e, p, c) => e.Extract(a.FilePath, destination, options, p, c));
        switch (result.Status)
        {
            case OperationStatus.Succeeded:
                S.LastExtractFolder = destination;
                SaveSettings();
                if (openAfter && result.Problems.Count == 0)
                {
                    OpenFolderInExplorer(destination);
                    break;
                }
                int open = MessageDialog.Show(this, result.Problems.Count > 0 ? DialogKind.Warning : DialogKind.Success,
                    result.Problems.Count > 0 ? MainText.ExtractProblems_Title : MainText.ExtractDone_Title,
                    string.IsNullOrWhiteSpace(result.Message) ? destination : $"{result.Message}\n{destination}",
                    result.Problems, [MainText.OpenFolder_Button, MainText.Close_Button]);
                if (open == 0 || openAfter) OpenFolderInExplorer(destination);
                break;
            case OperationStatus.Cancelled:
                ShowNotice(string.Format(MainText.ExtractCancelled, destination));
                break;
            default:
                MessageDialog.Result(this, MainText.ExtractFailed_Title, result);
                break;
        }
    }

    // ───────────────────────── 새 압축 ─────────────────────────

    private async Task CreateAsync()
    {
        var pick = new OpenFileDialog
        {
            Title = MainText.PickFilesToArchive_Title,
            Multiselect = true,
            Filter = MainText.Filter_AllFiles,
            CheckFileExists = true,
        };
        if (pick.ShowDialog(this) != true || pick.FileNames.Length == 0) return;
        var dlg = CreateArchiveDialog.Ask(this, pick.FileNames, S);
        if (dlg is null) return;

        var target = dlg.Target;
        var sources = dlg.Sources.ToList();
        var options = dlg.Options;
        var engine = Archives.For(options.Format);
        bool replace = dlg.ReplaceExisting && File.Exists(target);
        if (replace && options.VolumeSize is not null)
        {
            MessageDialog.Error(this, MainText.CreateBlocked_Title, MainText.Create_SplitReplace);
            return;
        }

        // 덮어쓰기를 허락받았어도 새 압축이 성공할 때까지 기존 파일을 지우지 않는다.
        var ext = Archives.DefaultExtension(options.Format);
        var work = replace
            ? Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp{ext}")
            : target;
        var result = await RunOperationAsync(MainText.Creating_Title, Path.GetFileName(target), (p, c) => engine.Create(work, sources, options, p, c));
        if (result.Succeeded && replace)
        {
            try
            {
                File.Move(work, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result = OperationResult.Fail(MainText.Create_MoveFailed, ex.Message, work);
            }
        }
        else if (!result.Succeeded && replace && File.Exists(work))
        {
            try { File.Delete(work); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        switch (result.Status)
        {
            case OperationStatus.Succeeded:
                if (result.Problems.Count > 0) MessageDialog.Result(this, MainText.CreateProblems_Title, result);
                // 7Z 분할은 name.7z.001… 로 만들어진다.
                var open = !File.Exists(target) && File.Exists(target + ".001") ? target + ".001" : target;
                await OpenArchiveAsync(open, read: string.IsNullOrEmpty(options.Password) ? null : new ReadOptions { Password = options.Password });
                break;
            case OperationStatus.Cancelled:
            {
                var leftovers = new[] { work, target }.Distinct().Where(p => replace ? p == work && File.Exists(p) : File.Exists(p)).ToList();
                if (leftovers.Count == 0)
                    ShowNotice(MainText.CreateCancelled + (replace ? MainText.CreateCancelled_ExistingKept : ""));
                else
                    MessageDialog.Show(this, DialogKind.Warning, MainText.CreateLeftovers_Title,
                        MainText.CreateLeftovers_Body, leftovers, [MainText.OK_Button]);
                break;
            }
            case OperationStatus.NotSupported:
                MessageDialog.Result(this, MainText.CreateNotSupported_Title, result);
                break;
            default:
                MessageDialog.Result(this, MainText.CreateFailed_Title, result);
                break;
        }
    }

    // ───────────────────────── 항목 명령(수정) ─────────────────────────

    private async Task AddAsync()
    {
        if (_archive is not { Engine.Capabilities.Modify: true }) return;
        var pick = new OpenFileDialog { Title = MainText.PickFilesToAdd_Title, Multiselect = true, Filter = MainText.Filter_AllFiles, CheckFileExists = true };
        if (pick.ShowDialog(this) != true || pick.FileNames.Length == 0) return;
        await AddSourcesAsync(pick.FileNames);
    }

    /// <summary>파일 추가(Ins)·붙여 넣기(Ctrl+V) 공통: 현재 폴더에 engine.Add.</summary>
    private async Task AddSourcesAsync(IReadOnlyList<string> sources)
    {
        if (_archive is not { Engine.Capabilities.Modify: true } a) return;
        if (sources.Any(s => string.Equals(Path.GetFullPath(s), a.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            MessageDialog.Error(this, MainText.AddBlocked_Title, MainText.Add_Self);
            return;
        }
        var folder = _folder;
        var where = folder.Length == 0 ? MainText.Add_TopLevel : folder;
        var before = Snapshot(a.FilePath);
        var result = await RunArchiveOperationAsync(a, MainText.Adding_Title, string.Format(MainText.Adding_Detail, sources.Count, where), (e, p, c) => e.Add(a.FilePath, sources, folder, p, c));
        await ReportModifyAsync(a, before, result, MainText.AddFailed_Title, MainText.AddCancelled_Title, Path.GetFileName(sources[0].TrimEnd(Path.DirectorySeparatorChar)));
    }

    private async Task DeleteAsync()
    {
        if (_archive is not { Engine.Capabilities.Modify: true } a) return;
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        if (S.ConfirmDelete)
        {
            var names = rows.Take(50).Select(r => r.IsDirectory ? r.Name + "/" : r.Name).ToList();
            if (rows.Count > names.Count) names.Add(string.Format(MainText.More_Items, rows.Count - names.Count));
            int answer = MessageDialog.Show(this, DialogKind.Question, MainText.DeleteConfirm_Title,
                string.Format(MainText.DeleteConfirm_Body, rows.Count, a.FileName), names,
                [MainText.Delete_Button, MainText.Cancel_Button], defaultIndex: 0, cancelIndex: 1);
            if (answer != 0) return;
        }
        var paths = rows.Select(r => r.Path).ToList();
        var before = Snapshot(a.FilePath);
        var result = await RunArchiveOperationAsync(a, MainText.Deleting_Title, string.Format(MainText.ItemCount, paths.Count), (e, p, c) => e.Delete(a.FilePath, paths, p, c));
        await ReportModifyAsync(a, before, result, MainText.DeleteFailed_Title, MainText.DeleteCancelled_Title, null);
    }

    private async Task RenameAsync()
    {
        if (_archive is not { Engine.Capabilities.Modify: true } a) return;
        var rows = SelectedRows();
        if (rows.Count != 1) return;
        var row = rows[0];
        var siblings = a.RowsIn(_folder).Where(r => r.Path != row.Path).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalid = Path.GetInvalidFileNameChars();
        var newName = InputDialog.Ask(this, MainText.Rename_Title, string.Format(MainText.Rename_Prompt, row.Name), row.Name, value =>
        {
            if (value.Length == 0) return MainText.Rename_Empty;
            if (value is "." or "..") return MainText.Rename_Dots;
            if (value.IndexOfAny(invalid) >= 0 || value.Contains('/')) return MainText.Rename_InvalidChars;
            if (value != row.Name && siblings.Contains(value)) return MainText.Rename_Duplicate;
            return null;
        }, okLabel: MainText.Rename_Button);
        if (newName is null || newName == row.Name) return;
        var before = Snapshot(a.FilePath);
        var result = await RunArchiveOperationAsync(a, MainText.Renaming_Title, $"{row.Name} → {newName}", (e, p, c) => e.Rename(a.FilePath, row.Path, newName, p, c));
        await ReportModifyAsync(a, before, result, MainText.RenameFailed_Title, MainText.RenameCancelled_Title, newName);
    }

    /// <summary>압축 파일 설명 편집(Ctrl+Shift+M) → engine.SetComment.</summary>
    private async Task EditCommentAsync()
    {
        if (_archive is not { Engine.Capabilities.Comment: true } a) return;
        var text = SimpleDialogs.Comment(this, a.FileName, a.Comment);
        if (text is null || text == (a.Comment ?? "")) return;
        var before = Snapshot(a.FilePath);
        var comment = text.Length == 0 ? null : text;
        var result = await RunArchiveOperationAsync(a, MainText.SavingComment_Title, a.FileName, (e, _, c) => e.SetComment(a.FilePath, comment, c));
        await ReportModifyAsync(a, before, result, result.Status == OperationStatus.NotSupported ? MainText.CommentNotSupported_Title : MainText.CommentFailed_Title,
            MainText.CommentCancelled_Title, null);
    }

    private static (long Length, DateTime Written)? Snapshot(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>수정 작업 뒤: 결과를 알리고 목록을 다시 읽는다(실패해도 실제 상태를 다시 보여 준다).</summary>
    private async Task ReportModifyAsync(ArchiveModel a, (long Length, DateTime Written)? before, OperationResult result,
        string failHeading, string cancelHeading, string? selectName)
    {
        switch (result.Status)
        {
            case OperationStatus.Succeeded:
                if (result.Problems.Count > 0) MessageDialog.Result(this, MainText.ModifyProblems_Title, result);
                break;
            case OperationStatus.Cancelled:
            {
                // 취소 뒤 실제로 압축 파일이 그대로인지 크기·수정 시각으로 확인해 알려 준다.
                var after = Snapshot(a.FilePath);
                if (before is not null && after == before)
                    ShowNotice(string.Format(MainText.Modify_CancelledUnchanged, cancelHeading));
                else
                    MessageDialog.Show(this, DialogKind.Warning, cancelHeading,
                        string.Format(MainText.Modify_CancelledChanged, result.Message), null, [MainText.OK_Button]);
                break;
            }
            default:
                MessageDialog.Result(this, failHeading, result);
                break;
        }
        if (ReferenceEquals(_archive, a)) await ReloadAsync(result.Succeeded ? selectName : null);
    }

    // ───────────────────────── 클립보드 ─────────────────────────

    /// <summary>클립보드로 복사(Ctrl+C): 선택 항목을 세션 임시 폴더에 엔진으로 풀고(한도·경로 검사 동일) 파일 목록을 클립보드에 둔다.</summary>
    private async Task CopyEntriesAsync()
    {
        if (_archive is not { Engine.Capabilities.Extract: true } a) return;
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        string destination;
        try
        {
            _clipboardTemp ??= Path.Combine(Path.GetTempPath(), "ohmyZip", $"clipboard-{Environment.ProcessId}-{Guid.NewGuid():N}");
            destination = Path.Combine(_clipboardTemp, (++_clipboardSeq).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Error(this, MainText.ClipboardCopyFailed_Title, string.Format(MainText.Clipboard_TempFailed, ex.Message));
            return;
        }
        var options = new ExtractOptions { Conflict = ConflictPolicy.Overwrite, Only = rows.Select(r => r.Path).ToList() };
        var result = await RunArchiveOperationAsync(a, MainText.CopyingToClipboard_Title, string.Format(MainText.ItemCount, rows.Count), (e, p, c) => e.Extract(a.FilePath, destination, options, p, c));
        if (!result.Succeeded)
        {
            TryDeleteDirectory(destination);
            if (result.Status == OperationStatus.Cancelled) ShowNotice(MainText.ClipboardCopyCancelled);
            else MessageDialog.Result(this, MainText.ClipboardCopyFailed_Title, result);
            return;
        }
        var files = new StringCollection();
        foreach (var r in rows)
        {
            var path = Path.Combine(destination, r.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) || Directory.Exists(path)) files.Add(path);
        }
        if (files.Count == 0)
        {
            MessageDialog.Result(this, MainText.ClipboardNothing_Title, result);
            return;
        }
        try
        {
            Clipboard.SetFileDropList(files);
        }
        catch (ExternalException ex)
        {
            MessageDialog.Error(this, MainText.ClipboardWrite_Title, ex.Message);
            return;
        }
        if (result.Problems.Count > 0) MessageDialog.Result(this, MainText.CopyProblems_Title, result);
        RefreshCommandState();
    }

    /// <summary>클립보드에서 붙여 넣기(Ctrl+V): 파일 목록을 현재 폴더에 추가(Ins 와 같은 경로).</summary>
    private async Task PasteAsync()
    {
        string[] files;
        try
        {
            files = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : [];
        }
        catch (ExternalException ex)
        {
            MessageDialog.Error(this, MainText.ClipboardRead_Title, ex.Message);
            return;
        }
        if (files.Length == 0)
        {
            ShowNotice(MainText.Clipboard_NoFiles);
            return;
        }
        await AddSourcesAsync(files);
    }

    /// <summary>파일의 경로명을 클립보드로: '압축 파일 경로\항목 경로' 한 줄씩.</summary>
    private void CopyEntryPaths()
    {
        if (_archive is null) return;
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        var text = string.Join("\r\n", rows.Select(r => _archive.FilePath + Path.DirectorySeparatorChar + r.Path.Replace('/', Path.DirectorySeparatorChar)));
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex)
        {
            MessageDialog.Error(this, MainText.ClipboardWrite_Title, ex.Message);
        }
    }

    private void CleanupClipboardTemp()
    {
        if (_clipboardTemp is not null) TryDeleteDirectory(_clipboardTemp);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ───────────────────────── 압축 파일 파일 작업 ─────────────────────────

    /// <summary>다른 이름으로 저장: 압축 파일 바이트를 그대로 복사(임시 파일 → 이동). 같은 경로는 거부.</summary>
    private async Task SaveAsAsync()
    {
        if (_archive is null) return;
        var a = _archive;
        var ext = Path.GetExtension(a.FilePath);
        var save = new SaveFileDialog
        {
            Title = MainText.SaveAs_Title,
            Filter = ext.Length > 0 ? string.Format(MainText.Filter_SaveAs, ext.TrimStart('.').ToUpperInvariant(), ext) : MainText.Filter_AllFiles,
            DefaultExt = ext,
            AddExtension = ext.Length > 0,
            OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(a.FilePath),
            FileName = Path.GetFileNameWithoutExtension(a.FilePath) + MainText.SaveAs_CopySuffix + ext,
        };
        if (save.ShowDialog(this) != true) return;
        var target = Path.GetFullPath(save.FileName);
        if (string.Equals(target, a.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            MessageDialog.Error(this, MainText.SaveBlocked_Title, MainText.SaveAs_SamePath);
            return;
        }
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        var source = a.FilePath;
        var result = await RunOperationAsync(MainText.SavingAs_Title, Path.GetFileName(target), (p, c) =>
        {
            try
            {
                CopyWithProgress(source, temp, p, c);
                File.Move(temp, target, overwrite: true);
                return OperationResult.Ok();
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        });
        switch (result.Status)
        {
            case OperationStatus.Succeeded:
                ShowNotice(string.Format(MainText.SaveAs_Done, target));
                break;
            case OperationStatus.Cancelled:
                ShowNotice(MainText.SaveAs_Cancelled);
                break;
            default:
                MessageDialog.Result(this, MainText.SaveAsFailed_Title, result);
                break;
        }
    }

    private static void CopyWithProgress(string source, string destination, IProgress<ProgressInfo> progress, CancellationToken cancel)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        long total = input.Length, done = 0;
        var buffer = new byte[1 << 20];
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            output.Write(buffer, 0, n);
            done += n;
            progress.Report(new ProgressInfo(done, total, Path.GetFileName(source)));
        }
    }

    /// <summary>압축 파일 삭제(Ctrl+Del): 확인 → 닫기 → 휴지통(SHFileOperation FOF_ALLOWUNDO).</summary>
    private void DeleteArchiveFile()
    {
        if (_archive is null) return;
        var path = _archive.FilePath;
        int answer = MessageDialog.Show(this, DialogKind.Question, MainText.DeleteArchive_Title,
            string.Format(MainText.DeleteArchive_Body, path), null, [MainText.MoveToRecycleBin_Button, MainText.Cancel_Button], defaultIndex: 0, cancelIndex: 1);
        if (answer != 0) return;
        CloseArchive();
        var error = NativeMethods.MoveToRecycleBin(this, path);
        if (error is null) ShowNotice(string.Format(MainText.RecycleDone, path));
        else MessageDialog.Error(this, MainText.DeleteArchiveFailed_Title, $"{error}\n{path}");
    }

    private void StartNewWindow()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            MessageDialog.Error(this, MainText.NewWindowFailed_Title, MainText.ExePathUnknown);
            return;
        }
        StartProcess(new ProcessStartInfo(exe) { UseShellExecute = false }, MainText.NewWindowFailed_Title);
    }

    /// <summary>
    /// 도움말: 실행 파일 옆 도움말 문서(한국어 화면이면 HELP.md, 그 밖은 HELP.en.md)를 연결된 프로그램으로,
    /// 연결이 없으면 메모장으로 연다.
    /// </summary>
    private void OpenHelp()
    {
        var fileName = UiLanguage.Current.TwoLetterISOLanguageName == "ko" ? "HELP.md" : "HELP.en.md";
        var help = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(help))
        {
            MessageDialog.Error(this, MainText.HelpMissing_Title, string.Format(MainText.HelpMissing_Body, fileName, help));
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(help) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(help);
            StartProcess(psi, MainText.HelpOpenFailed_Title);
        }
    }
}
