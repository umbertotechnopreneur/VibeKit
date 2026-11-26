// SPDX-License-Identifier: MIT
#include "Mixer.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <memory>
#include <new>
#include <string_view>
#include <vector>

namespace {
constexpr unsigned Rate = 24000;
constexpr float Frames(int seconds) { return static_cast<float>(seconds) * Rate; }
using Clip = std::vector<float>;
constexpr std::array Names{"startup.wav", "idle.wav", "shutdown.wav", "seek1.wav", "seek2.wav", "seek3.wav", "seek4.wav", "error.wav"};

Clip ReadClip(const std::filesystem::path& path)
{
    std::ifstream stream(path, std::ios::binary | std::ios::ate);
    const auto bytes = stream.tellg();
    if (bytes < 44 || bytes > 2 * Rate * 20 + 4096) return {};
    std::vector<unsigned char> file(static_cast<size_t>(bytes));
    stream.seekg(0);
    if (!stream.read(reinterpret_cast<char*>(file.data()), bytes)) return {};
    auto tag = [&](size_t at) { return std::string_view(reinterpret_cast<const char*>(file.data() + at), 4); };
    auto word = [&](size_t at) { return unsigned(file[at]) | (unsigned(file[at + 1]) << 8); };
    auto dword = [&](size_t at) { return word(at) | (word(at + 2) << 16); };
    if (tag(0) != "RIFF" || tag(8) != "WAVE" || dword(4) != file.size() - 8) return {};
    bool format = false;
    size_t data = 0, length = 0;
    for (size_t at = 12; at + 8 <= file.size();) {
        const size_t count = dword(at + 4);
        if (count > file.size() - at - 8) return {};
        if (tag(at) == "fmt " && count >= 16)
            format = word(at + 8) == 1 && word(at + 10) == 1 && dword(at + 12) == Rate &&
                dword(at + 16) == 2 * Rate && word(at + 20) == 2 && word(at + 22) == 16;
        if (tag(at) == "data") { data = at + 8; length = count; }
        at += 8 + count + (count % 2);
    }
    if (!format || length < 2 || length % 2) return {};
    Clip result;
    result.reserve(length / 2);
    for (size_t i = data; i < data + length; i += 2)
        result.push_back(static_cast<int16_t>(word(i)) / 32768.0f);
    return result;
}

struct Voice {
    unsigned clip{};
    size_t position{};
    float gain{};
};

struct Mixer {
    std::array<Clip, Names.size()> clips;
    std::array<Voice, 4> seeks;
    Voice motor;
    bool ready{}, on{}, idle{}, stopping{}, resumeAfterMotor{};
    float volume = .55f, currentVolume{}, idleMix{}, seekRate{}, quietCountdown{}, nextSeek{};
    size_t idlePosition{};
    int quietSeconds = 8, density = 100, retrySeconds = 0;
    float retryCountdown{}, returnDelay = -1;
    uint32_t seed = 0xd6a3dbef;
    bool motorActive() const { return motor.gain > 0; }

    float random()
    {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        return float(seed >> 8) / 16777216.0f;
    }
    void reset()
    {
        seeks = {};
        motor = {};
        seekRate = idleMix = nextSeek = 0;
        currentVolume = volume;
        quietCountdown = Frames(quietSeconds);
        retryCountdown = Frames(retrySeconds);
        returnDelay = -1;
        idlePosition = 0;
        stopping = resumeAfterMotor = false;
    }
    float sample(Voice& voice)
    {
        if (!voice.gain) return 0;
        const auto& clip = clips[voice.clip];
        const float value = clip[voice.position++] * voice.gain;
        if (voice.position == clip.size()) voice.gain = 0;
        return value;
    }
    void seek(float strength)
    {
        const auto voice = std::find_if(seeks.begin(), seeks.end(), [](const Voice& v) { return v.gain == 0; });
        if (voice != seeks.end()) *voice = {3 + static_cast<unsigned>(random() * 4), 0, strength};
    }
    void error(float strength)
    {
        const auto voice = std::find_if(seeks.begin(), seeks.end(), [](const Voice& v) { return v.gain == 0; });
        if (voice != seeks.end()) *voice = {7, 0, strength};
    }
    bool pending() const
    {
        return ready && on && (motorActive() || (!stopping && (idle || idleMix > .0001f || returnDelay >= 0 || seekRate > .25f ||
            std::any_of(seeks.begin(), seeks.end(), [](const Voice& voice) { return voice.gain > 0; }))));
    }
    void activityUpdate(float rate)
    {
        if (stopping) return;
        const float activity = std::isfinite(rate) && rate > 2 ? rate : 0;
        // Four activity observations per second. Idle ticks are independent of I/O.
        quietCountdown -= Rate / 4;
        seekRate += (std::min(26.0f, std::log1p(activity / 8) * .0335f * static_cast<float>(density)) - seekRate) *
            (activity > 0 ? .70f : .50f);
        if (quietCountdown <= 0 && ready && on && quietSeconds && seekRate < .5f) {
            seek(.11f);
            quietCountdown = Frames(quietSeconds) * (.60f + .80f * random());
        } else if (quietCountdown <= 0) quietCountdown = Frames(quietSeconds);
        retryCountdown = std::max(0.0f, retryCountdown - Rate / 4);
        if (ready && on && retrySeconds && retryCountdown == 0 && seekRate >= 1.5f) {
            error(.44f);
            returnDelay = .12f * Rate;
            retryCountdown = Frames(retrySeconds);
        }
    }
    void render(int16_t* pcm, size_t frames)
    {
        for (size_t i = 0; i < frames; ++i) {
            if (!ready || !on) { pcm[i] = 0; continue; }
            currentVolume += (volume - currentVolume) * .002f;
            if (!stopping && seekRate > .25f) {
                if (nextSeek <= 0) {
                    seek(.21f + .22f * random());
                    nextSeek = Rate * (.50f + random()) / seekRate;
                }
                --nextSeek;
            }
            if (returnDelay >= 0 && --returnDelay < 0) seek(.39f);
            const float target = idle && !stopping && !motorActive() ? .19f : 0;
            idleMix += (target - idleMix) * .00018f;
            float mixed = sample(motor);
            if (resumeAfterMotor && !motorActive()) stopping = resumeAfterMotor = false;
            if (idleMix > .0001f) {
                const auto& bed = clips[1];
                mixed += bed[idlePosition] * idleMix * (1 - .20f * std::min(seekRate / 17, 1.0f));
                idlePosition = (idlePosition + 1) % bed.size();
            }
            for (auto& voice : seeks) mixed += sample(voice);
            // Continuous soft peak limiting keeps overlapping seeks from clipping.
            mixed *= currentVolume;
            pcm[i] = static_cast<int16_t>(std::clamp(mixed / (1 + .32f * std::abs(mixed)), -1.f, 1.f) * 32767);
        }
    }
};
}

