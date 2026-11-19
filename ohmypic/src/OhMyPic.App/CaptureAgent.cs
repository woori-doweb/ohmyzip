using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using OhMyPic.App.Dialogs;
using OhMyPic.App.Editor;
using OhMyPic.App.Text;
using OhMyPic.Core;
using OhMyPic.Core.Editing;
using static OhMyPic.App.NativeMethods;

namespace OhMyPic.App;

/// <summary>
/// Resident Print Screen capture (<c>ohMypic.exe --capture-agent</c>, started at sign-in from the HKCU Run value).
/// One instance per session. A low-level keyboard hook takes unmodified Print Screen before Windows does, the monitor
/// under the cursor is copied, and the capture opens in the editor. The only visible part is a notification area icon
/// (capture now / turn off). Other processes stop it through a named event (<see cref="SignalQuit"/>).
/// </summary>
internal sealed class CaptureAgent : IDisposable
{
    private const string MutexName = @"Local\ohMypic.CaptureAgent";
    private const string QuitEventName = @"Local\ohMypic.CaptureAgent.Quit";
    private const uint CaptureMessage = WM_APP + 1;
    private const int TrayCallbackMessage = WM_APP + 2;
    private const uint TrayIconId = 1;

    /// <summary>An agent that was just told to quit (turn off, then on again) may still hold the mutex this long.</summary>
    private static readonly TimeSpan PreviousAgentWait = TimeSpan.FromSeconds(3);

    /// <summary>Lets the tray menu disappear before the screen is copied.</summary>
    private static readonly TimeSpan MenuCloseDelay = TimeSpan.FromMilliseconds(250);

    private readonly Application _app;
    private readonly Dispatcher _dispatcher;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _quitEvent;

    /// <summary>Kept in a field: the hook holds only a native pointer to it, so a collected delegate would crash.</summary>
    private readonly LowLevelKeyboardProc _hookProc;

    private RegisteredWaitHandle? _quitWait;
    private HwndSource? _trayWindow;
    private ContextMenu? _trayMenu;
    private IntPtr _iconHandle;
    private bool _ownsIcon;
    private uint _taskbarCreatedMessage;
    private bool _disposed;

    // Hook thread state. _hookThreadId and _hook are written before the start handshake and only read afterwards.
    private uint _hookThreadId;
    private IntPtr _hook;
    private int _hookError;
    private bool _printScreenDown;

    private CaptureAgent(Application app, Mutex mutex)
    {
        _app = app;
        _dispatcher = app.Dispatcher;
        _mutex = mutex;
        _hookProc = OnKeyboardHook;
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
    }

