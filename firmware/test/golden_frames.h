#pragma once
// Loads firmware/test/golden/frames.txt (shared with the C# tests) for native tests.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

struct GoldenFrame { std::string name; std::vector<uint8_t> bytes; };

inline std::vector<GoldenFrame> loadGolden() {
  // The test binary's working directory varies; derive the path from this header's location.
  std::string here = __FILE__;
  std::string dir = here.substr(0, here.find_last_of("/\\") + 1);
  const char* candidates[] = {"golden/frames.txt", "test/golden/frames.txt", "../test/golden/frames.txt"};
  FILE* f = fopen((dir + "golden/frames.txt").c_str(), "r");
  for (const char* c : candidates) if (!f) f = fopen(c, "r");
  std::vector<GoldenFrame> out;
  if (!f) return out;
  char line[1024];
  while (fgets(line, sizeof line, f)) {
    if (line[0] == '#' || line[0] == '\n' || line[0] == '\r') continue;
    GoldenFrame g;
    char* tok = strtok(line, " \t\r\n");
    if (!tok) continue;
    g.name = tok;
    while ((tok = strtok(nullptr, " \t\r\n"))) g.bytes.push_back((uint8_t)strtoul(tok, nullptr, 16));
    out.push_back(g);
  }
  fclose(f);
  return out;
}

inline std::vector<uint8_t> golden(const char* name) {
  static std::vector<GoldenFrame> all = loadGolden();
  for (auto& g : all) if (g.name == name) return g.bytes;
  return {};
}
