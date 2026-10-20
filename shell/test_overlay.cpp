// Teste da extensão sem o Explorer: carrega a DLL, cria os 3 handlers e pergunta IsMemberOf.
// Uso: test_overlay.exe <dll> <caminho> <atributos: f|d> ...  -> imprime o selo escolhido de cada caminho
#include <windows.h>
#include <shlobj.h>
#include <cstdio>

typedef HRESULT(STDAPICALLTYPE* GetClassObjectFn)(REFCLSID, REFIID, void**);

int wmain(int argc, wchar_t** argv) {
    if (argc < 4) return 2;
    CoInitialize(nullptr);
    HMODULE dll = LoadLibraryW(argv[1]);
    if (!dll) { wprintf(L"LoadLibrary falhou %lu\n", GetLastError()); return 1; }
    auto get = reinterpret_cast<GetClassObjectFn>(GetProcAddress(dll, "DllGetClassObject"));
    const wchar_t* names[] = {L"sincronizando", L"no PC", L"nuvem"};
    IShellIconOverlayIdentifier* ids[3] = {};
    for (int i = 0; i < 3; ++i) {
        CLSID c = {0xb1e6f0a0, 0x5c3d, 0x4e7a, {0x9b, 0x21, 0x0d, 0x36, 0x5a, 0x0e, 0x00, static_cast<unsigned char>(i + 1)}};
        IClassFactory* f = nullptr;
        if (FAILED(get(c, IID_IClassFactory, reinterpret_cast<void**>(&f)))) return 1;
        f->CreateInstance(nullptr, IID_IShellIconOverlayIdentifier, reinterpret_cast<void**>(&ids[i]));
        f->Release();
        wchar_t icon[MAX_PATH]; int idx; DWORD flags;
        if (FAILED(ids[i]->GetOverlayInfo(icon, MAX_PATH, &idx, &flags))) return 1;
        if (i == 0) wprintf(L"icone: %s\n", icon);
    }
    for (int a = 2; a + 1 < argc; a += 2) {
        DWORD attr = argv[a + 1][0] == L'd' ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        const wchar_t* result = L"(nenhum)";
        for (int i = 0; i < 3; ++i)
            if (ids[i]->IsMemberOf(argv[a], attr) == S_OK) { result = names[i]; break; }
        LARGE_INTEGER t0, t1, fq; QueryPerformanceFrequency(&fq); QueryPerformanceCounter(&t0);
        for (int n = 0; n < 1000; ++n) ids[2]->IsMemberOf(argv[a], attr);
        QueryPerformanceCounter(&t1);
        wprintf(L"%-14s %6.1f us/chamada  %s\n", result, (t1.QuadPart - t0.QuadPart) * 1e6 / fq.QuadPart / 1000.0, argv[a]);
    }
    for (auto* i : ids) i->Release();
    return 0;
}