    /// <summary>
    /// Starts the agent on the UI thread. Null when another agent already runs in this session. Throws when the hook,
    /// the tray window or the named objects cannot be created.
    /// </summary>
    internal static CaptureAgent? TryStart(Application app)
    {
        var mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(PreviousAgentWait);
        }
        catch (AbandonedMutexException)
        {
            // The previous agent ended without releasing it; the mutex is now ours.
            owned = true;
        }
        if (!owned)
        {
            mutex.Dispose();
            return null;
        }
        CaptureAgent? agent = null;
        try
        {
            agent = new CaptureAgent(app, mutex);
            agent.Start();
            return agent;
        }
        catch
        {
            if (agent is not null) agent.Dispose();
            else
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
            throw;
        }
    }

    /// <summary>Asks the running agent (if any) to remove its icon and exit.</summary>
    internal static void SignalQuit()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quit)) return;
        using (quit) quit.Set();
    }

    private void Start()
    {
        using (var ready = new ManualResetEventSlim())
        {
            // Own thread with its own message loop: a busy UI thread (decoding, a modal dialog) must never delay the
            // hook, because Windows silently removes a low-level hook that times out.
            var thread = new Thread(() => RunHookThread(ready)) { IsBackground = true, Name = "ohMypic capture hook" };
            thread.Start();
            ready.Wait();
        }
        if (_hook == IntPtr.Zero) throw new Win32Exception(_hookError);

        _trayMenu = CreateTrayMenu();
        _iconHandle = LoadSmallIcon(SystemIntegration.ExePath, out _ownsIcon);
        // A hidden top-level window, not a message-only one: only top-level windows get the TaskbarCreated broadcast.
        var parameters = new HwndSourceParameters(DialogText.App_Name)
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
            PositionX = 0,
            PositionY = 0,
            Width = 0,
            Height = 0,
        };
        _trayWindow = new HwndSource(parameters);
        _trayWindow.AddHook(TrayWindowProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        AddTrayIcon();

        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => _dispatcher.InvokeAsync(Quit), null,
            Timeout.Infinite, executeOnlyOnce: true);
        // Session end and any other shutdown path still remove the icon.
        _app.Exit += (_, _) => Dispose();
    }

    // ───────────── Hook thread ─────────────

    private void RunHookThread(ManualResetEventSlim ready)
    {
        // Creates this thread's message queue before anyone can post a capture request to it.
        PeekMessage(out _, IntPtr.Zero, WM_USER, WM_USER, PM_NOREMOVE);
        _hookThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) _hookError = Marshal.GetLastWin32Error();
        ready.Set();
        if (_hook == IntPtr.Zero) return;
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == CaptureMessage) CaptureAndOpen();
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
        }
    }

    /// <summary>
    /// Swallows Print Screen without Ctrl, Alt, Shift or Windows (so the Windows capture tool does not start) and
    /// queues one capture per press; everything else goes on unchanged. No other work here: the call is on the
    /// input path of every key press in the session.
    /// </summary>
    private IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Marshal.ReadInt32(lParam) == VK_SNAPSHOT)
        {
            int message = (int)wParam;
            if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                // Auto-repeat of a press already taken.
                if (_printScreenDown) return (IntPtr)1;
                if (!IsModifierDown())
                {
                    _printScreenDown = true;
                    PostThreadMessage(_hookThreadId, CaptureMessage, IntPtr.Zero, IntPtr.Zero);
                    return (IntPtr)1;
                }
            }
            else if ((message is WM_KEYUP or WM_SYSKEYUP) && _printScreenDown)
            {
                // The release of a swallowed press is swallowed too, even if a modifier went down meanwhile.
                _printScreenDown = false;
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsModifierDown() =>
        GetAsyncKeyState(VK_CONTROL) < 0 || GetAsyncKeyState(VK_MENU) < 0 || GetAsyncKeyState(VK_SHIFT) < 0
        || GetAsyncKeyState(VK_LWIN) < 0 || GetAsyncKeyState(VK_RWIN) < 0;

    /// <summary>
    /// Copies the screen right here, before any window of ours can appear, then hands the pixels to the UI thread.
    /// Nothing may escape: an exception on this thread would end the process and the hook with it.
    /// </summary>
    private void CaptureAndOpen()
    {
        DateTime capturedAt = DateTime.Now;
        RasterImage image;
        try
        {
            image = ScreenCapture.CaptureMonitorUnderCursor();
        }
        catch (Exception ex)
        {
            string reason = ex.Message;
            _dispatcher.InvokeAsync(() => ShowCaptureError(reason));
            return;
        }
        _dispatcher.InvokeAsync(() => OpenEditor(image, capturedAt));
    }

    // ───────────── UI thread ─────────────

    private void OpenEditor(RasterImage image, DateTime capturedAt)
    {
        if (_disposed) return;
        try
        {
            var window = EditorWindow.OpenCapture(image, capturedAt);
            BringToForeground(window);
        }
        catch (ImageOperationException ex) when (ex.Code == EditErrorCode.TooLarge)
        {
            // OpenCapture has already explained it.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowCaptureError(ex.Message);
        }
    }

    private void ShowCaptureError(string reason)
    {
        if (!_disposed) MessageDialog.Error(null, DialogText.Capture_Failed_Title, reason);
    }

    private ContextMenu CreateTrayMenu()
    {
        var capture = new MenuItem { Header = DialogText.Capture_Menu_Capture };
        AutomationProperties.SetAutomationId(capture, "OP-Capture-Menu-Capture");
        capture.Click += (_, _) => CaptureAfterDelay();
        var turnOff = new MenuItem { Header = DialogText.Capture_Menu_TurnOff };
        AutomationProperties.SetAutomationId(turnOff, "OP-Capture-Menu-TurnOff");
        turnOff.Click += (_, _) => TurnOff();
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        AutomationProperties.SetAutomationId(menu, "OP-Capture-Menu");
        AutomationProperties.SetName(menu, DialogText.Capture_TrayTip);
        menu.Items.Add(capture);
        menu.Items.Add(turnOff);
        return menu;
    }

    private NOTIFYICONDATA TrayIconData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _trayWindow!.Handle,
        uID = TrayIconId,
        uFlags = flags,
        uCallbackMessage = TrayCallbackMessage,
        hIcon = _iconHandle,
        szTip = DialogText.Capture_TrayTip,
    };

    /// <summary>
    /// Adds the icon. Failure is not an error: at sign-in the taskbar may not exist yet, and its TaskbarCreated
    /// broadcast adds the icon again (also after Explorer restarts).
    /// </summary>
    private void AddTrayIcon()
    {
        var data = TrayIconData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIcon(NIM_ADD, ref data)) return;
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private IntPtr TrayWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TrayCallbackMessage)
        {
            // NOTIFYICON_VERSION_4: the event is the low word of lParam.
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_CONTEXTMENU:
                    ShowTrayMenu();
                    break;
                case WM_LBUTTONDBLCLK:
                    CaptureAfterDelay();
                    break;
            }
            handled = true;
        }
        else if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            AddTrayIcon();
        }
        return IntPtr.Zero;
    }

    private void ShowTrayMenu()
    {
        if (_trayMenu is null || _trayWindow is null) return;
        // The shell lets the icon's window take the foreground; without it the menu would not close on an outside click.
        SetForegroundWindow(_trayWindow.Handle);
        _trayMenu.IsOpen = true;
    }

    private async void CaptureAfterDelay()
    {
        await Task.Delay(MenuCloseDelay);
        if (!_disposed) PostThreadMessage(_hookThreadId, CaptureMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void TurnOff()
    {
        try
        {
            SystemIntegration.DisableCapture();
        }
        catch (Exception ex) when (SystemIntegration.IsRegistryError(ex))
        {
            // Still registered: keep running so the icon matches what happens at the next sign-in.
            MessageDialog.Error(null, DialogText.Pref_Capture_DisableFailed_Title, ex.Message);
            return;
        }
        Quit();
    }

    private void Quit()
    {
        if (_disposed) return;
        Dispose();
        _app.Shutdown(0);
    }

    /// <summary>Removes the icon, ends the hook thread (which unhooks) and frees the single-instance mutex. UI thread only.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _quitWait?.Unregister(null);
        if (_trayMenu is not null) _trayMenu.IsOpen = false;
        if (_trayWindow is not null)
        {
            var data = TrayIconData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _trayWindow.RemoveHook(TrayWindowProc);
            _trayWindow.Dispose();
        }
        if (_ownsIcon) DestroyIcon(_iconHandle);
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _quitEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
