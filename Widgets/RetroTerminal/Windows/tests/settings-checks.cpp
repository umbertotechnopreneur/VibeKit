// SPDX-License-Identifier: MIT
#include "../EffectSettings.h"
#include <cassert>
#include <limits>
#include <iostream>
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
    assert(schedule.Sample(100, s).type == -1); // random is off
    s.glitchTypes = 4;
    assert(schedule.Trigger(100, s));
    assert(schedule.Sample(100.05, s).type == 2);
    assert(schedule.Sample(100.05, s).amount > 0);
    assert(!schedule.Trigger(100.06, s)); // cannot overlap
    assert(schedule.Sample(102, s).type == -1);
    assert(!schedule.Trigger(103, s)); // rest applies to Try now as well
    assert(schedule.Trigger(104, s));
    schedule.Cancel(104, s);
    assert(schedule.Sample(104, s).type == -1);
    assert(!schedule.Trigger(105, s));
    s.randomGlitches = true; s.intervalMin = s.intervalMax = 20;
    schedule.Reset(0, s);
    assert(schedule.Sample(19.9, s).type == -1);
    assert(schedule.Sample(20.05, s).type == 2);
    schedule.Cancel(20.1, s);
    s.randomGlitches = false;
    assert(schedule.Sample(1000, s).type == -1);
    s.glitchTypes = 0; assert(!schedule.Trigger(1001, s));
    s.glitchTypes = 15; s.intensity = 0;
    assert(!schedule.Trigger(1002, s));
    s.intensity = 1; s.glitchIntensity = 0;
    assert(!schedule.Trigger(1002, s));
    s.randomGlitches = true;
    assert(schedule.Sample(1002, s).type == -1);
    s.glitchIntensity = .25f;
    assert(schedule.Trigger(1002, s));
    assert(BuiltInPreset(1).randomGlitches && BuiltInPreset(2).mask == 1);
    std::cout << "PASS Windows settings bounds, presets, allowed types, rest and cancellation\n";
}
