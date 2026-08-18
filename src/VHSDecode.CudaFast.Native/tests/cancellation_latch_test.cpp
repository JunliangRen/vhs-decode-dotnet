#include "cancellation_latch.h"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <thread>
#include <vector>

int main() {
    using vhsdecode::cuda_fast::detail::CancellationLatch;

    // Model the output thread and overlapping RF-prefetch readers polling the
    // same managed cancellation callback. Every participant must observe the
    // first cancellation, and the latched state must remain true afterwards.
    constexpr size_t kWorkerCount = 24;
    CancellationLatch latch;
    std::atomic_bool start{false};
    std::atomic_bool external_cancellation{false};
    std::atomic_uint32_t ready{0};
    std::atomic_uint64_t callback_polls{0};
    std::array<std::atomic_bool, kWorkerCount> observed{};
    std::vector<std::thread> workers;
    workers.reserve(kWorkerCount);

    for (size_t worker = 0; worker < kWorkerCount; ++worker) {
        workers.emplace_back([&, worker]() {
            ready.fetch_add(1, std::memory_order_release);
            while (!start.load(std::memory_order_acquire)) {
                std::this_thread::yield();
            }
            while (!latch.poll([&]() {
                callback_polls.fetch_add(1, std::memory_order_relaxed);
                return external_cancellation.load(std::memory_order_acquire);
            })) {
                std::this_thread::yield();
            }
            observed[worker].store(true, std::memory_order_release);
        });
    }

    while (ready.load(std::memory_order_acquire) != kWorkerCount) {
        std::this_thread::yield();
    }
    start.store(true, std::memory_order_release);
    while (callback_polls.load(std::memory_order_acquire) < kWorkerCount) {
        std::this_thread::yield();
    }
    external_cancellation.store(true, std::memory_order_release);

    for (std::thread& worker : workers) worker.join();
    for (const std::atomic_bool& value : observed) {
        if (!value.load(std::memory_order_acquire)) {
            std::fprintf(stderr, "A concurrent cancellation poller missed the latched state.\n");
            return 1;
        }
    }

    const uint64_t polls_before_latched_check =
        callback_polls.load(std::memory_order_acquire);
    external_cancellation.store(false, std::memory_order_release);
    for (int iteration = 0; iteration < 100'000; ++iteration) {
        if (!latch.poll([&]() {
            callback_polls.fetch_add(1, std::memory_order_relaxed);
            return false;
        })) {
            std::fprintf(stderr, "Cancellation latch reverted after being set.\n");
            return 2;
        }
    }
    if (callback_polls.load(std::memory_order_acquire) != polls_before_latched_check) {
        std::fprintf(stderr, "A latched cancellation unexpectedly polled the callback again.\n");
        return 3;
    }

    // Model cancellation arriving inside a managed RF read: the callback's
    // entry poll saw false, the read then returned zero after observing its
    // token, and the bridge must refresh that token after pipeline teardown
    // instead of reporting the short read as a decode error.
    CancellationLatch read_latch;
    std::atomic_bool cancelled_during_read{false};
    if (read_latch.poll([&]() {
        return cancelled_during_read.load(std::memory_order_acquire);
    })) {
        std::fprintf(stderr, "The late-read test started in a cancelled state.\n");
        return 4;
    }
    const size_t samples_read = [&]() {
        cancelled_during_read.store(true, std::memory_order_release);
        return size_t{0};
    }();
    if (samples_read != 0 || read_latch.requested()) {
        std::fprintf(stderr, "The late-read test did not reproduce an unlatched short read.\n");
        return 5;
    }
    if (!read_latch.poll([&]() {
        return cancelled_during_read.load(std::memory_order_acquire);
    })) {
        std::fprintf(stderr, "Post-pipeline cancellation refresh missed a token set during read.\n");
        return 6;
    }

    std::printf("Parallel preview/prefetch cancellation latch test passed.\n");
    return 0;
}
