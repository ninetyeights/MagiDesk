#include <windows.h>
#include <windowsx.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <exdisp.h>
#include <shlguid.h>
#include <wrl/client.h>
#include <strsafe.h>
#include <limits.h>

// Bounded experiment. Optional target suppression affects painting only, never files.
struct SharedState {
    HWND list;
    HWND parent;
    DWORD processId;
    volatile LONG callbacks;
    volatile LONG notifications;
    volatile LONG customDraw;
    volatile LONG itemDraw;
    volatile LONG prePaint;
    volatile LONG postPaint;
    volatile LONG otherStage;
    volatile LONG notifyItem;
    volatile LONG skipDefault;
    volatile LONG defaultDraw;
    volatile LONG prePaintFlags;
    volatile LONG attempted, installed, restored, installError, modified;
    ULONGLONG deadline;
    WCHAR target[32768];
    volatile LONG matched, suppressed, identityError;
    volatile LONG blockedMouse, blockedSelection, blockedRename, interactionReady;
    BOOL protectInteraction;
    volatile LONG ownerData, stateEvents, correctedSelection, correctionFailures;
    volatile LONG navigationSkips, navigationBoundary;
    DWORD ownerPid;
    volatile LONG ownerExited, backgroundMenus, menuError;
};
#pragma data_seg(".mdprobe")
SharedState state = {};
#pragma data_seg()

struct ProbeResult {
    DWORD error;
    LONG callbacks;
    LONG notifications;
    LONG customDraw;
    LONG itemDraw;
    LONG prePaint, postPaint, otherStage;
    LONG notifyItem, skipDefault, defaultDraw, prePaintFlags;
    LONG attempted, installed, restored, installError, modified;
    LONG matched, suppressed, identityError;
    LONG blockedMouse, blockedSelection, blockedRename, interactionReady;
    LONG ownerData, stateEvents, correctedSelection, correctionFailures;
    LONG navigationSkips, navigationBoundary;
    LONG ownerExited, backgroundMenus, menuError;
};
static_assert(sizeof(ProbeResult) == 132);

// These variables belong only to the Explorer-side DLL, not the shared section.
static UINT_PTR restoreTimer{};
static bool subclassActive{};
static bool inputSubclassActive{};
static constexpr UINT protectedStates = LVIS_SELECTED | LVIS_FOCUSED | LVIS_DROPHILITED;
static Microsoft::WRL::ComPtr<IFolderView> folderView;
static Microsoft::WRL::ComPtr<IShellFolder> shellFolder;
static bool resolvingItem{};
static bool correctingSelection{};
static HANDLE ownerProcess{};
static Microsoft::WRL::ComPtr<IShellView> desktopShellView;
static LRESULT CALLBACK RequestItems(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);
static LRESULT CALLBACK FilterInput(HWND, UINT, WPARAM, LPARAM, UINT_PTR, DWORD_PTR);

static HRESULT OpenDesktopView() {
    using Microsoft::WRL::ComPtr;
    ComPtr<IShellWindows> windows;
    HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&windows));
    if (FAILED(hr)) return hr;
    VARIANT location{}, empty{};
    location.vt = VT_I4;
    location.lVal = CSIDL_DESKTOP;
    long handle{};
    ComPtr<IDispatch> dispatch;
    hr = windows->FindWindowSW(&location, &empty, SWC_DESKTOP, &handle, SWFO_NEEDDISPATCH, &dispatch);
    if (FAILED(hr)) return hr;
    if (!dispatch) return E_NOINTERFACE;
    ComPtr<IServiceProvider> provider;
    hr = dispatch.As(&provider);
    if (FAILED(hr)) return hr;
    ComPtr<IShellBrowser> browser;
    hr = provider->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser));
    if (FAILED(hr)) return hr;
    ComPtr<IShellView> view;
    hr = browser->QueryActiveShellView(&view);
    if (FAILED(hr)) return hr;
    HWND viewWindow{};
    hr = view->GetWindow(&viewWindow);
    if (FAILED(hr)) return hr;
    if (viewWindow != state.parent) return E_UNEXPECTED;
    desktopShellView = view;
    hr = view.As(&folderView);
    if (FAILED(hr)) return hr;
    return folderView->GetFolder(IID_PPV_ARGS(&shellFolder));
}

