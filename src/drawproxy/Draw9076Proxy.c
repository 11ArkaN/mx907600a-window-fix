#include <limits.h>
#include <stdint.h>
#include <string.h>
#include <windows.h>
#include <tlhelp32.h>

typedef int (__stdcall *CreateAPMFileFunc)(intptr_t, intptr_t, intptr_t, intptr_t, intptr_t, intptr_t);
typedef int (__stdcall *DrawDemoVersionFunc)(intptr_t, intptr_t);
typedef int (__stdcall *DrawGraphFunc)(HWND, void*, void*, int, int, int, int, short, short, short, short);
typedef int (__stdcall *DrawSetPairFunc)(int, int);

static HMODULE g_realModule = NULL;
static HMODULE g_selfModule = NULL;
static LONG g_hooksInstalled = 0;
static LONG g_hooksInstalling = 0;

static CreateAPMFileFunc g_createApmFile = NULL;
static DrawDemoVersionFunc g_drawDemoVersion = NULL;
static DrawGraphFunc g_drawGraph = NULL;
static DrawGraphFunc g_drawGraph2 = NULL;
static DrawSetPairFunc g_drawSetOffset = NULL;
static DrawSetPairFunc g_drawSetReso = NULL;
static BOOL (WINAPI *g_originalSetWindowPos)(HWND, HWND, int, int, int, int, UINT) = NULL;
static BOOL (WINAPI *g_originalMoveWindow)(HWND, int, int, int, int, BOOL) = NULL;

typedef struct
{
    HWND hwnd;
    LONG top;
    LONG area;
} BottomPanelCandidate;

typedef struct
{
    HWND hwnd;
} MarkerPositionCandidate;

static void EnsureHooksInstalled(void);
static void PatchModuleImports(HMODULE moduleBase);
static BOOL TryAdjustMarkerMovePosition(HWND hwnd, int* x, int* y);
static BOOL IsMarkerMovePanel(HWND hwnd);
static BOOL CALLBACK FindBottomPanelCallback(HWND hwnd, LPARAM lParam);
static BOOL CALLBACK FindMarkerPositionCallback(HWND hwnd, LPARAM lParam);
BOOL WINAPI HookSetWindowPos(HWND hwnd, HWND hwndInsertAfter, int x, int y, int cx, int cy, UINT flags);
BOOL WINAPI HookMoveWindow(HWND hwnd, int x, int y, int width, int height, BOOL repaint);

static void BuildSiblingPath(char* buffer, size_t bufferSize, const char* replacementName)
{
    DWORD length = GetModuleFileNameA(g_selfModule, buffer, (DWORD)bufferSize);
    if (length == 0 || length >= bufferSize)
    {
        buffer[0] = '\0';
        return;
    }

    for (DWORD i = length; i > 0; i--)
    {
        if (buffer[i - 1] == '\\' || buffer[i - 1] == '/')
        {
            buffer[i] = '\0';
            strncat(buffer, replacementName, bufferSize - strlen(buffer) - 1);
            return;
        }
    }

    buffer[0] = '\0';
}

static FARPROC ResolveExport(const char* name)
{
    if (g_realModule == NULL)
    {
        return NULL;
    }

    return GetProcAddress(g_realModule, name);
}

static void EnsureRealModuleLoaded(void)
{
    if (g_realModule != NULL)
    {
        return;
    }

    char path[MAX_PATH];
    BuildSiblingPath(path, sizeof(path), "Draw9076.real.dll");
    if (path[0] == '\0')
    {
        return;
    }

    g_realModule = LoadLibraryA(path);
    if (g_realModule == NULL)
    {
        return;
    }

    g_createApmFile = (CreateAPMFileFunc)ResolveExport("_CreateAPMFile@24");
    g_drawDemoVersion = (DrawDemoVersionFunc)ResolveExport("_DrawDemoVersion@8");
    g_drawGraph = (DrawGraphFunc)ResolveExport("_DrawGraph@44");
    g_drawGraph2 = (DrawGraphFunc)ResolveExport("_DrawGraph2@44");
    g_drawSetOffset = (DrawSetPairFunc)ResolveExport("_DrawSetOffset@8");
    g_drawSetReso = (DrawSetPairFunc)ResolveExport("_DrawSetReso@8");
}

