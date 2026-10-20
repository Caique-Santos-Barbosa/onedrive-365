// Teste do menu sem o Explorer: carrega a DLL, cria o comando raiz e confere visibilidade e subitens.
// Uso: test_menu.exe <dll> <caminho>...
#include <windows.h>
#include <shlobj.h>
#include <shobjidl_core.h>
#include <cstdio>

typedef HRESULT(STDAPICALLTYPE* GetClassObjectFn)(REFCLSID, REFIID, void**);

int wmain(int argc, wchar_t** argv) {
    if (argc < 3) return 2;
    CoInitialize(nullptr);
    HMODULE dll = LoadLibraryW(argv[1]);
    if (!dll) return 1;
    auto get = reinterpret_cast<GetClassObjectFn>(GetProcAddress(dll, "DllGetClassObject"));
    const CLSID root = {0xb1e6f0a0, 0x5c3d, 0x4e7a, {0x9b, 0x21, 0x0d, 0x36, 0x5a, 0x0e, 0x00, 0x10}};
    IClassFactory* f = nullptr;
    if (FAILED(get(root, IID_IClassFactory, reinterpret_cast<void**>(&f)))) return 1;
    IExplorerCommand* cmd = nullptr;
    f->CreateInstance(nullptr, IID_IExplorerCommand, reinterpret_cast<void**>(&cmd));
    f->Release();

    LPWSTR title = nullptr, icon = nullptr;
    cmd->GetTitle(nullptr, &title);
    cmd->GetIcon(nullptr, &icon);
    wprintf(L"menu: %s | ícone: %s\n", title, icon);
    IEnumExplorerCommand* e = nullptr;
    if (SUCCEEDED(cmd->EnumSubCommands(&e))) {
        IExplorerCommand* sub = nullptr;
        ULONG n = 0;
        while (e->Next(1, &sub, &n) == S_OK) {
            LPWSTR t = nullptr;
            sub->GetTitle(nullptr, &t);
            wprintf(L"  subitem: %s\n", t);
            CoTaskMemFree(t);
            sub->Release();
        }
        e->Release();
    }
    for (int i = 2; i < argc; ++i) {
        IShellItem* item = nullptr;
        IShellItemArray* arr = nullptr;
        if (FAILED(SHCreateItemFromParsingName(argv[i], nullptr, IID_PPV_ARGS(&item)))) { wprintf(L"(não existe) %s\n", argv[i]); continue; }
        SHCreateShellItemArrayFromShellItem(item, IID_PPV_ARGS(&arr));
        EXPCMDSTATE st = ECS_HIDDEN;
        cmd->GetState(arr, FALSE, &st);
        wprintf(L"%-9s %s\n", st == ECS_HIDDEN ? L"oculto" : L"visível", argv[i]);
        arr->Release();
        item->Release();
    }
    cmd->Release();
    return 0;
}