static bool MatchesTarget(DWORD_PTR index) {
    if (!state.target[0] || !folderView || !shellFolder || resolvingItem || index > static_cast<DWORD_PTR>(INT_MAX)) return false;
    resolvingItem = true;
    // Resolve the current item on every paint. Never cache a mutable ListView index
    // or compare display labels (extensions can be hidden and names can collide).
    auto view = folderView;
    auto folder = shellFolder;
    PITEMID_CHILD pidl{};
    HRESULT hr = view->Item(static_cast<int>(index), &pidl);
    bool matches = false;
    if (SUCCEEDED(hr) && pidl) {
        Microsoft::WRL::ComPtr<IShellItem> item;
        hr = SHCreateItemWithParent(nullptr, folder.Get(), pidl, IID_PPV_ARGS(&item));
        if (SUCCEEDED(hr)) {
            PWSTR path{};
            // Virtual desktop objects have no filesystem path; leave them visible.
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path) {
                matches = CompareStringOrdinal(path, -1, state.target, -1, TRUE) == CSTR_EQUAL;
                CoTaskMemFree(path);
            }
        }
    }
    CoTaskMemFree(pidl);
    if (FAILED(hr)) InterlockedExchange(&state.identityError, hr);
    resolvingItem = false;
    return matches;
}

static void Restore() {
    if (inputSubclassActive) {
        if (!RemoveWindowSubclass(state.list, FilterInput, 1)) return;
        inputSubclassActive = false;
    }
    if (subclassActive) {
        if (!RemoveWindowSubclass(state.parent, RequestItems, 1)) return;
        subclassActive = false;
        InterlockedExchange(&state.restored, 1);
        InvalidateRect(state.list, nullptr, TRUE);
    }
    if (restoreTimer) {
        KillTimer(nullptr, restoreTimer);
        restoreTimer = 0;
    }
    folderView.Reset();
    shellFolder.Reset();
    desktopShellView.Reset();
    if (ownerProcess) { CloseHandle(ownerProcess); ownerProcess = nullptr; }
}

static bool ShouldStop() {
    if (ownerProcess && WaitForSingleObject(ownerProcess, 0) == WAIT_OBJECT_0)
        InterlockedExchange(&state.ownerExited, 1);
    return state.ownerExited || GetTickCount64() >= state.deadline;
}

struct BackgroundMenuContext {
    Microsoft::WRL::ComPtr<IContextMenu2> menu2;
    Microsoft::WRL::ComPtr<IContextMenu3> menu3;
};

static LRESULT CALLBACK BackgroundMenuProc(HWND hwnd, UINT message, WPARAM wParam,
    LPARAM lParam, UINT_PTR id, DWORD_PTR data) {
    auto* context = reinterpret_cast<BackgroundMenuContext*>(data);
    if (message == WM_NCDESTROY) RemoveWindowSubclass(hwnd, BackgroundMenuProc, id);
    if (message == WM_INITMENUPOPUP || message == WM_DRAWITEM ||
        message == WM_MEASUREITEM || message == WM_MENUCHAR) {
        LRESULT result{};
        if (context->menu3 && SUCCEEDED(context->menu3->HandleMenuMsg2(message, wParam, lParam, &result))) return result;
        if (context->menu2 && SUCCEEDED(context->menu2->HandleMenuMsg(message, wParam, lParam))) return 0;
    }
    return DefSubclassProc(hwnd, message, wParam, lParam);
}

