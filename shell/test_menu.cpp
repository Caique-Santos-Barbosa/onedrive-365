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
    // test_menu.exe <dll> --invoke <título do subitem> <caminho>: executa o subitem como um clique no Explorer
    if (argc == 5 && wcscmp(argv[2], L"--invoke") == 0) {
        IShellItem* item = nullptr;
        IShellItemArray* arr = nullptr;
        if (FAILED(SHCreateItemFromParsingName(argv[4], nullptr, IID_PPV_ARGS(&item)))) return 3;
        SHCreateShellItemArrayFromShellItem(item, IID_PPV_ARGS(&arr));
        IEnumExplorerCommand* subs = nullptr;
        cmd->EnumSubCommands(&subs);
        IExplorerCommand* sub = nullptr;
        ULONG n = 0;
        HRESULT hr = E_INVALIDARG;
        while (subs->Next(1, &sub, &n) == S_OK) {
            LPWSTR t = nullptr;
            sub->GetTitle(nullptr, &t);
            if (wcscmp(t, argv[3]) == 0) {
                LARGE_INTEGER a, b, freq;
                QueryPerformanceFrequency(&freq);
                QueryPerformanceCounter(&a);
                hr = sub->Invoke(arr, nullptr);
                QueryPerformanceCounter(&b);
                wprintf(L"Invoke(%s) = 0x%08lx em %.1f ms\n", t, hr, (b.QuadPart - a.QuadPart) * 1000.0 / freq.QuadPart);
            }
            CoTaskMemFree(t);
            sub->Release();
        }
        subs->Release();
        arr->Release();
        item->Release();
        cmd->Release();
        return SUCCEEDED(hr) ? 0 : 1;
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
