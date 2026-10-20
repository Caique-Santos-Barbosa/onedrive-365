// Menu "OneDrive 365" do botão direito no menu compacto do Windows 11 (IExplorerCommand).
// Carregado pelo Explorer via pacote MSIX "sparse" (ver tools/package). Só aparece para itens no M: e nas
// pastas com backup, lidas de HKCU\Software\OneDrive365\MenuRoots (separadas por '|'). Cada subitem chama
//   "<Exe>" --action <share|link|web> "<caminho>"
#include <windows.h>
#include <shlobj.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <atomic>
#include <new>
#include <string>
#include <vector>

namespace {

HMODULE g_module;
std::atomic<long> g_objects{0};
std::atomic<long> g_locks{0};

// {B1E6F0A0-5C3D-4E7A-9B21-0D365A0E0010}
const CLSID kRootClsid = {0xb1e6f0a0, 0x5c3d, 0x4e7a, {0x9b, 0x21, 0x0d, 0x36, 0x5a, 0x0e, 0x00, 0x10}};

std::wstring RegString(const wchar_t* name) {
    wchar_t buf[4096];
    DWORD size = sizeof(buf);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\OneDrive365", name, RRF_RT_REG_SZ, nullptr, buf, &size) != ERROR_SUCCESS)
        return L"";
    return buf;
}

std::wstring FirstPath(IShellItemArray* items) {
    if (!items) return L"";
    IShellItem* item = nullptr;
    if (FAILED(items->GetItemAt(0, &item))) return L"";
    PWSTR path = nullptr;
    std::wstring result;
    if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) {
        result = path;
        CoTaskMemFree(path);
    }
    item->Release();
    return result;
}

// Pasta do módulo (instalação do app): os ícones do submenu ficam em menuicons\<ação>.ico
std::wstring ModuleDir() {
    wchar_t buf[MAX_PATH];
    const DWORD n = GetModuleFileNameW(g_module, buf, MAX_PATH);
    std::wstring dir(buf, n);
    return dir.substr(0, dir.rfind(L'\\') + 1);
}

bool IsFolder(IShellItemArray* items) {
    if (!items) return false;
    IShellItem* item = nullptr;
    if (FAILED(items->GetItemAt(0, &item))) return false;
    SFGAOF attrs = 0;
    item->GetAttributes(SFGAO_FOLDER, &attrs);
    item->Release();
    return (attrs & SFGAO_FOLDER) != 0;
}

bool UnderCloudRoot(const std::wstring& path) {
    if (path.empty()) return false;
    const std::wstring roots = RegString(L"MenuRoots");
    size_t start = 0;
    while (start < roots.size()) {
        size_t end = roots.find(L'|', start);
        if (end == std::wstring::npos) end = roots.size();
        std::wstring root = roots.substr(start, end - start);
        start = end + 1;
        if (root.empty()) continue;
        std::wstring bare = root;
        if (!bare.empty() && bare.back() == L'\\') bare.pop_back();
        if (_wcsnicmp(path.c_str(), root.c_str(), root.size()) == 0) return true;   // dentro da pasta
        if (_wcsicmp(path.c_str(), bare.c_str()) == 0) return true;                // a própria pasta
    }
    return false;
}

// Classe base com IUnknown + IExplorerCommand padrão
class CommandBase : public IExplorerCommand {
public:
    CommandBase() { ++g_objects; }
    virtual ~CommandBase() { --g_objects; }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (riid == IID_IUnknown || riid == IID_IExplorerCommand) {
            *ppv = static_cast<IExplorerCommand*>(this);
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

    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override {
        wchar_t sys[MAX_PATH];
        GetSystemDirectoryW(sys, MAX_PATH);
        return SHStrDupW((std::wstring(sys) + L"\\imageres.dll,-1043").c_str(), icon);
    }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) override { *tip = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* guid) override { *guid = GUID_NULL; return S_OK; }
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override {
        *state = UnderCloudRoot(FirstPath(items)) ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** e) override { *e = nullptr; return E_NOTIMPL; }

private:
    std::atomic<long> ref_{1};
};