static void ShowBackgroundMenu(POINT point) {
    // Ask the actual desktop view for its background menu, never the target item.
    auto view = desktopShellView;
    if (!view) return;
    Microsoft::WRL::ComPtr<IContextMenu> menu;
    HRESULT hr = view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(&menu));
    if (FAILED(hr)) { InterlockedExchange(&state.menuError, hr); return; }
    HMENU popup = CreatePopupMenu();
    if (!popup) { InterlockedExchange(&state.menuError, static_cast<LONG>(GetLastError())); return; }
    hr = menu->QueryContextMenu(popup, 0, 1, 0x7FFF, CMF_NORMAL);
    if (SUCCEEDED(hr)) {
        BackgroundMenuContext context;
        menu.As(&context.menu2);
        menu.As(&context.menu3);
        // Separate subclass outlives the hide timer while a menu is open.
        if (SetWindowSubclass(state.parent, BackgroundMenuProc, 2, reinterpret_cast<DWORD_PTR>(&context))) {
            InterlockedIncrement(&state.backgroundMenus);
            const UINT command = static_cast<UINT>(TrackPopupMenuEx(popup,
                TPM_RETURNCMD | TPM_RIGHTBUTTON, point.x, point.y, state.parent, nullptr));
            RemoveWindowSubclass(state.parent, BackgroundMenuProc, 2);
            if (command) {
                CMINVOKECOMMANDINFOEX invoke{};
                invoke.cbSize = sizeof(invoke);
                invoke.fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
                invoke.hwnd = state.parent;
                invoke.lpVerb = MAKEINTRESOURCEA(command - 1);
                invoke.lpVerbW = MAKEINTRESOURCEW(command - 1);
                invoke.nShow = SW_SHOWNORMAL;
                invoke.ptInvoke = point;
                hr = menu->InvokeCommand(reinterpret_cast<LPCMINVOKECOMMANDINFO>(&invoke));
            }
        } else hr = E_FAIL;
    }
    if (FAILED(hr)) InterlockedExchange(&state.menuError, hr);
    DestroyMenu(popup);
}

static bool ProtectedItem(int index) {
    return index >= 0 && MatchesTarget(static_cast<DWORD_PTR>(index)) &&
        inputSubclassActive && GetTickCount64() < state.deadline;
}

// Virtual ListViews report state changes after they happen, not before. Keep
// Shell's notifications intact, then synchronously remove the target's state.
// This is an experiment, not a claim that every Shell activation path is covered.
static bool RepairSelection(int changedIndex = -1) {
    if (!inputSubclassActive || !state.protectInteraction || correctingSelection ||
        resolvingItem || GetTickCount64() >= state.deadline) return true;
    correctingSelection = true;
    bool success = true;
    const auto clearTarget = [&](int index) {
        if (!ProtectedItem(index)) return;
        const UINT before = ListView_GetItemState(state.list, index, protectedStates);
        if (!before) return;
        ListView_SetItemState(state.list, index, 0, protectedStates);
        if (ListView_GetItemState(state.list, index, protectedStates) != 0) success = false;
        else InterlockedIncrement(&state.correctedSelection);
    };
    if (changedIndex >= 0) clearTarget(changedIndex);
    else {
        clearTarget(ListView_GetNextItem(state.list, -1, LVNI_FOCUSED));
        clearTarget(ListView_GetNextItem(state.list, -1, LVNI_DROPHILITED));
        // Bound the scan even if reentrant Shell notifications change the selection.
        const int count = ListView_GetItemCount(state.list);
        int index = ListView_GetNextItem(state.list, -1, LVNI_SELECTED);
        for (int visited = 0; index >= 0 && visited < count && success; ++visited) {
            const int next = ListView_GetNextItem(state.list, index, LVNI_SELECTED);
            clearTarget(index);
            index = next;
        }
    }
    correctingSelection = false;
    if (!success) {
        InterlockedIncrement(&state.correctionFailures);
        InterlockedExchange(&state.installError, ERROR_INVALID_STATE);
        // Fail visibly rather than leave an invisible item selected.
        InterlockedExchange(&state.interactionReady, 0);
        Restore();
    }
    return success;
}

