// 탐색기 하위 메뉴 명령 처리기(IExplorerCommand). 정적 verb 의 ExplorerCommandHandler 값으로 불린다.
// 처리기는 앱과 명령을 모른다. 받은 CLSID 의 HKCU\Software\Classes\CLSID\{clsid}\Command 값
// Exe(전체 경로)·Verb·Title 을 읽고, 고른 항목 전체를 목록 파일 하나에 적어
// "<Exe>" <Verb> --list "<목록 파일>" 로 앱을 한 번 실행한다.
// 탐색기 안에서 돌므로 C++ 예외와 전역 상태(참조 수 제외) 없이 모든 경로에서 HRESULT 를 돌려준다.

#include <windows.h>
#include <objbase.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <strsafe.h>

namespace {

// 살아 있는 개체 수 + LockServer 수. 0 이면 DllCanUnloadNow 가 S_OK.
LONG g_moduleRefs = 0;

void* HeapAllocBytes(SIZE_T bytes) { return HeapAlloc(GetProcessHeap(), 0, bytes); }

void HeapFreeBytes(void* p)
{
    if (p != nullptr) HeapFree(GetProcessHeap(), 0, p);
}

// 클래스 전용 operator new 가 noexcept 이므로 new 식은 할당 실패 때 생성자를 부르지 않고 nullptr 을 돌려준다.
struct HeapObject {
    static void* operator new(size_t bytes) noexcept { return HeapAllocBytes(bytes); }
    static void operator delete(void* p) noexcept { HeapFreeBytes(p); }
};

// HKCU 의 REG_SZ 값을 힙 문자열로 읽는다. 값이 없거나 비어 있으면 실패.
HRESULT ReadString(PCWSTR subKey, PCWSTR name, PWSTR* value)
{
    *value = nullptr;
    DWORD bytes = 0;
    LSTATUS status = RegGetValueW(HKEY_CURRENT_USER, subKey, name, RRF_RT_REG_SZ, nullptr, nullptr, &bytes);
    // 두 번 읽는 사이에 값이 커질 수 있으므로 몇 번 다시 시도한다.
    for (int attempt = 0; attempt < 3 && status == ERROR_SUCCESS; ++attempt) {
        PWSTR buffer = static_cast<PWSTR>(HeapAllocBytes(bytes));
        if (buffer == nullptr) return E_OUTOFMEMORY;
        DWORD read = bytes;
        status = RegGetValueW(HKEY_CURRENT_USER, subKey, name, RRF_RT_REG_SZ, nullptr, buffer, &read);
        if (status == ERROR_SUCCESS) {
            if (buffer[0] == L'\0') {
                HeapFreeBytes(buffer);
                return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
            }
            *value = buffer;
            return S_OK;
        }
        HeapFreeBytes(buffer);
        if (status == ERROR_MORE_DATA) {
            bytes = read;
            status = ERROR_SUCCESS;
        }
    }
    return HRESULT_FROM_WIN32(status == ERROR_SUCCESS ? ERROR_MORE_DATA : status);
}

// CLSID 키의 Command 값. Exe·Verb 는 꼭 있어야 하고 Title 은 없어도 된다.
struct CommandConfig {
    PWSTR exe = nullptr;
    PWSTR verb = nullptr;
    PWSTR title = nullptr;
};

void FreeConfig(CommandConfig* config)
{
    HeapFreeBytes(config->exe);
    HeapFreeBytes(config->verb);
    HeapFreeBytes(config->title);
    *config = CommandConfig{};
}

HRESULT LoadConfig(REFCLSID clsid, CommandConfig* config)
{
    *config = CommandConfig{};
    WCHAR guid[40];
    if (StringFromGUID2(clsid, guid, ARRAYSIZE(guid)) == 0) return E_UNEXPECTED;
    WCHAR subKey[96];
    HRESULT hr = StringCchPrintfW(subKey, ARRAYSIZE(subKey), L"Software\\Classes\\CLSID\\%s\\Command", guid);
    if (FAILED(hr)) return hr;
    hr = ReadString(subKey, L"Exe", &config->exe);
    if (SUCCEEDED(hr)) hr = ReadString(subKey, L"Verb", &config->verb);
    if (FAILED(hr)) {
        FreeConfig(config);
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    if (FAILED(ReadString(subKey, L"Title", &config->title))) config->title = nullptr;
    return S_OK;
}

// 목록 파일 내용(UTF-8, BOM 없음, 한 줄에 경로 하나, \r\n).
struct Utf8Text {
    char* data = nullptr;
    SIZE_T size = 0;
    SIZE_T capacity = 0;
};

HRESULT AppendLine(Utf8Text* text, PCWSTR path)
{
    const int length = lstrlenW(path);
    if (length == 0) return E_INVALIDARG;
    // 짝 없는 서로게이트처럼 UTF-8 로 바꿀 수 없는 경로는 실패로 돌려 건너뛰게 한다.
    const int needed = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, path, length, nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return HRESULT_FROM_WIN32(GetLastError());
    const SIZE_T required = text->size + static_cast<SIZE_T>(needed) + 2;
    if (required > text->capacity) {
        SIZE_T capacity = text->capacity == 0 ? 1024 : text->capacity * 2;
        while (capacity < required) capacity *= 2;
        void* grown = text->data == nullptr ? HeapAllocBytes(capacity)
                                            : HeapReAlloc(GetProcessHeap(), 0, text->data, capacity);
        if (grown == nullptr) return E_OUTOFMEMORY;
        text->data = static_cast<char*>(grown);
        text->capacity = capacity;
    }
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, path, length, text->data + text->size, needed, nullptr, nullptr);
    text->size += static_cast<SIZE_T>(needed);
    text->data[text->size++] = '\r';
    text->data[text->size++] = '\n';
    return S_OK;
}

// Exe 경로의 파일 이름에서 확장자를 뺀 부분(예 C:\...\ohmyZip.exe → ohmyZip). 결과 길이를 돌려준다.
PCWSTR ExeBaseName(PCWSTR exe, int* length)
{
    PCWSTR name = exe;
    for (PCWSTR p = exe; *p != L'\0'; ++p) {
        if (*p == L'\\' || *p == L'/') name = p + 1;
    }
    PCWSTR dot = nullptr;
    for (PCWSTR p = name; *p != L'\0'; ++p) {
        if (*p == L'.') dot = p;
    }
    *length = dot != nullptr ? static_cast<int>(dot - name) : lstrlenW(name);
    return name;
}

HRESULT EnsureDirectory(PCWSTR path)
{
    if (CreateDirectoryW(path, nullptr)) return S_OK;
    const DWORD error = GetLastError();
    return error == ERROR_ALREADY_EXISTS ? S_OK : HRESULT_FROM_WIN32(error);
}

// %LOCALAPPDATA%\<Exe 이름>\selections\<새 GUID>.txt 에 text 를 쓰고 그 경로를 돌려준다.
HRESULT WriteListFile(PCWSTR exe, const Utf8Text& text, PWSTR* listPath)
{
    *listPath = nullptr;
    if (text.size > MAXDWORD) return E_INVALIDARG;
    int baseLength = 0;
    PCWSTR base = ExeBaseName(exe, &baseLength);
    if (baseLength == 0) return E_INVALIDARG;

    GUID id;
    HRESULT hr = CoCreateGuid(&id);
    if (FAILED(hr)) return hr;
    WCHAR guid[40];
    if (StringFromGUID2(id, guid, ARRAYSIZE(guid)) == 0) return E_UNEXPECTED;
    guid[37] = L'\0';  // "{...}" 에서 닫는 괄호를 뗀다. 여는 괄호는 guid + 1 로 건너뛴다.

    PWSTR root = nullptr;
    hr = SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT, nullptr, &root);
    if (FAILED(hr)) return hr;
    const SIZE_T chars = static_cast<SIZE_T>(lstrlenW(root)) + 1 + static_cast<SIZE_T>(baseLength) + 64;
    PWSTR path = static_cast<PWSTR>(HeapAllocBytes(chars * sizeof(WCHAR)));
    if (path == nullptr) {
        CoTaskMemFree(root);
        return E_OUTOFMEMORY;
    }
    hr = StringCchPrintfW(path, chars, L"%s\\%.*s", root, baseLength, base);
    CoTaskMemFree(root);
    if (SUCCEEDED(hr)) hr = EnsureDirectory(path);
    if (SUCCEEDED(hr)) hr = StringCchCatW(path, chars, L"\\selections");
    if (SUCCEEDED(hr)) hr = EnsureDirectory(path);
    if (SUCCEEDED(hr)) {
        const size_t used = static_cast<size_t>(lstrlenW(path));
        hr = StringCchPrintfW(path + used, chars - used, L"\\%s.txt", guid + 1);
    }
    if (FAILED(hr)) {
        HeapFreeBytes(path);
        return hr;
    }

    HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        hr = HRESULT_FROM_WIN32(GetLastError());
        HeapFreeBytes(path);
        return hr;
    }
    DWORD written = 0;
    const DWORD size = static_cast<DWORD>(text.size);
    if (!WriteFile(file, text.data, size, &written, nullptr)) {
        hr = HRESULT_FROM_WIN32(GetLastError());
    } else if (written != size) {
        hr = HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);
    }
    if (!CloseHandle(file) && SUCCEEDED(hr)) hr = HRESULT_FROM_WIN32(GetLastError());
    if (FAILED(hr)) {
        DeleteFileW(path);
        HeapFreeBytes(path);
        return hr;
    }
    *listPath = path;
    return S_OK;
}

// "<Exe>" <Verb> --list "<목록 파일>" 로 실행하고 자식이 앞으로 나올 수 있게 허락한다.
HRESULT Launch(const CommandConfig& config, PCWSTR listPath)
{
    const SIZE_T chars = static_cast<SIZE_T>(lstrlenW(config.exe)) + static_cast<SIZE_T>(lstrlenW(config.verb)) +
                         static_cast<SIZE_T>(lstrlenW(listPath)) + 16;
    PWSTR commandLine = static_cast<PWSTR>(HeapAllocBytes(chars * sizeof(WCHAR)));
    if (commandLine == nullptr) return E_OUTOFMEMORY;
    HRESULT hr = StringCchPrintfW(commandLine, chars, L"\"%s\" %s --list \"%s\"", config.exe, config.verb, listPath);
    if (SUCCEEDED(hr)) {
        STARTUPINFOW startup = {};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process = {};
        if (CreateProcessW(config.exe, commandLine, nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process)) {
            AllowSetForegroundWindow(process.dwProcessId);
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        } else {
            hr = HRESULT_FROM_WIN32(GetLastError());
        }
    }
    HeapFreeBytes(commandLine);
    return hr;
}

