// SPDX-License-Identifier: MIT
#define VIBEWARE_RECOVERY_TESTING
#include "../DesktopEffectRecovery.h"
#include "../DesktopEffectCommands.h"
#include <iostream>
#include <sstream>

int wmain(int argc, wchar_t** argv)
{
    if (argc == 2 && std::wstring(argv[1]) == L"--commands")
    {
        const auto enable = RegisterWindowMessageW(L"VibeWare.DesktopEffects.EnableAll.v1");
        const auto disable = RegisterWindowMessageW(L"VibeWare.DesktopEffects.DisableAll.v1");
        return enable && disable && enable != disable && vibeware::effects::Commands::IsEnableAll(enable) &&
            vibeware::effects::Commands::IsDisableAll(disable) && !vibeware::effects::Commands::IsEnableAll(disable) ? 0 : 5;
    }
    if (argc == 3 && std::wstring(argv[1]) == L"--corrupt")
    {
        winrt::init_apartment();
        vibeware::effects::Recovery recovery(L"native corrupt-state check", argv[2], [] { return 1000; }, [] { return true; }, [] { return true; });
        recovery.Tick();
        std::cout << (recovery.Available() ? "available" : "unavailable") << std::endl;
        return 0;
    }
    if (argc == 4 && std::wstring(argv[1]) == L"--observe")
    {
        winrt::init_apartment();
        std::vector<vibeware::effects::ParticipantStatus> status;
        if (!vibeware::effects::Recovery::ObserveForTest(argv[2], std::stoull(argv[3]), status)) return 4;
        int leaders = 0, pending = 0;
        for (const auto& participant : status) { leaders += participant.leader; pending += participant.pending; }
        std::cout << status.size() << '|' << leaders << '|' << pending << std::endl;
        return 0;
    }
    if (argc != 2) return 2;
    winrt::init_apartment();
    std::uint64_t now = 1000;
    bool healthy = true, desktop = true;
    vibeware::effects::Recovery recovery(L"native test worker", argv[1], [&] { return now; }, [&] { return healthy; }, [&] { return desktop; });
    if (!recovery.Available()) return 3;
    int resumed = 0;
    std::string line;
    while (std::getline(std::cin, line))
    {
        std::istringstream input(line);
        std::string command, time, health, ready;
        std::getline(input, command, '|'); std::getline(input, time, '|');
        std::getline(input, health, '|'); std::getline(input, ready, '|');
        now = std::stoull(time); healthy = health == "1"; desktop = ready == "1";
        if (command == "quit") break;
        if (command == "pause") recovery.Pause(true, 30);
        else if (command == "cancel") recovery.Cancel();
        if (recovery.Tick()) ++resumed;
        std::cout << resumed << '|' << (recovery.Pending() ? 1 : 0) << std::endl;
    }
}
