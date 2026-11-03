// neoplug (H0 spike): Windows helpers for the GUI checks: a child HWND to host a plugin view, a
// floating window, a description of the window tree and a snapshot (BMP) with colour statistics.
#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <cstdio>
#include <set>
#include <string>
#include <vector>

#include "np_internal.h"

static const wchar_t* containerClass()
{
    static bool done = false;
    if (!done)
    {
        WNDCLASSW wc{};
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"NeoPlugContainer";
        wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        RegisterClassW(&wc);
        done = true;
    }
    return L"NeoPlugContainer";
}

extern "C" {

NP_API void* np_platform_container(void* parent, int width, int height)
{
    DWORD style = parent ? (WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN) : (WS_POPUP | WS_CLIPCHILDREN);
    return CreateWindowExW(0, containerClass(), L"", style, 0, 0, width, height, (HWND)parent, nullptr, GetModuleHandleW(nullptr), nullptr);
}

NP_API void* np_platform_window(const char* title, int width, int height)
{
    std::wstring t;
    for (const char* c = title ? title : "neoplug"; *c; c++) t += (wchar_t)(unsigned char)*c;
    RECT r{0, 0, width, height};
    AdjustWindowRect(&r, WS_OVERLAPPEDWINDOW, FALSE);
    HWND w = CreateWindowExW(0, containerClass(), t.c_str(), WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN | WS_VISIBLE, 120, 120, r.right - r.left,
                             r.bottom - r.top, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    ShowWindow(w, SW_SHOW);
    UpdateWindow(w);
    return w;
}

struct DescribeCtx { std::string* out; int depth; };

static void describeOne(HWND h, int depth, std::string& out)
{
    char cls[128] = {}, title[128] = {};
    GetClassNameA(h, cls, sizeof cls);
    GetWindowTextA(h, title, sizeof title);
    RECT r{};
    GetWindowRect(h, &r);
    char line[512];
    snprintf(line, sizeof line, "%*s%s \"%s\" rect=(%ld,%ld %ldx%ld)%s\n", depth * 2, "", cls, title, r.left, r.top, r.right - r.left,
             r.bottom - r.top, IsWindowVisible(h) ? "" : " hidden");
    out += line;
    for (HWND c = GetWindow(h, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT)) describeOne(c, depth + 1, out);
}

NP_API int np_platform_describe(void* view, char* buf, int len)
{
    HWND h = (HWND)view;
    if (!h) return -1;
    std::string out;
    HWND root = GetAncestor(h, GA_ROOT);
    char cls[128] = {}, title[128] = {};
    GetClassNameA(root, cls, sizeof cls);
    GetWindowTextA(root, title, sizeof title);
    out += std::string("window: ") + title + " class=" + cls + "\n";
    std::string chain;
    for (HWND a = GetParent(h); a; a = GetParent(a))
    {
        GetClassNameA(a, cls, sizeof cls);
        chain = std::string(cls) + (chain.empty() ? "" : " > ") + chain;
    }
    out += "ancestors: " + (chain.empty() ? std::string("(none)") : chain) + "\n";
    describeOne(h, 0, out);
    np::copyStr(buf, (size_t)len, out);
    return (int)out.size();
}

NP_API int np_platform_snapshot(void* view, const char* path, int rgb, char* statsOut, int len)
{
    HWND h = (HWND)view;
    if (!h) return -1;
    RECT r{};
    GetClientRect(h, &r);
    int w = r.right, ht = r.bottom;
    if (w <= 0 || ht <= 0) return -1;
    HDC src = GetDC(h);
    HDC mem = CreateCompatibleDC(src);
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof bi.bmiHeader;
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -ht;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* bits = nullptr;
    HBITMAP bmp = CreateDIBSection(mem, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    HGDIOBJ old = SelectObject(mem, bmp);
    // PrintWindow renders child windows too (PW_RENDERFULLCONTENT = 2); BitBlt is the fallback.
    if (!PrintWindow(h, mem, PW_CLIENTONLY | 2)) BitBlt(mem, 0, 0, w, ht, src, 0, 0, SRCCOPY);
    GdiFlush();
    const uint8_t* px = (const uint8_t*)bits;
    std::set<uint32_t> colors;
    long match = 0, total = 0;
    int tr = (rgb >> 16) & 255, tg = (rgb >> 8) & 255, tb = rgb & 255;
    for (int y = 0; y < ht; y += 2)
        for (int x = 0; x < w; x += 2)
        {
            const uint8_t* p = px + ((size_t)y * w + x) * 4;
            int b = p[0], g = p[1], rr = p[2];
            colors.insert((uint32_t)((rr << 16) | (g << 8) | b));
            total++;
            if (abs(rr - tr) <= 24 && abs(g - tg) <= 24 && abs(b - tb) <= 24) match++;
        }
    if (path)
    {
        FILE* f = fopen(path, "wb");
        if (f)
        {
            BITMAPFILEHEADER fh{};
            fh.bfType = 0x4D42;
            fh.bfOffBits = sizeof fh + sizeof bi.bmiHeader;
            fh.bfSize = fh.bfOffBits + (DWORD)w * ht * 4;
            fwrite(&fh, sizeof fh, 1, f);
            fwrite(&bi.bmiHeader, sizeof bi.bmiHeader, 1, f);
            fwrite(bits, (size_t)w * ht * 4, 1, f);
            fclose(f);
        }
    }
    SelectObject(mem, old);
    DeleteObject(bmp);
    DeleteDC(mem);
    ReleaseDC(h, src);
    char buf[256];
    snprintf(buf, sizeof buf, "{\"view\":{\"width\":%d,\"height\":%d,\"colors\":%zu,\"match_permille\":%ld},\"window\":null}", w, ht,
             colors.size(), total ? match * 1000 / total : 0L);
    np::copyStr(statsOut, (size_t)len, buf);
    return 0;
}

NP_API void np_platform_run(double seconds)
{
    DWORD end = GetTickCount() + (DWORD)(seconds * 1000);
    while ((long)(end - GetTickCount()) > 0)
    {
        MSG m;
        while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&m);
            DispatchMessageW(&m);
        }
        Sleep(10);
    }
}

NP_API void np_platform_release(void* handle)
{
    if (handle) DestroyWindow((HWND)handle);
}

} // extern "C"
#endif