HRESULT DuplicateString(PCWSTR source, PWSTR* copy)
{
    *copy = nullptr;
    const SIZE_T chars = static_cast<SIZE_T>(lstrlenW(source)) + 1;
    PWSTR buffer = static_cast<PWSTR>(CoTaskMemAlloc(chars * sizeof(WCHAR)));
    if (buffer == nullptr) return E_OUTOFMEMORY;
    CopyMemory(buffer, source, chars * sizeof(WCHAR));
    *copy = buffer;
    return S_OK;
}

class Command final : public IExplorerCommand, public HeapObject {
public:
    // config 의 문자열 소유권을 넘겨받는다.
    explicit Command(const CommandConfig& config) : config_(config) { InterlockedIncrement(&g_moduleRefs); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IExplorerCommand)) {
            *ppv = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const LONG refs = InterlockedDecrement(&refs_);
        if (refs == 0) delete this;
        return static_cast<ULONG>(refs);
    }

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override
    {
        if (name == nullptr) return E_POINTER;
        *name = nullptr;
        // Title 이 없으면 셸이 verb 키의 MUIVerb 를 쓴다.
        if (config_.title == nullptr) return E_NOTIMPL;
        return DuplicateString(config_.title, name);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
    {
        if (icon == nullptr) return E_POINTER;
        *icon = nullptr;
        const SIZE_T chars = static_cast<SIZE_T>(lstrlenW(config_.exe)) + 3;
        PWSTR buffer = static_cast<PWSTR>(CoTaskMemAlloc(chars * sizeof(WCHAR)));
        if (buffer == nullptr) return E_OUTOFMEMORY;
        const HRESULT hr = StringCchPrintfW(buffer, chars, L"%s,0", config_.exe);
        if (FAILED(hr)) {
            CoTaskMemFree(buffer);
            return hr;
        }
        *icon = buffer;
        return S_OK;
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override
    {
        if (tip != nullptr) *tip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* name) override
    {
        if (name != nullptr) *name = GUID_NULL;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        if (state == nullptr) return E_POINTER;
        *state = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        if (items == nullptr) return S_OK;
        DWORD count = 0;
        HRESULT hr = items->GetCount(&count);
        if (FAILED(hr)) return hr;

        Utf8Text text;
        DWORD paths = 0;
        for (DWORD i = 0; i < count; ++i) {
            IShellItem* item = nullptr;
            if (FAILED(items->GetItemAt(i, &item))) continue;
            PWSTR path = nullptr;
            const HRESULT hrPath = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
            item->Release();
            // 파일 시스템 경로가 없는 항목(가상 폴더 등)은 건너뛴다.
            if (FAILED(hrPath)) continue;
            hr = AppendLine(&text, path);
            CoTaskMemFree(path);
            if (hr == E_OUTOFMEMORY) {
                HeapFreeBytes(text.data);
                return hr;
            }
            if (SUCCEEDED(hr)) ++paths;
        }
        if (paths == 0) {
            HeapFreeBytes(text.data);
            return S_OK;
        }

        PWSTR listPath = nullptr;
        hr = WriteListFile(config_.exe, text, &listPath);
        HeapFreeBytes(text.data);
        if (FAILED(hr)) return hr;
        hr = Launch(config_, listPath);
        // 앱이 뜨지 않았으면 읽을 쪽이 없으므로 목록 파일을 지운다.
        if (FAILED(hr)) DeleteFileW(listPath);
        HeapFreeBytes(listPath);
        return hr;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (flags == nullptr) return E_POINTER;
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        if (commands != nullptr) *commands = nullptr;
        return E_NOTIMPL;
    }

private:
    ~Command()
    {
        FreeConfig(&config_);
        InterlockedDecrement(&g_moduleRefs);
    }

    LONG refs_ = 1;
    CommandConfig config_;
};

class ClassFactory final : public IClassFactory, public HeapObject {
public:
    explicit ClassFactory(REFCLSID clsid) : clsid_(clsid) { InterlockedIncrement(&g_moduleRefs); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory)) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&refs_)); }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const LONG refs = InterlockedDecrement(&refs_);
        if (refs == 0) delete this;
        return static_cast<ULONG>(refs);
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (ppv == nullptr) return E_POINTER;
        *ppv = nullptr;
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        // 등록 값이 그사이 바뀌었을 수 있으므로 개체마다 다시 읽는다.
        CommandConfig config;
        HRESULT hr = LoadConfig(clsid_, &config);
        if (FAILED(hr)) return hr;
        Command* command = new Command(config);
        if (command == nullptr) {
            FreeConfig(&config);
            return E_OUTOFMEMORY;
        }
        hr = command->QueryInterface(riid, ppv);
        command->Release();
        return hr;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) {
            InterlockedIncrement(&g_moduleRefs);
        } else {
            InterlockedDecrement(&g_moduleRefs);
        }
        return S_OK;
    }

private:
    ~ClassFactory() { InterlockedDecrement(&g_moduleRefs); }

    LONG refs_ = 1;
    CLSID clsid_;
};

}  // namespace

STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ LPVOID FAR* ppv)
{
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;
    // Command 값이 없는 CLSID 는 맡지 않는다.
    CommandConfig config;
    HRESULT hr = LoadConfig(rclsid, &config);
    if (FAILED(hr)) return hr;
    FreeConfig(&config);
    ClassFactory* factory = new ClassFactory(rclsid);
    if (factory == nullptr) return E_OUTOFMEMORY;
    hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

__control_entrypoint(DllExport) STDAPI DllCanUnloadNow()
{
    return g_moduleRefs == 0 ? S_OK : S_FALSE;
}
