// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <chrono>

// A restart needs both an elapsed cooldown and a continuous run of healthy samples.
class PerformanceRecovery final
{
public:
    using Clock = std::chrono::steady_clock;
    using TimePoint = Clock::time_point;

    bool Pending() const { return pending; }

    void Reset()
    {
        Cancel();
        attempts = 0;
    }

    bool Pause(TimePoint now)
    {
        if (!enabled || attempts >= MaxAttempts) return false;
        pending = true;
        pausedAt = lastSample = now;
        stableSince = {};
        return true;
    }

    bool Configure(bool autoRestart, int seconds)
    {
        enabled = autoRestart;
        cooldown = std::chrono::seconds(std::clamp(seconds, 30, 300));
        if (!enabled && pending) {
            Cancel();
            return true;
        }
        return false;
    }

    bool Ready(TimePoint now, bool healthy)
    {
        if (!pending) return false;
        healthy = healthy && now - lastSample <= std::chrono::seconds(2);
        lastSample = now;
        if (!healthy) stableSince = {};
        else if (stableSince == TimePoint{}) stableSince = now;
        return stableSince != TimePoint{} && now - stableSince >= std::chrono::seconds(30)
            && now - pausedAt >= cooldown * (attempts + 1);
    }

    void Resume()
    {
        Cancel();
        ++attempts;
    }

    void Cancel()
    {
        pending = false;
        stableSince = {};
    }

private:
    static constexpr int MaxAttempts = 2;
    bool enabled = true, pending = false;
    int attempts = 0;
    std::chrono::seconds cooldown{60};
    TimePoint pausedAt{}, stableSince{}, lastSample{};
};
