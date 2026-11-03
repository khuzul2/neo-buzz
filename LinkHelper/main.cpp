// ReBuzzLink: Ableton Link for ReBuzz, in its own process.
//
// Copyright (C) 2026 the neo-buzz contributors.
// This program is free software; you can redistribute it and/or modify it under the terms of
// the GNU General Public License as published by the Free Software Foundation; either version
// 2 of the License, or (at your option) any later version. It links Ableton Link
// (https://github.com/Ableton/link), which is under the same licence. See README.md.
//
// ReBuzz starts this program and talks to it in lines of text (docs/specs/session-view.md 0.8):
//
//   in (from ReBuzz)                       out (to ReBuzz)
//   enable 0|1                             hello <protocol>
//   startstop 0|1                          state <micros> <beat> <bpm> <peers> <playing> <enabled>
//   quantum <beats>                          (every 10 ms: the beat at a host time, for the quantum)
//   tempo <bpm>
//   beat <beat> <micros>                   (request: the beat at a host time; with peers, Link
//   play 0|1 <micros>                       keeps the session's phase)
//   quit
//
// Times are Link's clock in microseconds, the clock .NET's Stopwatch reads on each OS, so ReBuzz
// compares them with its own times as they are:
//   Windows: QueryPerformanceCounter, Link's own clock there;
//   macOS:   mach_absolute_time, Link's own clock there (Stopwatch reads it as CLOCK_UPTIME_RAW);
//   Linux:   CLOCK_MONOTONIC. Link's own is CLOCK_MONOTONIC_RAW, which NTP doesn't slew, so it
//            drifts from Stopwatch's; this program gives Link CLOCK_MONOTONIC instead.
// It exits when ReBuzz closes its input, so it never outlives ReBuzz.

#include <ableton/Link.hpp>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>

namespace
{
constexpr int Protocol = 1;

#if defined(LINK_PLATFORM_LINUX)
using HostLink = ableton::BasicLink<ableton::platforms::linux_::ClockMonotonic>;
#else
using HostLink = ableton::Link;
#endif

std::mutex outMutex;

void say(const std::string& line)
{
  std::lock_guard<std::mutex> lock(outMutex);
  std::fwrite(line.data(), 1, line.size(), stdout);
  std::fputc('\n', stdout);
  std::fflush(stdout);
}
} // namespace

int main()
{
  std::ios::sync_with_stdio(false);
  HostLink link(120.0);
  std::atomic<double> quantum{4.0};
  std::atomic<bool> running{true};

  say("hello " + std::to_string(Protocol));

  std::thread reporter([&] {
    while (running.load())
    {
      const auto now = link.clock().micros();
      const auto state = link.captureAppSessionState();
      char buffer[256];
      std::snprintf(buffer, sizeof buffer, "state %lld %.9f %.6f %zu %d %d",
        static_cast<long long>(now.count()), state.beatAtTime(now, quantum.load()), state.tempo(),
        link.numPeers(), state.isPlaying() ? 1 : 0, link.isEnabled() ? 1 : 0);
      say(buffer);
      std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
  });

  std::string line;
  while (std::getline(std::cin, line))
  {
    std::istringstream in(line);
    std::string command;
    in >> command;
    if (command == "enable")
    {
      int on = 0;
      if (in >> on) link.enable(on != 0);
    }
    else if (command == "startstop")
    {
      int on = 0;
      if (in >> on) link.enableStartStopSync(on != 0);
    }
    else if (command == "quantum")
    {
      double q = 0;
      if (in >> q && q >= 1 && q <= 64) quantum.store(q);
    }
    else if (command == "tempo")
    {
      double bpm = 0;
      if (in >> bpm && bpm >= 20 && bpm <= 999)
      {
        auto state = link.captureAppSessionState();
        state.setTempo(bpm, link.clock().micros());
        link.commitAppSessionState(state);
      }
    }
    else if (command == "beat")
    {
      double beat = 0;
      long long at = 0;
      if (in >> beat >> at)
      {
        auto state = link.captureAppSessionState();
        state.requestBeatAtTime(beat, std::chrono::microseconds(at), quantum.load());
        link.commitAppSessionState(state);
      }
    }
    else if (command == "play")
    {
      int on = 0;
      long long at = 0;
      if (in >> on >> at)
      {
        auto state = link.captureAppSessionState();
        state.setIsPlaying(on != 0, std::chrono::microseconds(at));
        link.commitAppSessionState(state);
      }
    }
    else if (command == "quit")
      break;
  }

  running.store(false);
  reporter.join();
  link.enable(false);
  return 0;
}
