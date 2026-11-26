// SPDX-License-Identifier: MIT
#include "../PresentationRetry.h"
#include <cassert>

int main()
{
    using namespace std::chrono_literals;
    const std::chrono::steady_clock::time_point start{};
    PresentationRetry retry;
    assert(!retry.Waiting(start));
    retry.Occluded(start);
    assert(retry.Waiting(start + 99ms));
    assert(!retry.Waiting(start + 100ms));
    retry.Occluded(start + 100ms);
    assert(!retry.Expired(start + 2999ms));
    assert(retry.Expired(start + 3s));
    retry.Presented();
    assert(!retry.Expired(start + 4s));
    retry.Occluded(start + 5s);
    retry.Presented();
    assert(!retry.Waiting(start + 5s));
    assert(!retry.Expired(start + 9s));
}
