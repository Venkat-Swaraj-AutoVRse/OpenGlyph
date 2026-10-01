/* dialog_win32.c — editor DLL native file dialog (2 of 8 exports), Milestone 2.
 * Matches Editor/NativeFileDialog.cs: unitext_open_files_dialog(title, filters, initialDir)
 * where args are UTF-8 C strings; filters is a comma-separated extension list ("ttf,otf,ttc").
 * Returns a newline-separated UTF-8 list of selected paths (malloc'd; freed by
 * unitext_free_dialog_result), or NULL if cancelled. Multi-select via GetOpenFileNameW. */
#include "ut_native.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commdlg.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <wchar.h>

static wchar_t* utf8_to_wide(const char* s) {
    if (!s) return NULL;
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    wchar_t* w = (wchar_t*)malloc((size_t)n * sizeof(wchar_t));
    if (w) MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n);
    return w;
}
static char* wide_to_utf8(const wchar_t* w) {
    if (!w) return NULL;
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
    char* s = (char*)malloc((size_t)n);
    if (s) WideCharToMultiByte(CP_UTF8, 0, w, -1, s, n, NULL, NULL);
    return s;
}

/* Build a Win32 filter string "Fonts\0*.ttf;*.otf\0All\0*.*\0\0" from "ttf,otf". */
static void build_filter(const char* filtersUtf8, wchar_t* out, size_t cap) {
    wchar_t pat[512]; pat[0] = 0;
    if (filtersUtf8 && *filtersUtf8) {
        wchar_t* wf = utf8_to_wide(filtersUtf8);
        size_t p = 0;
        for (wchar_t* tok = wf; tok && *tok; ) {
            wchar_t* comma = wcschr(tok, L',');
            size_t len = comma ? (size_t)(comma - tok) : wcslen(tok);
            if (p + len + 3 < 512) { pat[p++]=L'*'; pat[p++]=L'.'; wcsncpy(pat+p, tok, len); p+=len; if (comma) pat[p++]=L';'; }
            tok = comma ? comma + 1 : NULL;
        }
        pat[p] = 0;
        free(wf);
    } else { wcscpy(pat, L"*.*"); }
    /* compose double-null-terminated filter */
    size_t o = 0;
    const wchar_t* label = L"Fonts";
    for (const wchar_t* c = label; *c && o < cap-1; ++c) out[o++] = *c; out[o++] = 0;
    for (const wchar_t* c = pat; *c && o < cap-1; ++c) out[o++] = *c; out[o++] = 0;
    const wchar_t* all = L"All Files"; for (const wchar_t* c = all; *c && o < cap-1; ++c) out[o++] = *c; out[o++] = 0;
    const wchar_t* allp = L"*.*"; for (const wchar_t* c = allp; *c && o < cap-1; ++c) out[o++] = *c; out[o++] = 0;
    out[o] = 0;
}

UT_API char* unitext_open_files_dialog(const char* titleUtf8, const char* filtersUtf8, const char* initialDirUtf8) {
    wchar_t filter[1024]; build_filter(filtersUtf8, filter, 1024);
    wchar_t* wtitle = utf8_to_wide(titleUtf8);
    wchar_t* wdir = utf8_to_wide(initialDirUtf8);

    static wchar_t buf[32768]; buf[0] = 0;
    OPENFILENAMEW ofn; memset(&ofn, 0, sizeof(ofn));
    ofn.lStructSize = sizeof(ofn);
    ofn.lpstrFilter = filter;
    ofn.lpstrFile = buf;
    ofn.nMaxFile = 32768;
    ofn.lpstrTitle = wtitle;
    ofn.lpstrInitialDir = wdir;
    ofn.Flags = OFN_EXPLORER | OFN_ALLOWMULTISELECT | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;

    char* result = NULL;
    if (GetOpenFileNameW(&ofn)) {
        /* Multi-select: dir\0file1\0file2\0\0 ; single: fullpath\0\0 */
        wchar_t* dir = buf;
        wchar_t* first = buf + wcslen(buf) + 1;
        /* accumulate UTF-8 newline-joined */
        size_t cap = 4096, len = 0;
        result = (char*)malloc(cap); if (result) result[0] = 0;
        if (*first == 0) {
            /* single selection: 'dir' is the full path */
            char* u = wide_to_utf8(dir);
            if (u && result) { strncpy(result, u, cap-1); len = strlen(result); }
            free(u);
        } else {
            wchar_t path[MAX_PATH*2];
            for (wchar_t* f = first; *f; f += wcslen(f) + 1) {
                swprintf(path, MAX_PATH*2, L"%s\\%s", dir, f);
                char* u = wide_to_utf8(path);
                if (u && result) {
                    size_t ul = strlen(u);
                    if (len + ul + 2 > cap) { cap = (len+ul+2)*2; result = (char*)realloc(result, cap); }
                    if (result) { if (len) result[len++]='\n'; memcpy(result+len, u, ul); len+=ul; result[len]=0; }
                }
                free(u);
            }
        }
    }
    free(wtitle); free(wdir);
    return result;
}

UT_API void unitext_free_dialog_result(char* result) { if (result) free(result); }

#else
UT_API char* unitext_open_files_dialog(const char* t, const char* f, const char* d) {(void)t;(void)f;(void)d;return 0;}
UT_API void unitext_free_dialog_result(char* r) {(void)r;}
#endif