static void AdjustGraphSize(HWND hwnd, short requestedWidth, short requestedHeight, short* widthOut, short* heightOut)
{
    *widthOut = requestedWidth;
    *heightOut = requestedHeight;

    if (!IsWindow(hwnd))
    {
        return;
    }

    RECT rect;
    if (!GetClientRect(hwnd, &rect))
    {
        return;
    }

    int width = rect.right - rect.left;
    int height = rect.bottom - rect.top;
    if (width <= 0 || height <= 0)
    {
        return;
    }

    if (width > SHRT_MAX)
    {
        width = SHRT_MAX;
    }

    if (height > SHRT_MAX)
    {
        height = SHRT_MAX;
    }

    *widthOut = (short)width;
    *heightOut = (short)height;
}

static BOOL ClassNameEquals(HWND hwnd, const char* expected)
{
    char className[128];

    if (!IsWindow(hwnd))
    {
        return FALSE;
    }

    if (GetClassNameA(hwnd, className, sizeof(className)) <= 0)
    {
        return FALSE;
    }

    return lstrcmpiA(className, expected) == 0;
}

static int CountChildrenWithClass(HWND parent, const char* expectedClass)
{
    int count = 0;
    HWND child = GetWindow(parent, GW_CHILD);
    while (child != NULL)
    {
        if (ClassNameEquals(child, expectedClass))
        {
            count++;
        }

        child = GetWindow(child, GW_HWNDNEXT);
    }

    return count;
}

static BOOL IsMarkerMovePanel(HWND hwnd)
{
    RECT rect;

    if (!ClassNameEquals(hwnd, "ThunderRT6Frame"))
    {
        return FALSE;
    }

    if (GetWindowTextLengthA(hwnd) != 0)
    {
        return FALSE;
    }

    if (!GetWindowRect(hwnd, &rect))
    {
        return FALSE;
    }

    if ((rect.right - rect.left) < 250 || (rect.right - rect.left) > 700)
    {
        return FALSE;
    }

    if ((rect.bottom - rect.top) < 100 || (rect.bottom - rect.top) > 350)
    {
        return FALSE;
    }

    return CountChildrenWithClass(hwnd, "AfxWnd40") >= 3;
}

static BOOL CALLBACK FindBottomPanelCallback(HWND hwnd, LPARAM lParam)
{
    BottomPanelCandidate* candidate = (BottomPanelCandidate*)lParam;
    RECT rect;
    LONG area;

    if (!IsWindowVisible(hwnd) || !ClassNameEquals(hwnd, "SSTabCtlWndClass"))
    {
        return TRUE;
    }

    if (!GetWindowRect(hwnd, &rect))
    {
        return TRUE;
    }

    if ((rect.right - rect.left) < 500 || (rect.bottom - rect.top) < 120)
    {
        return TRUE;
    }

    area = (rect.right - rect.left) * (rect.bottom - rect.top);
    if (candidate->hwnd == NULL || rect.top > candidate->top || (rect.top == candidate->top && area > candidate->area))
    {
        candidate->hwnd = hwnd;
        candidate->top = rect.top;
        candidate->area = area;
    }

    return TRUE;
}

static BOOL CALLBACK FindMarkerPositionCallback(HWND hwnd, LPARAM lParam)
{
    MarkerPositionCandidate* candidate = (MarkerPositionCandidate*)lParam;
    char title[128];

    if (!IsWindowVisible(hwnd) || !ClassNameEquals(hwnd, "ThunderRT6Frame"))
    {
        return TRUE;
    }

    if (GetWindowTextA(hwnd, title, sizeof(title)) <= 0)
    {
        return TRUE;
    }

    if (strstr(title, "Marker Position") != NULL)
    {
        candidate->hwnd = hwnd;
        return FALSE;
    }

    return TRUE;
}