static bool SkipHiddenNavigation(HWND hwnd, WPARAM key) {
    if (!inputSubclassActive || !state.interactionReady || (GetKeyState(VK_MENU) & 0x8000)) return false;
    UINT direction{};
    switch (key) {
    case VK_LEFT: direction = LVNI_TOLEFT; break;
    case VK_RIGHT: direction = LVNI_TORIGHT; break;
    case VK_UP: direction = LVNI_ABOVE; break;
    case VK_DOWN: direction = LVNI_BELOW; break;
    case VK_HOME: case VK_END: break;
    default: return false;
    }
    const int count = ListView_GetItemCount(hwnd);
    if (count <= 0) return false;
    const int current = ListView_GetNextItem(hwnd, -1, LVNI_FOCUSED);
    int next = key == VK_HOME ? 0 : key == VK_END ? count - 1 :
        ListView_GetNextItem(hwnd, current, direction);
    // Leave ordinary navigation entirely to Explorer. Only replace a move whose
    // destination would be the hidden item, before native selection is cleared.
    if (!ProtectedItem(next)) return false;
    for (int visited = 0; visited < count && next >= 0; ++visited) {
        const int previous = next;
        next = key == VK_HOME ? next + 1 : key == VK_END ? next - 1 :
            ListView_GetNextItem(hwnd, next, direction);
        if (next < 0 || next >= count || next == previous || next == current) {
            // No visible destination in this direction: preserve current selection.
            InterlockedIncrement(&state.navigationBoundary);
            return true;
        }
        if (!ProtectedItem(next)) break;
    }
    if (next < 0 || next >= count || ProtectedItem(next) ||
        !inputSubclassActive || GetTickCount64() >= state.deadline) return false;
    const bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
    const bool control = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
    int anchor = ListView_GetSelectionMark(hwnd);
    if (anchor < 0 || anchor >= count || ProtectedItem(anchor)) anchor = current >= 0 ? current : next;
    if (!control) { ListView_SetItemState(hwnd, -1, 0, LVIS_SELECTED); }
    if (shift) {
        const int first = anchor < next ? anchor : next;
        const int last = anchor > next ? anchor : next;
        for (int index = first; index <= last; ++index) {
            if (!ProtectedItem(index)) { ListView_SetItemState(hwnd, index, LVIS_SELECTED, LVIS_SELECTED); }
        }
    } else if (!control) { ListView_SetItemState(hwnd, next, LVIS_SELECTED, LVIS_SELECTED); }
    ListView_SetItemState(hwnd, -1, 0, LVIS_FOCUSED);
    ListView_SetItemState(hwnd, next, LVIS_FOCUSED, LVIS_FOCUSED);
    if (shift) ListView_SetSelectionMark(hwnd, anchor);
    else if (!control) ListView_SetSelectionMark(hwnd, next);
    ListView_EnsureVisible(hwnd, next, FALSE);
    InterlockedIncrement(&state.navigationSkips);
    RepairSelection();
    return true;
}

static LRESULT CALLBACK FilterInput(HWND hwnd, UINT message, WPARAM wParam,
    LPARAM lParam, UINT_PTR, DWORD_PTR) {
    if (message == WM_NCDESTROY || ShouldStop()) {
        Restore();
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN || message == WM_CHAR ||
        message == WM_CONTEXTMENU) {
        if (!RepairSelection()) return 0;
    }
    if (message == WM_KEYDOWN && SkipHiddenNavigation(hwnd, wParam)) return 0;
    if (message == LVM_SETITEMSTATE && lParam) {
        const auto* requested = reinterpret_cast<const LVITEMW*>(lParam);
        if (requested->state & requested->stateMask & protectedStates) {
            if (wParam == static_cast<WPARAM>(-1)) {
                // Preserve bulk selection for visible items, excluding only our target.
                const int count = ListView_GetItemCount(hwnd);
                for (int index = 0; index < count; ++index) {
                    auto item = *requested;
                    if (ProtectedItem(index)) {
                        item.state &= ~protectedStates;
                        InterlockedIncrement(&state.blockedSelection);
                    }
                    DefSubclassProc(hwnd, message, static_cast<WPARAM>(index), reinterpret_cast<LPARAM>(&item));
                }
                return TRUE;
            }
            if (wParam <= static_cast<WPARAM>(INT_MAX) && ProtectedItem(static_cast<int>(wParam))) {
                auto item = *requested;
                item.state &= ~protectedStates;
                InterlockedIncrement(&state.blockedSelection);
                return DefSubclassProc(hwnd, message, wParam, reinterpret_cast<LPARAM>(&item));
            }
        }
    }
    switch (message) {
    case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_LBUTTONDBLCLK:
    case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_RBUTTONDBLCLK:
    case WM_MBUTTONDOWN: case WM_MBUTTONUP: case WM_MBUTTONDBLCLK:
    case WM_CONTEXTMENU: {
        if (message == WM_CONTEXTMENU && lParam == -1) break;
        // A marquee/drag started on visible content must still receive its release.
        if ((message == WM_LBUTTONUP || message == WM_RBUTTONUP || message == WM_MBUTTONUP)
            && GetCapture() == hwnd) break;
        LVHITTESTINFO hit{};
        hit.pt = { GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam) };
        if (message == WM_CONTEXTMENU) ScreenToClient(hwnd, &hit.pt);
        const int index = ListView_HitTest(hwnd, &hit);
        if (ProtectedItem(index)) {
            InterlockedIncrement(&state.blockedMouse);
            if (message == WM_RBUTTONUP || message == WM_CONTEXTMENU) {
                POINT point = hit.pt;
                ClientToScreen(hwnd, &point);
                ShowBackgroundMenu(point);
            }
            return 0;
        }
        break;
    }
    default: break;
    }
    const LRESULT result = DefSubclassProc(hwnd, message, wParam, lParam);
    if (message == WM_KEYDOWN || message == WM_CHAR ||
        message == WM_LBUTTONUP || message == WM_RBUTTONUP) RepairSelection();
    return result;
}

