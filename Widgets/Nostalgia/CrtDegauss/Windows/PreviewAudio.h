// SPDX-License-Identifier: MIT
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <string_view>
#include <vector>

namespace degauss
{
    inline std::vector<unsigned char> AmplifyPreview(const std::filesystem::path& path, int percent)
    {
        std::ifstream stream(path, std::ios::binary | std::ios::ate);
        const auto size = stream.tellg();
        if (size < 44 || size > 1024 * 1024) return {};
        std::vector<unsigned char> wav(static_cast<size_t>(size));
        stream.seekg(0);
        if (!stream.read(reinterpret_cast<char*>(wav.data()), size)) return {};

        auto tag = [&](size_t offset) { return std::string_view(reinterpret_cast<const char*>(wav.data() + offset), 4); };
        auto word = [&](size_t offset) { return unsigned(wav[offset]) | (unsigned(wav[offset + 1]) << 8); };
        auto dword = [&](size_t offset) { return word(offset) | (word(offset + 2) << 16); };
        if (tag(0) != "RIFF" || tag(8) != "WAVE" || dword(4) > wav.size() - 8) return {};
        const size_t end = 8 + dword(4);
        bool pcm16 = false;
        size_t data = 0, bytes = 0;
        for (size_t chunk = 12; chunk + 8 <= end;) {
            const size_t count = dword(chunk + 4);
            const size_t payload = chunk + 8;
            if (count > end - payload) return {};
            if (tag(chunk) == "fmt " && count >= 16)
                pcm16 = word(payload) == 1 && word(payload + 14) == 16;
            else if (tag(chunk) == "data") {
                data = payload;
                bytes = count;
            }
            chunk = payload + count + (count % 2);
        }
        if (!pcm16 || bytes < 2 || bytes % 2) return {};

        const double gain = 1.0 + std::clamp(percent, -50, 50) / 100.0;
        for (size_t offset = data; offset < data + bytes; offset += 2) {
            const auto sample = static_cast<std::int16_t>(word(offset));
            const auto adjusted = static_cast<std::uint16_t>(std::clamp(std::lround(sample * gain), -32768L, 32767L));
            wav[offset] = static_cast<unsigned char>(adjusted & 0xff);
            wav[offset + 1] = static_cast<unsigned char>(adjusted >> 8);
        }
        return wav;
    }
}