static BOOL TryAdjustMarkerMovePosition(HWND hwnd, int* x, int* y)
{
    HWND parent;
    HWND root;
    BottomPanelCandidate bottomPanel;
    MarkerPositionCandidate markerPosition;
    RECT panelRect;
    RECT bottomRect;
    RECT markerRect;
    RECT rootRect;
    POINT target;
    int panelWidth;
    int minLeftScreen;
    int maxLeftScreen;

    if (!IsMarkerMovePanel(hwnd))
    {
        return FALSE;
    }

    parent = GetParent(hwnd);
    root = GetAncestor(hwnd, GA_ROOT);
    if (parent == NULL || root == NULL)
    {
        return FALSE;
    }

    bottomPanel.hwnd = NULL;
    bottomPanel.top = LONG_MIN;
    bottomPanel.area = 0;
    EnumChildWindows(root, FindBottomPanelCallback, (LPARAM)&bottomPanel);
    if (bottomPanel.hwnd == NULL || !GetWindowRect(bottomPanel.hwnd, &bottomRect))
    {
        return FALSE;
    }

    markerPosition.hwnd = NULL;
    EnumChildWindows(root, FindMarkerPositionCallback, (LPARAM)&markerPosition);

    if (!GetWindowRect(hwnd, &panelRect))
    {
        return FALSE;
    }

    if (!GetWindowRect(root, &rootRect))
    {
        return FALSE;
    }

    panelWidth = panelRect.right - panelRect.left;
    minLeftScreen = rootRect.left + 16;
    maxLeftScreen = rootRect.right - panelWidth - 16;

    target.x = bottomRect.right + 16;
    target.y = bottomRect.top;

    if (markerPosition.hwnd != NULL && GetWindowRect(markerPosition.hwnd, &markerRect))
    {
        int markerBound = markerRect.left - panelWidth - 16;
        if (target.x > markerBound)
        {
            target.x = markerBound;
        }
    }

    if (target.x < minLeftScreen)
    {
        target.x = minLeftScreen;
    }

    if (target.x > maxLeftScreen)
    {
        target.x = maxLeftScreen;
    }

    ScreenToClient(parent, &target);
    *x = target.x;
    *y = target.y;
    return TRUE;
}

static void PatchModuleImports(HMODULE moduleBase)
{
    BYTE* base = (BYTE*)moduleBase;
    IMAGE_DOS_HEADER* dosHeader;
    IMAGE_NT_HEADERS* ntHeaders;
    IMAGE_IMPORT_DESCRIPTOR* importDescriptor;

    if (moduleBase == NULL || moduleBase == g_selfModule)
    {
        return;
    }

    dosHeader = (IMAGE_DOS_HEADER*)base;
    if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return;
    }

    ntHeaders = (IMAGE_NT_HEADERS*)(base + dosHeader->e_lfanew);
    if (ntHeaders->Signature != IMAGE_NT_SIGNATURE)
    {
        return;
    }

    if (ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress == 0)
    {
        return;
    }

    importDescriptor = (IMAGE_IMPORT_DESCRIPTOR*)(base + ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress);
    while (importDescriptor->Name != 0)
    {
        const char* libraryName = (const char*)(base + importDescriptor->Name);
        IMAGE_THUNK_DATA* thunk = (IMAGE_THUNK_DATA*)(base + importDescriptor->FirstThunk);
        IMAGE_THUNK_DATA* originalThunk = importDescriptor->OriginalFirstThunk != 0
            ? (IMAGE_THUNK_DATA*)(base + importDescriptor->OriginalFirstThunk)
            : NULL;

        if (libraryName != NULL && lstrcmpiA(libraryName, "USER32.dll") == 0)
        {
            while (thunk->u1.Function != 0)
            {
                const char* importName = NULL;

                if (originalThunk != NULL && !IMAGE_SNAP_BY_ORDINAL(originalThunk->u1.Ordinal))
                {
                    IMAGE_IMPORT_BY_NAME* importByName = (IMAGE_IMPORT_BY_NAME*)(base + originalThunk->u1.AddressOfData);
                    importName = (const char*)importByName->Name;
                }

                if (importName != NULL)
                {
                    FARPROC replacement = NULL;
                    DWORD oldProtect;

                    if (lstrcmpiA(importName, "SetWindowPos") == 0)
                    {
                        replacement = (FARPROC)HookSetWindowPos;
                    }
                    else if (lstrcmpiA(importName, "MoveWindow") == 0)
                    {
                        replacement = (FARPROC)HookMoveWindow;
                    }

                    if (replacement != NULL &&
                        VirtualProtect(&thunk->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProtect))
                    {
                        thunk->u1.Function = (uintptr_t)replacement;
                        VirtualProtect(&thunk->u1.Function, sizeof(void*), oldProtect, &oldProtect);
                    }
                }

                thunk++;
                if (originalThunk != NULL)
                {
                    originalThunk++;
                }
            }
        }

        importDescriptor++;
    }
}

