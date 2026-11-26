// SPDX-License-Identifier: MIT
#pragma once
#include <chrono>
#include <optional>

// An overlay can briefly be occluded while another desktop filter closes.
// Keep the deadline until a real frame is presented, even if the input switches.
class PresentationRetry final
{
    using Clock = std::chrono::steady_clock;
    std::optional<Clock::time_point> firstOcclusion;
    Clock::time_point nextAttempt{};
public:
    void Occluded(Clock::time_point now)
    {
        if (!firstOcclusion) firstOcclusion = now;
        nextAttempt = now + std::chrono::milliseconds(100);
    }
    bool Waiting(Clock::time_point now) const { return firstOcclusion && now < nextAttempt; }
    bool Expired(Clock::time_point now) const { return firstOcclusion && now - *firstOcclusion >= std::chrono::seconds(3); }
    void Presented() { firstOcclusion.reset(); }
};