static void CALLBACK RestoreTick(HWND, UINT, UINT_PTR, DWORD) {
    if (ShouldStop()) Restore();
}

static LRESULT CALLBACK RequestItems(HWND hwnd, UINT message, WPARAM wParam,
    LPARAM lParam, UINT_PTR, DWORD_PTR) {
    if (message == WM_NCDESTROY || ShouldStop()) {
        Restore();
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }
    // Capture the stage before forwarding; all other notifications retain their result.
    bool prePaint = false;
    if (message == WM_NOTIFY && lParam) {
        const auto* header = reinterpret_cast<const NMHDR*>(lParam);
        if (header->hwndFrom == state.list && state.protectInteraction &&
            (header->code == static_cast<UINT>(LVN_ITEMCHANGED) ||
             header->code == static_cast<UINT>(LVN_ODSTATECHANGED))) {
            InterlockedIncrement(&state.stateEvents);
            const int changedIndex = header->code == static_cast<UINT>(LVN_ITEMCHANGED)
                ? reinterpret_cast<const NMLISTVIEW*>(header)->iItem : -1;
            const LRESULT result = DefSubclassProc(hwnd, message, wParam, lParam);
            RepairSelection(changedIndex);
            return result;
        }
        if (header->hwndFrom == state.list && state.protectInteraction &&
            (header->code == static_cast<UINT>(NM_DBLCLK) ||
             header->code == static_cast<UINT>(LVN_ITEMACTIVATE))) {
            const auto* activate = reinterpret_cast<const NMITEMACTIVATE*>(header);
            if (ProtectedItem(activate->iItem)) {
                InterlockedIncrement(&state.blockedMouse);
                return 0;
            }
        }
        if (header->hwndFrom == state.list && state.protectInteraction &&
            (header->code == static_cast<UINT>(LVN_KEYDOWN) || header->code == static_cast<UINT>(NM_RETURN))) {
            if (!RepairSelection()) return 0;
        }
        if (header->hwndFrom == state.list && header->code == static_cast<UINT>(LVN_ITEMCHANGING)) {
            const auto* change = reinterpret_cast<const NMLISTVIEW*>(header);
            if ((change->uChanged & LVIF_STATE) &&
                (change->uNewState & ~change->uOldState & protectedStates) && ProtectedItem(change->iItem)) {
                InterlockedIncrement(&state.blockedSelection);
                return TRUE;
            }
        }
        if (header->hwndFrom == state.list &&
            (header->code == static_cast<UINT>(LVN_BEGINLABELEDITW) ||
             header->code == static_cast<UINT>(LVN_BEGINLABELEDITA))) {
            // iItem is in the common prefix of the ANSI and Unicode structures.
            const auto* edit = reinterpret_cast<const NMLVDISPINFOW*>(header);
            if (ProtectedItem(edit->item.iItem)) {
                InterlockedIncrement(&state.blockedRename);
                return TRUE;
            }
        }
        if (header->hwndFrom == state.list && header->code == static_cast<UINT>(NM_CUSTOMDRAW)) {
            const auto* draw = reinterpret_cast<const NMCUSTOMDRAW*>(header);
            prePaint = draw->dwDrawStage == CDDS_PREPAINT;
            if (draw->dwDrawStage == CDDS_ITEMPREPAINT && MatchesTarget(draw->dwItemSpec)) {
                InterlockedIncrement(&state.matched);
                // COM calls may pump messages; recheck the lifetime before suppressing.
                if (subclassActive && (!state.protectInteraction || state.interactionReady) && GetTickCount64() < state.deadline) {
                    InterlockedIncrement(&state.suppressed);
                    return CDRF_SKIPDEFAULT;
                }
            }
        }
    }
    const LRESULT result = DefSubclassProc(hwnd, message, wParam, lParam);
    // Only extend the previously observed default path. Respect custom rendering flags.
    if (prePaint && result == CDRF_DODEFAULT && GetTickCount64() < state.deadline) {
        InterlockedIncrement(&state.modified);
        return result | CDRF_NOTIFYITEMDRAW;
    }
    return result;
}

