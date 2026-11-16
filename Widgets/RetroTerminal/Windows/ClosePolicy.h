// SPDX-License-Identifier: MIT
#pragma once

enum class SaveFailureChoice { Retry, KeepOpen, CloseWithoutSaving };

// Stop first, even if saving fails or the user keeps the panel open. A failed
// close never restarts the effect or discards edits without an explicit choice.
template<class Stop, class Save, class Choose>
bool RequestSafeClose(Stop stop, Save save, Choose choose)
{
    stop();
    while (!save()) {
        switch (choose()) {
        case SaveFailureChoice::Retry: break;
        case SaveFailureChoice::KeepOpen: return false;
        case SaveFailureChoice::CloseWithoutSaving: return true;
        }
    }
    return true;
}
