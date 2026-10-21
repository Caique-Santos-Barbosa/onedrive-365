// Abre o menu de botão direito de um caminho (como o Explorer), mostra o submenu "OneDrive 365" (ou o menu inteiro)
// e salva uma imagem dele, fechando sozinho. Para documentação; não mexe em mouse/teclado.
// Uso: test_menushot.exe <caminho> <saida.bmp> [--full]
#include <windows.h>
#include <shlobj.h>
#include <shobjidl_core.h>
#include <cstdio>
#include <string>

static IContextMenu* g_cm = nullptr;
static std::wstring g_out;
static HWND g_owner = nullptr;

static HMENU FindSub(HMENU menu, const wchar_t* title) {
    const int n = GetMenuItemCount(menu);
    for (int i = 0; i < n; ++i) {
        wchar_t text[256] = L"";
        MENUITEMINFOW mii{sizeof(mii)};
        mii.fMask = MIIM_STRING | MIIM_SUBMENU;
        mii.dwTypeData = text;
        mii.cch = 255;
        if (GetMenuItemInfoW(menu, i, TRUE, &mii) && mii.hSubMenu && wcscmp(text, title) == 0) {
            IContextMenu3* cm3 = nullptr;
            if (SUCCEEDED(g_cm->QueryInterface(IID_PPV_ARGS(&cm3)))) {
                LRESULT r = 0;
                cm3->HandleMenuMsg2(WM_INITMENUPOPUP, (WPARAM)mii.hSubMenu, i, &r);
                cm3->Release();
            }
            return mii.hSubMenu;
        }
    }
    return nullptr;
}

static void SaveBmp(HBITMAP bmp, int w, int h, const std::wstring& path) {
    BITMAPINFOHEADER bi{sizeof(bi), w, -h, 1, 24, BI_RGB};
    const int stride = ((w * 3 + 3) & ~3);
    std::string pixels(stride * h, '\0');
    HDC dc = GetDC(nullptr);
    GetDIBits(dc, bmp, 0, h, &pixels[0], reinterpret_cast<BITMAPINFO*>(&bi), DIB_RGB_COLORS);
    ReleaseDC(nullptr, dc);
    BITMAPFILEHEADER bf{0x4D42, (DWORD)(sizeof(bf) + sizeof(bi) + pixels.size()), 0, 0, sizeof(bf) + sizeof(bi)};
    FILE* f = _wfopen(path.c_str(), L"wb");
    if (!f) return;
    fwrite(&bf, sizeof(bf), 1, f);
    fwrite(&bi, sizeof(bi), 1, f);
    fwrite(pixels.data(), pixels.size(), 1, f);
    fclose(f);
}

static int g_tries = 0;
static void CALLBACK Snap(HWND, UINT, UINT_PTR id, DWORD) {
    HWND menu = FindWindowW(L"#32768", nullptr);
    if (!menu || !IsWindowVisible(menu)) {
        if (++g_tries < 20) return;   // espera o menu aparecer (até ~6 s); depois desiste e fecha
        KillTimer(g_owner, id);
        EndMenu();
        return;
    }
    KillTimer(g_owner, id);
    Sleep(400);   // fim da animação de abrir
    RECT r;
    GetWindowRect(menu, &r);
    const int w = r.right - r.left, h = r.bottom - r.top;
    HDC screen = GetDC(nullptr);
    HDC mem = CreateCompatibleDC(screen);
    HBITMAP bmp = CreateCompatibleBitmap(screen, w, h);
    HGDIOBJ old = SelectObject(mem, bmp);
    PrintWindow(menu, mem, 2 /* PW_RENDERFULLCONTENT */);
    SelectObject(mem, old);
    SaveBmp(bmp, w, h, g_out);
    DeleteObject(bmp);
    DeleteDC(mem);
    ReleaseDC(nullptr, screen);
    wprintf(L"menu %dx%d salvo\n", w, h);
    EndMenu();
}

static LRESULT CALLBACK Proc(HWND h, UINT m, WPARAM w, LPARAM l) {
    IContextMenu3* cm3 = nullptr;
    if (g_cm && (m == WM_INITMENUPOPUP || m == WM_DRAWITEM || m == WM_MEASUREITEM || m == WM_MENUCHAR) &&
        SUCCEEDED(g_cm->QueryInterface(IID_PPV_ARGS(&cm3)))) {
        LRESULT r = 0;
        const HRESULT hr = cm3->HandleMenuMsg2(m, w, l, &r);
        cm3->Release();
        if (SUCCEEDED(hr)) return r;
    }
    return DefWindowProcW(h, m, w, l);
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 3) return 2;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    g_out = argv[2];
    const bool full = argc > 3 && wcscmp(argv[3], L"--full") == 0;
    WNDCLASSW wc{};
    wc.lpfnWndProc = Proc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"MenuShot";
    RegisterClassW(&wc);
    g_owner = CreateWindowExW(WS_EX_TOOLWINDOW, L"MenuShot", L"", WS_POPUP, 0, 0, 0, 0, nullptr, nullptr, wc.hInstance, nullptr);
    PIDLIST_ABSOLUTE pidl = nullptr;
    if (FAILED(SHParseDisplayName(argv[1], nullptr, &pidl, 0, nullptr))) return 3;
    IShellFolder* parent = nullptr;
    PCUITEMID_CHILD child = nullptr;
    SHBindToParent(pidl, IID_PPV_ARGS(&parent), &child);
    parent->GetUIObjectOf(g_owner, 1, &child, IID_IContextMenu, nullptr, reinterpret_cast<void**>(&g_cm));
    HMENU menu = CreatePopupMenu();
    g_cm->QueryContextMenu(menu, 0, 1, 0x7FFF, CMF_NORMAL);
    HMENU show = full ? menu : FindSub(menu, L"OneDrive 365");
    if (!show) { wprintf(L"submenu não encontrado\n"); return 4; }
    SetForegroundWindow(g_owner);
    SetTimer(g_owner, 1, 300, Snap);
    TrackPopupMenuEx(show, TPM_RETURNCMD | TPM_NONOTIFY, 400, 300, g_owner, nullptr);
    DestroyMenu(menu);
    g_cm->Release();
    parent->Release();
    CoTaskMemFree(pidl);
    DestroyWindow(g_owner);
    CoUninitialize();
    return 0;
}