static void InstallRequest() {
    if (GetTickCount64() >= state.deadline ||
        InterlockedCompareExchange(&state.attempted, 1, 0) != 0) return;
    // SetWindowSubclass must run on the window's owning thread.
    if (GetWindowThreadProcessId(state.parent, nullptr) != GetCurrentThreadId()) {
        InterlockedExchange(&state.installError, ERROR_INVALID_THREAD_ID);
        return;
    }
    if (state.ownerPid) {
        ownerProcess = OpenProcess(SYNCHRONIZE, FALSE, state.ownerPid);
        if (!ownerProcess || ShouldStop()) {
            InterlockedExchange(&state.installError, ERROR_PROCESS_ABORTED);
            Restore();
            return;
        }
    }
    if (state.target[0]) {
        // Owner-data views use synchronous post-change correction instead of veto.
        if (ListView_GetEditControl(state.list) != nullptr || GetCapture() == state.list) {
            InterlockedExchange(&state.installError, ERROR_BUSY);
            Restore();
            return;
        }
        const HRESULT hr = OpenDesktopView();
        if (FAILED(hr) || GetTickCount64() >= state.deadline) {
            InterlockedExchange(&state.identityError, FAILED(hr) ? hr : HRESULT_FROM_WIN32(ERROR_TIMEOUT));
            InterlockedExchange(&state.installError, ERROR_NOT_READY);
            Restore();
            return;
        }
    }
    HMODULE pinned{};
    // Keep callback code valid even if the helper dies. This small test DLL remains
    // loaded until Explorer exits; the timer/subclass are removed independently.
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&RequestItems), &pinned)) {
        InterlockedExchange(&state.installError, static_cast<LONG>(GetLastError()));
        Restore();
        return;
    }
    restoreTimer = SetTimer(nullptr, 0, 100, RestoreTick);
    if (!restoreTimer) {
        InterlockedExchange(&state.installError, static_cast<LONG>(GetLastError()));
        Restore();
        return;
    }
    if (!SetWindowSubclass(state.parent, RequestItems, 1, 0)) {
        InterlockedExchange(&state.installError, ERROR_GEN_FAILURE);
        Restore();
        return;
    }
    subclassActive = true;
    if (state.target[0] && state.protectInteraction) {
        if (!SetWindowSubclass(state.list, FilterInput, 1, 0)) {
            InterlockedExchange(&state.installError, ERROR_GEN_FAILURE);
            Restore();
            return;
        }
        inputSubclassActive = true;
        // Remove an existing selection/focus before hiding, so Enter/commands cannot
        // act on a previously selected invisible target. Other items are untouched.
        const int count = ListView_GetItemCount(state.list);
        for (int index = 0; index < count; ++index) {
            if (!ProtectedItem(index)) continue;
            ListView_SetItemState(state.list, index, 0, protectedStates);
            if (ListView_GetItemState(state.list, index, protectedStates) != 0) {
                InterlockedExchange(&state.installError, ERROR_ACCESS_DENIED);
                Restore();
                return;
            }
        }
        if (!inputSubclassActive || GetTickCount64() >= state.deadline) {
            InterlockedExchange(&state.installError, ERROR_TIMEOUT);
            Restore();
            return;
        }
        InterlockedExchange(&state.interactionReady, 1);
    }
    InterlockedExchange(&state.installed, 1);
    InvalidateRect(state.list, nullptr, TRUE);
}

