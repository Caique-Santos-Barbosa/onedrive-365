// Monta o menu de botão direito de um caminho como o Explorer faz (IContextMenu) e imprime os itens,
// incluindo submenus. Uso: test_ctx.exe <caminho> [<caminho>...]
#include <windows.h>
#include <shlobj.h>
#include <shobjidl_core.h>
#include <cstdio>
#include <string>

static void Dump(HMENU menu, IContextMenu* cm, int depth) {
    const int n = GetMenuItemCount(menu);
    for (int i = 0; i < n; ++i) {
        wchar_t text[256] = L"";
        MENUITEMINFOW mii{sizeof(mii)};
        mii.fMask = MIIM_STRING | MIIM_SUBMENU | MIIM_FTYPE | MIIM_STATE;
        mii.dwTypeData = text;
        mii.cch = 255;
        if (!GetMenuItemInfoW(menu, i, TRUE, &mii) || (mii.fType & MFT_SEPARATOR)) continue;
        wprintf(L"%*s%s%s\n", depth * 4, L"", text, (mii.fState & MFS_DISABLED) ? L"  (desativado)" : L"");
        if (mii.hSubMenu) {
            IContextMenu3* cm3 = nullptr;
            if (SUCCEEDED(cm->QueryInterface(IID_PPV_ARGS(&cm3)))) {
                LRESULT r = 0;
                cm3->HandleMenuMsg2(WM_INITMENUPOPUP, (WPARAM)mii.hSubMenu, i, &r);
                cm3->Release();
            }
            Dump(mii.hSubMenu, cm, depth + 1);
        }
    }
}

int wmain(int argc, wchar_t** argv) {
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    for (int a = 1; a < argc; ++a) {
        wprintf(L"== %s\n", argv[a]);
        PIDLIST_ABSOLUTE pidl = nullptr;
        if (FAILED(SHParseDisplayName(argv[a], nullptr, &pidl, 0, nullptr))) { wprintf(L"(caminho inválido)\n"); continue; }
        IShellFolder* parent = nullptr;
        PCUITEMID_CHILD child = nullptr;
        if (SUCCEEDED(SHBindToParent(pidl, IID_PPV_ARGS(&parent), &child))) {
            IContextMenu* cm = nullptr;
            if (SUCCEEDED(parent->GetUIObjectOf(nullptr, 1, &child, IID_IContextMenu, nullptr, reinterpret_cast<void**>(&cm)))) {
                HMENU menu = CreatePopupMenu();
                cm->QueryContextMenu(menu, 0, 1, 0x7FFF, CMF_NORMAL);
                Dump(menu, cm, 1);
                DestroyMenu(menu);
                cm->Release();
            }
            parent->Release();
        }
        CoTaskMemFree(pidl);
    }
    CoUninitialize();
    return 0;
}