static void EnsureHooksInstalled(void)
{
    HANDLE snapshot;
    MODULEENTRY32 moduleEntry;

    if (g_hooksInstalled != 0)
    {
        return;
    }

    if (InterlockedCompareExchange(&g_hooksInstalling, 1, 0) != 0)
    {
        return;
    }

    g_originalSetWindowPos = (BOOL (WINAPI*)(HWND, HWND, int, int, int, int, UINT))GetProcAddress(GetModuleHandleA("USER32.dll"), "SetWindowPos");
    g_originalMoveWindow = (BOOL (WINAPI*)(HWND, int, int, int, int, BOOL))GetProcAddress(GetModuleHandleA("USER32.dll"), "MoveWindow");
    if (g_originalSetWindowPos == NULL || g_originalMoveWindow == NULL)
    {
        InterlockedExchange(&g_hooksInstalling, 0);
        return;
    }

    snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        InterlockedExchange(&g_hooksInstalling, 0);
        return;
    }

    ZeroMemory(&moduleEntry, sizeof(moduleEntry));
    moduleEntry.dwSize = sizeof(moduleEntry);
    if (Module32First(snapshot, &moduleEntry))
    {
        do
        {
            PatchModuleImports(moduleEntry.hModule);
        }
        while (Module32Next(snapshot, &moduleEntry));
    }

    CloseHandle(snapshot);
    InterlockedExchange(&g_hooksInstalled, 1);
    InterlockedExchange(&g_hooksInstalling, 0);
}

BOOL WINAPI HookSetWindowPos(HWND hwnd, HWND hwndInsertAfter, int x, int y, int cx, int cy, UINT flags)
{
    EnsureHooksInstalled();

    if (g_originalSetWindowPos == NULL)
    {
        return FALSE;
    }

    TryAdjustMarkerMovePosition(hwnd, &x, &y);
    return g_originalSetWindowPos(hwnd, hwndInsertAfter, x, y, cx, cy, flags);
}

BOOL WINAPI HookMoveWindow(HWND hwnd, int x, int y, int width, int height, BOOL repaint)
{
    EnsureHooksInstalled();

    if (g_originalMoveWindow == NULL)
    {
        return FALSE;
    }

    TryAdjustMarkerMovePosition(hwnd, &x, &y);
    return g_originalMoveWindow(hwnd, x, y, width, height, repaint);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        g_selfModule = instance;
        DisableThreadLibraryCalls(instance);
        EnsureRealModuleLoaded();
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        if (g_realModule != NULL)
        {
            FreeLibrary(g_realModule);
            g_realModule = NULL;
        }
    }

    return TRUE;
}

int __stdcall CreateAPMFile(intptr_t a1, intptr_t a2, intptr_t a3, intptr_t a4, intptr_t a5, intptr_t a6)
{
    EnsureRealModuleLoaded();
    EnsureHooksInstalled();
    return g_createApmFile != NULL ? g_createApmFile(a1, a2, a3, a4, a5, a6) : 0;
}

int __stdcall DrawDemoVersion(intptr_t a1, intptr_t a2)
{
    EnsureRealModuleLoaded();
    EnsureHooksInstalled();
    return g_drawDemoVersion != NULL ? g_drawDemoVersion(a1, a2) : 0;
}

int __stdcall DrawGraph(HWND hwnd, void* a2, void* a3, int a4, int a5, int a6, int a7, short width, short height, short a10, short a11)
{
    short adjustedWidth;
    short adjustedHeight;

    EnsureRealModuleLoaded();
    EnsureHooksInstalled();
    if (g_drawGraph == NULL)
    {
        return 0;
    }

    AdjustGraphSize(hwnd, width, height, &adjustedWidth, &adjustedHeight);
    return g_drawGraph(hwnd, a2, a3, a4, a5, a6, a7, adjustedWidth, adjustedHeight, a10, a11);
}

int __stdcall DrawGraph2(HWND hwnd, void* a2, void* a3, int a4, int a5, int a6, int a7, short width, short height, short a10, short a11)
{
    short adjustedWidth;
    short adjustedHeight;

    EnsureRealModuleLoaded();
    EnsureHooksInstalled();
    if (g_drawGraph2 == NULL)
    {
        return 0;
    }

    AdjustGraphSize(hwnd, width, height, &adjustedWidth, &adjustedHeight);
    return g_drawGraph2(hwnd, a2, a3, a4, a5, a6, a7, adjustedWidth, adjustedHeight, a10, a11);
}

int __stdcall DrawSetOffset(int x, int y)
{
    EnsureRealModuleLoaded();
    EnsureHooksInstalled();
    return g_drawSetOffset != NULL ? g_drawSetOffset(x, y) : 0;
}

int __stdcall DrawSetReso(int x, int y)
{
    EnsureRealModuleLoaded();
    EnsureHooksInstalled();
    return g_drawSetReso != NULL ? g_drawSetReso(x, y) : 0;
}
