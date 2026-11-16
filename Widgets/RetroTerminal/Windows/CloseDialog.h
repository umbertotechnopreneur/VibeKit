// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <commctrl.h>
#include <iterator>
#include "ClosePolicy.h"

inline SaveFailureChoice ShowSaveFailure(HWND owner)
{
    constexpr int Retry = 100, KeepOpen = 101, CloseWithoutSaving = 102;
    const TASKDIALOG_BUTTON buttons[]{
        {Retry, L"Retry saving"},
        {KeepOpen, L"Keep settings open"},
        {CloseWithoutSaving, L"Close without saving"},
    };
    TASKDIALOGCONFIG dialog{sizeof(dialog)};
    dialog.hwndParent = owner;
    dialog.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_POSITION_RELATIVE_TO_WINDOW;
    dialog.pszWindowTitle = L"Retro Terminal";
    dialog.pszMainInstruction = L"Your settings could not be saved";
    dialog.pszContent = L"The CRT effect has stopped. Retry saving, keep this panel open with your current values, or close without saving your changes.";
    dialog.pszMainIcon = TD_WARNING_ICON;
    dialog.cButtons = UINT(std::size(buttons));
    dialog.pButtons = buttons;
    dialog.nDefaultButton = KeepOpen;
    int selected = KeepOpen;
    if (FAILED(TaskDialogIndirect(&dialog, &selected, nullptr, nullptr)))
        return SaveFailureChoice::KeepOpen;
    if (selected == Retry) return SaveFailureChoice::Retry;
    if (selected == CloseWithoutSaving) return SaveFailureChoice::CloseWithoutSaving;
    return SaveFailureChoice::KeepOpen;
}
