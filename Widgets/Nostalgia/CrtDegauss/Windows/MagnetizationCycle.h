// SPDX-License-Identifier: MIT
#pragma once

#include <algorithm>

namespace degauss
{
    inline constexpr double PulseSeconds = 1.8;
    inline constexpr int DefaultMinutes = 15;
    inline constexpr int MaximumMinutes = 120;

    struct Frame
    {
        double amount{};
        double pulse = -1;
        double remaining{};
        bool due{};
        bool notify{};
    };

    class Cycle
    {
    public:
        void Start(double now)
        {
            started = now;
            degaussing = false;
            prompted = false;
        }

        bool Degauss(double now, int minutes)
        {
            if (degaussing) return false;
            beforeDegauss = Strength(now, minutes);
            degaussStarted = now;
            degaussing = true;
            return true;
        }

        bool ShowNow(double now, int minutes)
        {
            if (degaussing) return false;
            started = now - minutes * 60.0;
            prompted = true;
            return true;
        }

        Frame Sample(double now, int minutes)
        {
            if (degaussing)
            {
                const double progress = std::max(0.0, (now - degaussStarted) / PulseSeconds);
                if (progress < 1)
                    return {beforeDegauss * (1 - Smooth(progress)), progress};
                Start(now);
            }
            const double remaining = std::max(0.0, started + minutes * 60.0 - now);
            const bool due = remaining == 0;
            const bool notify = due && !prompted;
            prompted |= due;
            return {Strength(now, minutes), -1, remaining, due, notify};
        }

        bool Degaussing() const { return degaussing; }

    private:
        static double Smooth(double t) { return t * t * (3 - 2 * t); }

        double Strength(double now, int minutes) const
        {
            return Smooth(std::clamp((now - started) / (minutes * 60.0), 0.0, 1.0));
        }

        double started{}, degaussStarted{}, beforeDegauss{};
        bool degaussing{}, prompted{};
    };
}
