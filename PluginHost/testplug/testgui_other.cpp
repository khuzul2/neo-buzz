// neoplug-test.clap GUI: a solid colour child window on Windows; nothing elsewhere (macOS: testgui_mac.mm).
#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

static LRESULT CALLBACK panelProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (msg == WM_PAINT)
    {
        PAINTSTRUCT ps;
        HDC dc = BeginPaint(hwnd, &ps);
        RECT r;
        GetClientRect(hwnd, &r);
        COLORREF c = (COLORREF)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        HBRUSH b = CreateSolidBrush(c);
        FillRect(dc, &r, b);
        DeleteObject(b);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(255, 255, 255));
        DrawTextW(dc, L"NeoPlug test plugin", -1, &r, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        EndPaint(hwnd, &ps);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

extern "C" int testgui_supported(void) { return 1; }

extern "C" void* testgui_create(void* parent, int w, int h, int rgb)
{
    static bool registered = false;
    HINSTANCE inst = GetModuleHandleW(nullptr);
    if (!registered)
    {
        WNDCLASSW wc{};
        wc.lpfnWndProc = panelProc;
        wc.hInstance = inst;
        wc.lpszClassName = L"NeoPlugTestPanel";
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        RegisterClassW(&wc);
        registered = true;
    }
    HWND hwnd = CreateWindowExW(0, L"NeoPlugTestPanel", L"", WS_CHILD | WS_VISIBLE, 0, 0, w, h, (HWND)parent, nullptr, inst, nullptr);
    if (!hwnd) return nullptr;
    SetWindowLongPtrW(hwnd, GWLP_USERDATA, (LONG_PTR)RGB((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255));
    InvalidateRect(hwnd, nullptr, TRUE);
    return hwnd;
}

extern "C" void testgui_destroy(void* view)
{
    if (view) DestroyWindow((HWND)view);
}

#elif !defined(__APPLE__)
extern "C" int testgui_supported(void) { return 0; }
extern "C" void* testgui_create(void*, int, int, int) { return nullptr; }
extern "C" void testgui_destroy(void*) {}
#endif