void* hdd_create() { return new (std::nothrow) Mixer; }
void hdd_destroy(void* value) { delete static_cast<Mixer*>(value); }
int hdd_load(void* value, const char* folder, int retrySeconds)
{
    auto& mixer = *static_cast<Mixer*>(value);
    try {
        const auto path = std::filesystem::path(reinterpret_cast<const char8_t*>(folder));
        std::array<Clip, Names.size()> clips;
        for (size_t i = 0; i < Names.size(); ++i) {
            const auto file = path / Names[i];
            if (i == Names.size() - 1 && !std::filesystem::exists(file)) {
                if (retrySeconds > 0) return -1;
                break;
            }
            clips[i] = ReadClip(file);
            if (clips[i].empty()) return 0;
        }
        mixer.clips = std::move(clips);
        mixer.retrySeconds = std::clamp(retrySeconds, 0, 600);
        mixer.ready = true;
        mixer.reset();
        return 1;
    } catch (...) { return 0; }
}
void hdd_configure(void* value, float volume, int idle, int enabled)
{
    auto& mixer = *static_cast<Mixer*>(value);
    mixer.volume = std::isfinite(volume) ? std::clamp(volume, 0.f, 1.f) : .55f;
    mixer.idle = idle != 0;
    if (mixer.on != (enabled != 0)) mixer.reset();
    mixer.on = enabled != 0;
}
int hdd_frequency(void* value, int quietSeconds, int density, int retrySeconds)
{
    auto& mixer = *static_cast<Mixer*>(value);
    quietSeconds = std::clamp(quietSeconds, 0, 120);
    retrySeconds = std::clamp(retrySeconds, 0, 600);
    if (retrySeconds && mixer.clips.back().empty()) return 0;
    if (mixer.quietSeconds != quietSeconds) mixer.quietCountdown = Frames(quietSeconds);
    if (mixer.retrySeconds != retrySeconds) {
        mixer.retryCountdown = Frames(retrySeconds);
        mixer.returnDelay = -1;
    }
    mixer.quietSeconds = quietSeconds;
    mixer.density = std::clamp(density, 0, 150);
    mixer.retrySeconds = retrySeconds;
    return 1;
}
void hdd_activity(void* value, float operationsPerSecond) { static_cast<Mixer*>(value)->activityUpdate(operationsPerSecond); }
void hdd_trigger(void* value, int kind)
{
    auto& mixer = *static_cast<Mixer*>(value);
    if (!mixer.ready || !mixer.on) return;
    if (kind == 2) {
        if (!mixer.stopping) {
            mixer.seek(.47f);
            mixer.seekRate = std::max(mixer.seekRate, 11.0f);
        }
    } else if (kind == 0 || kind == 1 || kind == 3) {
        mixer.reset();
        mixer.stopping = kind != 0;
        mixer.resumeAfterMotor = kind == 3;
        mixer.motor = {kind == 0 ? 0u : 2u, 0, .82f};
    }
}
int hdd_pending(void* value) { return static_cast<Mixer*>(value)->pending(); }
void hdd_render(void* value, int16_t* pcm, size_t frames) { static_cast<Mixer*>(value)->render(pcm, frames); }
