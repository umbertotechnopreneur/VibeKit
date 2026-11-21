// SPDX-License-Identifier: MIT
#include "../BuiltInPresets.h"
#include "../PerformanceRecovery.h"
#include <cassert>
#include <limits>
#include <set>
#include <iostream>
#include <string_view>
int main()
{
    EffectSettings s;
    s.scanSpacing = 2; s.scanThickness = 12; s.intervalMin = 40; s.intervalMax = 3;
    s.durationMinMs = 700; s.durationMaxMs = 100; s.contrast = std::numeric_limits<float>::quiet_NaN();
    s.glitchTypes = 255; s.Clamp();
    assert(s.scanThickness < s.scanSpacing && s.intervalMax >= s.intervalMin);
    assert(s.durationMaxMs >= s.durationMinMs && s.contrast == 1 && s.glitchTypes == 15);
    s.intervalMin = 300; s.intervalMax = std::numeric_limits<float>::quiet_NaN();
    s.durationMinMs = 1200; s.durationMaxMs = std::numeric_limits<float>::quiet_NaN();
    s.scanSpacing = 2; s.scanThickness = std::numeric_limits<float>::quiet_NaN();
    s.Clamp();
    assert(s.intervalMax == 300 && s.durationMaxMs == 1200);
    assert(s.scanThickness >= .25f && s.scanThickness <= s.scanSpacing - .25f);
    s = EffectSettings{};
    GlitchSchedule schedule(42); schedule.Reset(0, s);
    assert(schedule.Sample(100, s).type == GlitchType::None); // random is off
    s.glitchTypes = 4;
    assert(schedule.Trigger(100, s));
    assert(schedule.Sample(100.05, s).type == GlitchType::Noise);
    assert(schedule.Sample(100.05, s).amount > 0);
    assert(!schedule.Trigger(100.06, s)); // cannot overlap
    assert(schedule.Sample(102, s).type == GlitchType::None);
    assert(!schedule.Trigger(103, s)); // rest applies to Try now as well
    assert(schedule.Trigger(104, s));
    schedule.Cancel(104, s);
    assert(schedule.Sample(104, s).type == GlitchType::None);
    assert(!schedule.Trigger(105, s));
    s.randomGlitches = true; s.intervalMin = s.intervalMax = 20;
    schedule.Reset(0, s);
    assert(schedule.Sample(19.9, s).type == GlitchType::None);
    assert(schedule.Sample(20.05, s).type == GlitchType::Noise);
    schedule.Cancel(20.1, s);
    s.randomGlitches = false;
    assert(schedule.Sample(1000, s).type == GlitchType::None);
    s.glitchTypes = 0; assert(!schedule.Trigger(1001, s));
    s.glitchTypes = 15; s.intensity = 0;
    assert(!schedule.Trigger(1002, s));
    s.intensity = 1; s.glitchIntensity = 0;
    assert(!schedule.Trigger(1002, s));
    s.randomGlitches = true;
    assert(schedule.Sample(1002, s).type == GlitchType::None);
    s.glitchIntensity = .25f;
    assert(schedule.Trigger(1002, s));
    assert(BuiltInPresetCount == 40);
    std::set<std::wstring_view> names;
    for (const auto& [name, settings] : BuiltInPresets) {
        assert(names.emplace(name).second);
        auto clamped = settings;
        clamped.Clamp();
        for (const auto& control : FloatControls)
            assert(settings.*(control.field) == clamped.*(control.field));
        assert(settings.intensity * settings.scanStrength <= .055f);
        assert(settings.intensity * settings.grain >= .015f);
        assert(!settings.randomGlitches && !settings.persistence);
        if (std::wstring_view(name).starts_with(L"Trinitron "))
            assert(settings.mask == PhosphorMask::RgbStripes);
    }
    using namespace std::chrono_literals;
    PerformanceRecovery recovery;
    auto now = PerformanceRecovery::TimePoint{} + 1s;
    assert(recovery.Pause(now));
    for (int elapsed = 1; elapsed <= 60; ++elapsed)
        assert(!recovery.Ready(now + std::chrono::seconds(elapsed), elapsed != 48));
    assert(!recovery.Ready(now + 79s, true)); // A dispatcher stall breaks the stable window.
    for (int elapsed = 80; elapsed < 110; ++elapsed)
        assert(!recovery.Ready(now + std::chrono::seconds(elapsed), true));
    assert(recovery.Ready(now + 110s, true));
    recovery.Resume();
    now += 111s;
    assert(recovery.Pause(now));
    for (int elapsed = 1; elapsed < 120; ++elapsed)
        assert(!recovery.Ready(now + std::chrono::seconds(elapsed), true));
    assert(recovery.Ready(now + 120s, true));
    recovery.Resume();
    assert(!recovery.Pause(now + 121s));
    recovery.Reset();
    assert(recovery.Pause(now));
    assert(recovery.Configure(false, 30) && !recovery.Pending());
    assert(!recovery.Pause(now));
    std::cout << "PASS Windows settings bounds, presets, glitches and performance recovery\n";
}