static LRESULT CALLBACK Observe(int code, WPARAM wParam, LPARAM lParam) {
    if (code >= 0 && GetCurrentProcessId() == state.processId && lParam) {
        const auto* message = reinterpret_cast<const CWPRETSTRUCT*>(lParam);
        if (message->hwnd == state.parent || message->hwnd == state.list)
            InstallRequest();
        InterlockedIncrement(&state.callbacks);
        if (message->hwnd == state.parent && message->message == WM_NOTIFY && message->lParam) {
            const auto* header = reinterpret_cast<const NMHDR*>(message->lParam);
            if (header->hwndFrom == state.list) {
                InterlockedIncrement(&state.notifications);
                if (header->code == static_cast<UINT>(NM_CUSTOMDRAW)) {
                    InterlockedIncrement(&state.customDraw);
                    const auto* draw = reinterpret_cast<const NMCUSTOMDRAW*>(header);
                    if ((draw->dwDrawStage & CDDS_ITEM) != 0)
                        InterlockedIncrement(&state.itemDraw);
                    else if (draw->dwDrawStage == CDDS_PREPAINT) {
                        InterlockedIncrement(&state.prePaint);
                        const auto flags = static_cast<LONG>(message->lResult);
                        InterlockedOr(&state.prePaintFlags, flags);
                        if ((flags & CDRF_NOTIFYITEMDRAW) != 0)
                            InterlockedIncrement(&state.notifyItem);
                        if ((flags & CDRF_SKIPDEFAULT) != 0)
                            InterlockedIncrement(&state.skipDefault);
                        if (flags == CDRF_DODEFAULT)
                            InterlockedIncrement(&state.defaultDraw);
                    } else if (draw->dwDrawStage == CDDS_POSTPAINT)
                        InterlockedIncrement(&state.postPaint);
                    else
                        InterlockedIncrement(&state.otherStage);
                }
            }
        }
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

// Versioned export prevents an older managed caller receiving a larger structure.
extern "C" __declspec(dllexport) int WINAPI ObserveDesktopV9(HWND list, LPCWSTR target, BOOL protectInteraction, DWORD ownerPid, ProbeResult* result) {
    if (!result) return 1;
    *result = {};
    WCHAR name[64]{};
    if (!IsWindow(list) || !GetClassNameW(list, name, 64) || lstrcmpW(name, L"SysListView32") != 0) {
        result->error = ERROR_INVALID_WINDOW_HANDLE;
        return 1;
    }
    // One session per interactive desktop. No global hooks or registered extension.
    HANDLE gate = CreateMutexW(nullptr, TRUE, L"Local\\MagiDesk.DesktopDrawProbe");
    if (!gate) { result->error = GetLastError(); return 1; }
    if (GetLastError() == ERROR_ALREADY_EXISTS) { CloseHandle(gate); return 2; }
    state.protectInteraction = protectInteraction;
    state.ownerPid = ownerPid;
    InterlockedExchange(&state.ownerExited, 0);
    InterlockedExchange(&state.backgroundMenus, 0);
    InterlockedExchange(&state.menuError, 0);
    if (FAILED(StringCchCopyW(state.target, ARRAYSIZE(state.target), target ? target : L""))) {
        result->error = ERROR_INSUFFICIENT_BUFFER;
        ReleaseMutex(gate);
        CloseHandle(gate);
        return 1;
    }
    DWORD pid{};
    DWORD thread = GetWindowThreadProcessId(list, &pid);
    state.list = list;
    state.parent = GetParent(list);
    state.processId = pid;
    InterlockedExchange(&state.callbacks, 0);
    InterlockedExchange(&state.notifications, 0);
    InterlockedExchange(&state.customDraw, 0);
    InterlockedExchange(&state.itemDraw, 0);
    InterlockedExchange(&state.prePaint, 0);
    InterlockedExchange(&state.postPaint, 0);
    InterlockedExchange(&state.otherStage, 0);
    InterlockedExchange(&state.notifyItem, 0);
    InterlockedExchange(&state.skipDefault, 0);
    InterlockedExchange(&state.defaultDraw, 0);
    InterlockedExchange(&state.prePaintFlags, 0);
    InterlockedExchange(&state.attempted, 0);
    InterlockedExchange(&state.installed, 0);
    InterlockedExchange(&state.restored, 0);
    InterlockedExchange(&state.installError, 0);
    InterlockedExchange(&state.modified, 0);
    InterlockedExchange(&state.matched, 0);
    InterlockedExchange(&state.suppressed, 0);
    InterlockedExchange(&state.identityError, 0);
    InterlockedExchange(&state.blockedMouse, 0);
    InterlockedExchange(&state.blockedSelection, 0);
    InterlockedExchange(&state.blockedRename, 0);
    InterlockedExchange(&state.interactionReady, 0);
    InterlockedExchange(&state.ownerData, (GetWindowLongPtrW(list, GWL_STYLE) & LVS_OWNERDATA) != 0 ? 1 : 0);
    InterlockedExchange(&state.stateEvents, 0);
    InterlockedExchange(&state.correctedSelection, 0);
    InterlockedExchange(&state.correctionFailures, 0);
    InterlockedExchange(&state.navigationSkips, 0);
    InterlockedExchange(&state.navigationBoundary, 0);
    const DWORD duration = state.target[0] ? 15000 : 5000;
    state.deadline = GetTickCount64() + duration;
    HMODULE module{};
    HHOOK hook{};
    if (thread && state.parent && GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&Observe), &module)) {
        hook = SetWindowsHookExW(WH_CALLWNDPROCRET, Observe, module, thread);
    }
    if (!hook) {
        result->error = GetLastError();
    } else {
        InvalidateRect(list, nullptr, FALSE);
        PostMessageW(list, WM_NULL, 0, 0);
        const ULONGLONG waitUntil = GetTickCount64() + duration + 500;
        while (GetTickCount64() < waitUntil && !state.restored && !state.installError) Sleep(100);
        // Give Explorer time to acknowledge cleanup, without an unbounded wait.
        for (int i = 0; i < 20 && state.installed && !state.restored; ++i) Sleep(100);
        if (!UnhookWindowsHookEx(hook)) result->error = GetLastError();
        result->callbacks = InterlockedCompareExchange(&state.callbacks, 0, 0);
        result->notifications = InterlockedCompareExchange(&state.notifications, 0, 0);
        result->customDraw = InterlockedCompareExchange(&state.customDraw, 0, 0);
        result->itemDraw = InterlockedCompareExchange(&state.itemDraw, 0, 0);
        result->prePaint = InterlockedCompareExchange(&state.prePaint, 0, 0);
        result->postPaint = InterlockedCompareExchange(&state.postPaint, 0, 0);
        result->otherStage = InterlockedCompareExchange(&state.otherStage, 0, 0);
        result->notifyItem = InterlockedCompareExchange(&state.notifyItem, 0, 0);
        result->skipDefault = InterlockedCompareExchange(&state.skipDefault, 0, 0);
        result->defaultDraw = InterlockedCompareExchange(&state.defaultDraw, 0, 0);
        result->prePaintFlags = InterlockedCompareExchange(&state.prePaintFlags, 0, 0);
        result->attempted = InterlockedCompareExchange(&state.attempted, 0, 0);
        result->installed = InterlockedCompareExchange(&state.installed, 0, 0);
        result->restored = InterlockedCompareExchange(&state.restored, 0, 0);
        result->installError = InterlockedCompareExchange(&state.installError, 0, 0);
        result->modified = InterlockedCompareExchange(&state.modified, 0, 0);
        result->matched = InterlockedCompareExchange(&state.matched, 0, 0);
        result->suppressed = InterlockedCompareExchange(&state.suppressed, 0, 0);
        result->identityError = InterlockedCompareExchange(&state.identityError, 0, 0);
        result->blockedMouse = InterlockedCompareExchange(&state.blockedMouse, 0, 0);
        result->blockedSelection = InterlockedCompareExchange(&state.blockedSelection, 0, 0);
        result->blockedRename = InterlockedCompareExchange(&state.blockedRename, 0, 0);
        result->interactionReady = InterlockedCompareExchange(&state.interactionReady, 0, 0);
        result->ownerData = InterlockedCompareExchange(&state.ownerData, 0, 0);
        result->stateEvents = InterlockedCompareExchange(&state.stateEvents, 0, 0);
        result->correctedSelection = InterlockedCompareExchange(&state.correctedSelection, 0, 0);
        result->correctionFailures = InterlockedCompareExchange(&state.correctionFailures, 0, 0);
        result->navigationSkips = InterlockedCompareExchange(&state.navigationSkips, 0, 0);
        result->navigationBoundary = InterlockedCompareExchange(&state.navigationBoundary, 0, 0);
        result->ownerExited = InterlockedCompareExchange(&state.ownerExited, 0, 0);
        result->backgroundMenus = InterlockedCompareExchange(&state.backgroundMenus, 0, 0);
        result->menuError = InterlockedCompareExchange(&state.menuError, 0, 0);
        if (!result->error && result->installError) result->error = static_cast<DWORD>(result->installError);
        if (!result->error && (!result->installed || !result->restored)) result->error = ERROR_TIMEOUT;
    }
    ReleaseMutex(gate);
    CloseHandle(gate);
    return hook && !result->error ? 0 : 1;
}
