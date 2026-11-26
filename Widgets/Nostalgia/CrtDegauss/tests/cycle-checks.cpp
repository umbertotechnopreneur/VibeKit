// SPDX-License-Identifier: MIT
#include "../Windows/MagnetizationCycle.h"
#include <cmath>
#include <cstdlib>
#include <iostream>

int main()
{
    const auto check = [](bool condition, const char* message) {
        if (!condition) {
            std::cerr << message << '\n';
            std::exit(EXIT_FAILURE);
        }
    };
    degauss::Cycle cycle;
    cycle.Start(100);
    check(cycle.Sample(100, 15).amount == 0, "Start clean");
    check(cycle.Sample(550, 15).amount == 0.5, "Half strength after half the interval");
    check(!cycle.Sample(999, 15).notify, "No early alert");
    const auto due = cycle.Sample(1000, 15);
    check(due.amount == 1 && due.due && due.notify, "Notify at the configured interval");
    check(!cycle.Sample(1200, 15).notify, "Do not repeat alert");
    check(!cycle.Sample(1200, 20).due && !cycle.Sample(1301, 20).notify, "Interval changes do not repeat an alert");
    check(cycle.Degauss(1500, 15) && !cycle.Degauss(1500.2, 15), "One degauss pulse at a time");
    const auto pulse = cycle.Sample(1500 + degauss::PulseSeconds / 2, 15);
    check(std::abs(pulse.amount - 0.5) < 1e-9 && std::abs(pulse.pulse - 0.5) < 1e-9, "Pulse removes patches");
    const auto clean = cycle.Sample(1502, 15);
    check(clean.amount == 0 && clean.pulse == -1 && clean.remaining == 900 && !clean.notify, "Clean display starts a new cycle");
    check(cycle.Sample(2402, 15).notify, "The new cycle can notify again");
    cycle.Start(3000);
    check(cycle.ShowNow(3001, 15), "Show now is accepted when idle");
    const auto shown = cycle.Sample(3001, 15);
    check(shown.amount == 1 && shown.due && !shown.notify, "Show now reaches full strength without a redundant notification");
    check(cycle.Degauss(3002, 15) && !cycle.ShowNow(3002.1, 15), "Show now does not interrupt degaussing");
    check(cycle.Sample(3004, 15).amount == 0, "The normal cycle restarts after manual degaussing");
    std::cout << "PASS Windows magnetization cycle and one reminder per cycle\n";
}
