// Selos de status do OneDrive 365 no Explorer (IShellIconOverlayIdentifier).
//
// O Explorer chama IsMemberOf para cada ícone exibido, então tudo aqui é local e barato:
// nunca toca na unidade montada (isso dispararia acesso à rede). O estado vem de
//   - HKCU\Software\OneDrive365: DriveLetter, CacheRoot (cache do rclone) e PendingFile;
//   - PendingFile: caminhos relativos com upload pendente, um por linha (UTF-8), escrito pelo app.
#include <windows.h>
#include <shlobj.h>
#include <atomic>
#include <mutex>
#include <string>
#include <unordered_set>

namespace {

HMODULE g_module;
std::atomic<long> g_objects{0};
std::atomic<long> g_locks{0};

enum Kind { KindSync = 0, KindLocal = 1, KindCloud = 2, KindNone = -1 };

// {B1E6F0A0-5C3D-4E7A-9B21-0D365A0E0001..3}
const CLSID kClsids[] = {
    {0xb1e6f0a0, 0x5c3d, 0x4e7a, {0x9b, 0x21, 0x0d, 0x36, 0x5a, 0x0e, 0x00, 0x01}},
    {0xb1e6f0a0, 0x5c3d, 0x4e7a, {0x9b, 0x21, 0x0d, 0x36, 0x5a, 0x0e, 0x00, 0x02}},
    {0xb1e6f0a0, 0x5c3d, 0x4e7a, {0x9b, 0x21, 0x0d, 0x36, 0x5a, 0x0e, 0x00, 0x03}},
};
const wchar_t* kIconFiles[] = {L"overlay-sync.ico", L"overlay-local.ico", L"overlay-cloud.ico"};

std::wstring Lower(std::wstring s) {
    if (!s.empty()) CharLowerBuffW(&s[0], static_cast<DWORD>(s.size()));
    return s;
}

std::wstring RegString(HKEY key, const wchar_t* name) {
    wchar_t buf[1024];
    DWORD size = sizeof(buf);
    if (RegGetValueW(key, nullptr, name, RRF_RT_REG_SZ, nullptr, buf, &size) != ERROR_SUCCESS) return L"";
    return buf;
}

struct State {
    std::mutex mu;
    std::wstring letter;       // "m:"
    std::wstring cacheRoot;    // ...\cache\vfs\m365
    std::wstring pendingFile;
    std::unordered_set<std::wstring> pending;   // caminhos relativos em minúsculas, com '\'
    FILETIME pendingTime{};
    ULONGLONG lastRegRead = 0;
    ULONGLONG lastPendingCheck = 0;

    void Refresh() {
        const ULONGLONG now = GetTickCount64();
        if (letter.empty() || now - lastRegRead > 10000) {
            lastRegRead = now;
            HKEY key;
            if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\OneDrive365", 0, KEY_READ, &key) == ERROR_SUCCESS) {
                letter = Lower(RegString(key, L"DriveLetter"));
                cacheRoot = RegString(key, L"CacheRoot");
                pendingFile = RegString(key, L"PendingFile");
                RegCloseKey(key);
            }
        }
        if (now - lastPendingCheck < 1000 || pendingFile.empty()) return;
        lastPendingCheck = now;

        WIN32_FILE_ATTRIBUTE_DATA data;
        if (!GetFileAttributesExW(pendingFile.c_str(), GetFileExInfoStandard, &data)) {
            pending.clear();
            return;
        }
        if (CompareFileTime(&data.ftLastWriteTime, &pendingTime) == 0) return;
        pendingTime = data.ftLastWriteTime;
        pending.clear();

        HANDLE f = CreateFileW(pendingFile.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                               nullptr, OPEN_EXISTING, 0, nullptr);
        if (f == INVALID_HANDLE_VALUE) return;
        std::string bytes(data.nFileSizeLow, '\0');
        DWORD read = 0;
        if (!bytes.empty()) ReadFile(f, &bytes[0], data.nFileSizeLow, &read, nullptr);
        CloseHandle(f);
        bytes.resize(read);

        size_t start = 0;
        while (start < bytes.size()) {
            size_t end = bytes.find('\n', start);
            if (end == std::string::npos) end = bytes.size();
            std::string line = bytes.substr(start, end - start);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (!line.empty()) {
                int n = MultiByteToWideChar(CP_UTF8, 0, line.data(), static_cast<int>(line.size()), nullptr, 0);
                std::wstring w(n, L'\0');
                MultiByteToWideChar(CP_UTF8, 0, line.data(), static_cast<int>(line.size()), &w[0], n);
                pending.insert(Lower(w));
            }
            start = end + 1;
        }
    }
} g_state;

