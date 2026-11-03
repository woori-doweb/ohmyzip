using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OhMyZip.Core;

/// <summary>파일 내용을 한 번 덮어쓴 뒤 이름을 바꾸고 삭제하는 서비스.</summary>
public static class SecureDelete
{
    private const int BufferSize = 81920;
    private const string SsdCaveat = "SSD 에서는 완전 삭제를 보장할 수 없음";
    private const string PartialOverwriteWarning = "내용이 일부 덮어써졌을 수 있습니다";
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>
    /// 일반 파일만 한 번 무작위 바이트로 덮어쓰고, 같은 폴더의 무작위 이름으로 옮긴 뒤 삭제한다.
    /// SSD·플래시 저장장치의 웨어 레벨링 때문에 물리적 완전 삭제는 보장하지 않는다.
    /// </summary>
    public static OperationResult Shred(string filePath, CancellationToken cancel = default)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Fail("삭제할 파일 경로가 올바르지 않습니다", ex.Message);
        }

        // This check deliberately precedes opening the file: a pre-cancelled request must not
        // even open the target, and therefore must preserve its bytes.
        if (cancel.IsCancellationRequested)
            return OperationResult.Cancelled();
        if (!OperatingSystem.IsWindows())
            return OperationResult.Unsupported("완전 삭제 서비스는 Windows에서만 지원합니다");

        FileAttributes attributes;
        try
        {
            if (Directory.Exists(fullPath))
                return OperationResult.Fail("폴더는 완전 삭제할 수 없습니다");
            if (!File.Exists(fullPath))
                return OperationResult.Fail("삭제할 파일이 없습니다");
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return OperationResult.Fail("삭제할 파일을 확인할 수 없습니다", ex.Message);
        }

        if ((attributes & FileAttributes.Directory) != 0)
            return OperationResult.Fail("폴더는 완전 삭제할 수 없습니다");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return OperationResult.Fail("재분석 지점(링크)은 완전 삭제하지 않습니다");
        if ((attributes & FileAttributes.ReadOnly) != 0)
            return OperationResult.Fail("읽기 전용 파일은 완전 삭제하지 않습니다");

        string renamedPath;
        bool overwriteStarted = false;
        try
        {
            string folder = Path.GetDirectoryName(fullPath)!;
            do
            {
                renamedPath = Path.Combine(folder, ".ohmyZip-delete-" + Guid.NewGuid().ToString("N"));
            }
            while (File.Exists(renamedPath) || Directory.Exists(renamedPath));

            using (var input = OpenForOverwrite(fullPath))
            {
                OperationResult? handleCheck = ValidateOpenedHandle(input);
                if (handleCheck is not null)
                    return handleCheck;

                long length = input.Length;
                byte[] random = new byte[BufferSize];
                long remaining = length;
                while (remaining > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(random.Length, remaining);
                    RandomNumberGenerator.Fill(random.AsSpan(0, count));
                    // Mark before the write: an I/O exception can occur after the device has
                    // accepted only part of a requested buffer.
                    overwriteStarted = true;
                    input.Write(random, 0, count);
                    remaining -= count;
                }
                input.Flush(flushToDisk: true);

                if (cancel.IsCancellationRequested)
                    return CancelledAfterOverwrite(overwriteStarted);

                if (!TryRenameByHandle(input.SafeFileHandle, renamedPath, out string renameError))
                {
                    // Do not close the validated handle and then race a path-based move.
                    return OperationResult.Fail("파일 이름을 바꾸지 못해 완전 삭제하지 않았습니다",
                        renameError, PartialOverwriteWarning);
                }
                if (!TryDeleteByHandle(input.SafeFileHandle, out string deleteError))
                {
                    return OperationResult.Fail("파일 이름을 바꾼 뒤 삭제하지 못했습니다", deleteError,
                        PartialOverwriteWarning, $"이름이 바뀐 경로: {renamedPath}");
                }
            }

            return OperationResult.Ok("파일을 덮어쓰고 삭제했습니다. " + SsdCaveat);
        }
        catch (OperationCanceledException)
        {
            return CancelledAfterOverwrite(overwriteStarted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return overwriteStarted
                ? OperationResult.Fail("파일을 완전히 삭제하지 못했습니다 — " + PartialOverwriteWarning, ex.Message)
                : OperationResult.Fail("파일을 완전히 삭제하지 못했습니다", ex.Message);
        }
    }

    private static OperationResult? ValidateOpenedHandle(FileStream input)
    {
        try
        {
            if (!GetFileInformationByHandle(input.SafeFileHandle, out ByHandleFileInformation information))
                return OperationResult.Fail("삭제 대상 파일 정보를 확인하지 못했습니다", LastErrorMessage());
            if ((information.FileAttributes & FileAttributes.Directory) != 0)
                return OperationResult.Fail("폴더는 완전 삭제할 수 없습니다");
            if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
                return OperationResult.Fail("재분석 지점(링크)은 완전 삭제하지 않습니다");
            if ((information.FileAttributes & FileAttributes.ReadOnly) != 0)
                return OperationResult.Fail("읽기 전용 파일은 완전 삭제하지 않습니다");
            if (information.NumberOfLinks > 1)
                return OperationResult.Fail("하드 링크가 여러 개인 파일은 완전 삭제하지 않습니다");
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return OperationResult.Fail("삭제 대상 파일 정보를 확인하지 못했습니다", ex.Message);
        }
    }

    private static FileStream OpenForOverwrite(string path)
    {
        SafeFileHandle handle = CreateFile(
            path,
            GenericRead | GenericWrite | DeleteAccess,
            shareMode: 0,
            securityAttributes: IntPtr.Zero,
            OpenExisting,
            FileFlagWriteThrough | FileFlagSequentialScan | FileFlagOpenReparsePoint,
            templateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("삭제할 파일을 열 수 없습니다", new System.ComponentModel.Win32Exception(error));
        }
        return new FileStream(handle, FileAccess.ReadWrite, BufferSize, isAsync: false);
    }

    private static bool TryRenameByHandle(SafeFileHandle handle, string target, out string error)
    {
        error = "";
        byte[] name = Encoding.Unicode.GetBytes(target);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        int size = checked(nameOffset + name.Length);
        IntPtr info = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, info, size);
            Marshal.WriteByte(info, 0, 0); // ReplaceIfExists = false
            Marshal.WriteIntPtr(info, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(info, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(info, nameOffset), name.Length);
            if (SetFileInformationByHandle(handle, FileRenameInfo, info, (uint)size))
                return true;
            error = LastErrorMessage();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    private static bool TryDeleteByHandle(SafeFileHandle handle, out string error)
    {
        error = "";
        IntPtr info = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.Copy(new byte[1], 0, info, 1);
            Marshal.WriteByte(info, 0, 1); // DeleteFile = true
            if (SetFileInformationByHandle(handle, FileDispositionInfo, info, 1))
                return true;
            error = LastErrorMessage();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    private static string LastErrorMessage() =>
        new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

    private static OperationResult CancelledAfterOverwrite(bool overwriteStarted) =>
        overwriteStarted
            ? OperationResult.Cancelled("취소됨 — " + PartialOverwriteWarning)
            : OperationResult.Cancelled();
}
