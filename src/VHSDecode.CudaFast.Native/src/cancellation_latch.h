#pragma once

#include <atomic>

namespace vhsdecode::cuda_fast::detail {

// Cancellation is monotonic and is polled from both the pipeline/output thread
// and the overlapping RF-prefetch thread. Keep the latched state atomic so a
// cancellation observed by either side is visible to the other without a data
// race.
class CancellationLatch final {
public:
    bool requested() const noexcept {
        return requested_.load(std::memory_order_acquire);
    }

    template <typename PollCallback>
    bool poll(PollCallback&& callback) {
        if (requested()) return true;
        if (callback()) {
            requested_.store(true, std::memory_order_release);
            return true;
        }
        return requested();
    }

private:
    std::atomic_bool requested_{false};
};

}  // namespace vhsdecode::cuda_fast::detail