class SubCommand final : public CommandBase {
public:
    SubCommand(const wchar_t* title, const wchar_t* action, bool foldersOnly = false)
        : title_(title), action_(action), foldersOnly_(foldersOnly) {}

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override { return SHStrDupW(title_, name); }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override {
        return SHStrDupW((ModuleDir() + L"menuicons\\" + action_ + L".ico").c_str(), icon);
    }
    IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override {
        *state = foldersOnly_ && !IsFolder(items) ? ECS_HIDDEN : ECS_ENABLED;
        return S_OK;
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { *flags = ECF_DEFAULT; return S_OK; }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override {
        const std::wstring path = FirstPath(items);
        const std::wstring exe = RegString(L"Exe");
        if (path.empty() || exe.empty()) return E_FAIL;
        std::wstring cmd = L"\"" + exe + L"\" --action " + action_ + L" \"" + path + L"\"";
        STARTUPINFOW si{sizeof(si)};
        PROCESS_INFORMATION pi{};
        if (!CreateProcessW(exe.c_str(), &cmd[0], nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi))
            return HRESULT_FROM_WIN32(GetLastError());
        AllowSetForegroundWindow(pi.dwProcessId);   // a janela "Compartilhar" precisa vir para a frente
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return S_OK;
    }

private:
    const wchar_t* title_;
    const wchar_t* action_;
    bool foldersOnly_;
};

class SubEnum final : public IEnumExplorerCommand {
public:
    SubEnum() {
        ++g_objects;
        items_.push_back(new (std::nothrow) SubCommand(L"Compartilhar", L"share"));
        items_.push_back(new (std::nothrow) SubCommand(L"Copiar Link", L"link"));
        items_.push_back(new (std::nothrow) SubCommand(L"Gerenciar acesso", L"access"));
        items_.push_back(new (std::nothrow) SubCommand(L"Exibir online", L"web"));
        items_.push_back(new (std::nothrow) SubCommand(L"Cor da pasta", L"color", true));
    }
    ~SubEnum() {
        for (auto* c : items_) if (c) c->Release();
        --g_objects;
    }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (riid == IID_IUnknown || riid == IID_IEnumExplorerCommand) {
            *ppv = static_cast<IEnumExplorerCommand*>(this);
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

    IFACEMETHODIMP Next(ULONG count, IExplorerCommand** out, ULONG* fetched) override {
        ULONG n = 0;
        while (n < count && pos_ < items_.size()) {
            if (items_[pos_]) {
                items_[pos_]->AddRef();
                out[n++] = items_[pos_];
            }
            ++pos_;
        }
        if (fetched) *fetched = n;
        return n == count ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG count) override { pos_ += count; return pos_ <= items_.size() ? S_OK : S_FALSE; }
    IFACEMETHODIMP Reset() override { pos_ = 0; return S_OK; }
    IFACEMETHODIMP Clone(IEnumExplorerCommand** e) override { *e = nullptr; return E_NOTIMPL; }

private:
    std::atomic<long> ref_{1};
    std::vector<SubCommand*> items_;
    size_t pos_ = 0;
};

class RootCommand final : public CommandBase {
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name) override { return SHStrDupW(L"OneDrive 365", name); }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override { *flags = ECF_HASSUBCOMMANDS; return S_OK; }
    IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) override { return E_NOTIMPL; }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** e) override {
        *e = new (std::nothrow) SubEnum();
        return *e ? S_OK : E_OUTOFMEMORY;
    }
};

class Factory final : public IClassFactory {
public:
    Factory() { ++g_objects; }
    ~Factory() { --g_objects; }
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
        auto* c = new (std::nothrow) RootCommand();
        if (!c) return E_OUTOFMEMORY;
        const HRESULT hr = c->QueryInterface(riid, ppv);
        c->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { lock ? ++g_locks : --g_locks; return S_OK; }

private:
    std::atomic<long> ref_{1};
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
    if (clsid != kRootClsid) return CLASS_E_CLASSNOTAVAILABLE;
    auto* f = new (std::nothrow) Factory();
    if (!f) return E_OUTOFMEMORY;
    const HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow() { return g_objects == 0 && g_locks == 0 ? S_OK : S_FALSE; }
