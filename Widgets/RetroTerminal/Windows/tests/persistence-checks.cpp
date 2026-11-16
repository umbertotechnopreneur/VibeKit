// SPDX-License-Identifier: MIT
#include "../SettingsSnapshot.h"
#include "../ClosePolicy.h"
#include <cassert>
#include <iostream>
#include <limits>
#include <string>

int main()
{
    EffectSettings source;
    for (const auto& spec : FloatControls) {
        const float defaultValue = source.*(spec.field);
        assert(defaultValue >= spec.low && defaultValue <= spec.high);
        source.*(spec.field) = (spec.low + spec.high) / 2;
    }
    source.palette = Palette::Custom;
    source.mask = PhosphorMask::Dots;
    source.color = {.125f, .5f, .875f};
    source.persistence = source.randomGlitches = true;
    source.glitchTypes = 1u << unsigned(GlitchType::Band);
    source.Clamp();
    const auto snapshot = EncodeSettings(source);
    EffectSettings restored;
    assert(DecodeSettings(snapshot, restored));
    assert(EncodeSettings(restored) == snapshot);

    const auto unchanged = EncodeSettings(restored);
    for (size_t length = 0; length < snapshot.size(); ++length) {
        assert(!DecodeSettings(std::span(snapshot.data(), length), restored));
        assert(EncodeSettings(restored) == unchanged);
    }
    auto invalid = snapshot;
    invalid[1]++; // Unsupported format cannot overwrite the current values.
    assert(!DecodeSettings(invalid, restored));
    invalid = snapshot;
    invalid[3] = std::bit_cast<uint32_t>(std::numeric_limits<float>::quiet_NaN());
    assert(!DecodeSettings(invalid, restored));
    invalid = snapshot;
    invalid.back() = 0xffff;
    assert(!DecodeSettings(invalid, restored));
    assert(EncodeSettings(restored) == unchanged);

    // The same descriptors drive clamping, controls and serialization.
    for (const auto& spec : FloatControls) {
        EffectSettings invalidSettings;
        invalidSettings.*(spec.field) = std::numeric_limits<float>::infinity();
        invalidSettings.Clamp();
        assert(std::isfinite(invalidSettings.*(spec.field)));
        assert(invalidSettings.*(spec.field) >= spec.low && invalidSettings.*(spec.field) <= spec.high);
    }

    std::string calls;
    int attempts = 0;
    assert(RequestSafeClose([&] { calls += 'X'; }, [&] { calls += 'S'; return ++attempts == 2; },
        [&] { calls += 'R'; return SaveFailureChoice::Retry; }));
    assert(calls == "XSRS"); // Stop precedes even the first failed save.
    calls.clear();
    assert(!RequestSafeClose([&] { calls += 'X'; }, [&] { calls += 'S'; return false; },
        [&] { calls += 'K'; return SaveFailureChoice::KeepOpen; }));
    assert(calls == "XSK");
    calls.clear();
    assert(RequestSafeClose([&] { calls += 'X'; }, [&] { calls += 'S'; return false; },
        [&] { calls += 'D'; return SaveFailureChoice::CloseWithoutSaving; }));
    assert(calls == "XSD");
    std::cout << "PASS settings snapshot roundtrip/rejection, descriptor bounds, stop-before-save and close decisions\n";
}