Kind Classify(const wchar_t* path, DWORD attrib) {
    std::wstring p(path);
    std::lock_guard<std::mutex> lock(g_state.mu);
    g_state.Refresh();
    if (g_state.letter.size() != 2 || p.size() < 4 || p[2] != L'\\') return KindNone;
    if (_wcsnicmp(p.c_str(), g_state.letter.c_str(), 2) != 0) return KindNone;

    const std::wstring rel = p.substr(3);
    const size_t first = rel.find(L'\\');
    if (first == std::wstring::npos) return KindNone;   // "Meu OneDrive" e "SharePoint" têm ícone próprio
    if (_wcsnicmp(rel.c_str(), L"SharePoint\\", 11) == 0 && rel.find(L'\\', first + 1) == std::wstring::npos)
        return KindNone;                                // raiz de cada biblioteca também

    const std::wstring lrel = Lower(rel);
    const size_t slash = lrel.rfind(L'\\');
    if (lrel.compare(slash + 1, std::wstring::npos, L"desktop.ini") == 0) return KindNone;

    const bool isDir = (attrib & FILE_ATTRIBUTE_DIRECTORY) != 0;
    if (g_state.pending.count(lrel)) return KindSync;
    if (isDir) {
        const std::wstring prefix = lrel + L"\\";
        for (const auto& item : g_state.pending)
            if (item.compare(0, prefix.size(), prefix) == 0) return KindSync;
        return KindCloud;
    }
    if (!g_state.cacheRoot.empty()) {
        const std::wstring cached = g_state.cacheRoot + L"\\" + rel;
        if (GetFileAttributesW(cached.c_str()) != INVALID_FILE_ATTRIBUTES) return KindLocal;
    }
    return KindCloud;
}

class Overlay : public IShellIconOverlayIdentifier {
public:
    explicit Overlay(Kind kind) : kind_(kind) { ++g_objects; }
    virtual ~Overlay() { --g_objects; }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (riid == IID_IUnknown || riid == IID_IShellIconOverlayIdentifier) {
            *ppv = static_cast<IShellIconOverlayIdentifier*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++ref_; }
    IFACEMETHODIMP_(ULONG) Release() override {
        const long r = --ref_;
        if (r == 0) delete this;
        return r;
    }

    IFACEMETHODIMP IsMemberOf(LPCWSTR path, DWORD attrib) override {
        try { return Classify(path, attrib) == kind_ ? S_OK : S_FALSE; }
        catch (...) { return S_FALSE; }   // exceção não pode atravessar para o Explorer
    }

    IFACEMETHODIMP GetOverlayInfo(LPWSTR iconFile, int cchMax, int* index, DWORD* flags) override {
        wchar_t dir[MAX_PATH];
        const DWORD n = GetModuleFileNameW(g_module, dir, MAX_PATH);
        if (n == 0 || n == MAX_PATH) return E_FAIL;
        std::wstring path(dir);
        path = path.substr(0, path.rfind(L'\\') + 1) + kIconFiles[kind_];
        if (static_cast<int>(path.size()) >= cchMax) return E_FAIL;
        wcscpy_s(iconFile, cchMax, path.c_str());
        *index = 0;
        *flags = ISIOI_ICONFILE;
        return S_OK;
    }

    IFACEMETHODIMP GetPriority(int* priority) override {
        *priority = kind_;   // sincronizando > local > nuvem
        return S_OK;
    }

private:
    std::atomic<long> ref_{1};
    Kind kind_;
};

class Factory : public IClassFactory {
public:
    explicit Factory(Kind kind) : kind_(kind) { ++g_objects; }
    virtual ~Factory() { --g_objects; }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (riid == IID_IUnknown || riid == IID_IClassFactory) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return ++ref_; }
    IFACEMETHODIMP_(ULONG) Release() override {
        const long r = --ref_;
        if (r == 0) delete this;
        return r;
    }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override {
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        Overlay* o = new (std::nothrow) Overlay(kind_);
        if (!o) return E_OUTOFMEMORY;
        const HRESULT hr = o->QueryInterface(riid, ppv);
        o->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override {
        lock ? ++g_locks : --g_locks;
        return S_OK;
    }

private:
    std::atomic<long> ref_{1};
    Kind kind_;
};

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv) {
    *ppv = nullptr;
    for (int i = 0; i < 3; ++i) {
        if (clsid == kClsids[i]) {
            Factory* f = new (std::nothrow) Factory(static_cast<Kind>(i));
            if (!f) return E_OUTOFMEMORY;
            const HRESULT hr = f->QueryInterface(riid, ppv);
            f->Release();
            return hr;
        }
    }
    return CLASS_E_CLASSNOTAVAILABLE;
}

STDAPI DllCanUnloadNow() {
    return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE;
}
