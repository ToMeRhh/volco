#pragma once
// Loads and rewrites firmware/test/golden/render.txt, the layout-1 renders shared with
// tools/simulator/test/oled.test.mjs: <name> <Screen payload hex> <scroll ms> <canvas hex or ->.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

struct GoldenRender {
  std::string name;
  std::vector<uint8_t> payload;
  uint32_t since = 0;
  std::vector<uint8_t> canvas;   // empty until generated
};

inline std::string goldenRenderPath() {
  std::string here = __FILE__;   // the test binary's working directory varies
  return here.substr(0, here.find_last_of("/\\") + 1) + "golden/render.txt";
}

inline std::vector<uint8_t> goldenHex(const char* s) {
  std::vector<uint8_t> out;
  if (strcmp(s, "-") == 0) return out;
  for (size_t i = 0; s[i] && s[i + 1]; i += 2) {
    char b[3] = {s[i], s[i + 1], 0};
    out.push_back((uint8_t)strtoul(b, nullptr, 16));
  }
  return out;
}

// header: the comment lines, kept for writing back.
inline bool loadGoldenRenders(std::vector<std::string>& header, std::vector<GoldenRender>& out) {
  FILE* f = fopen(goldenRenderPath().c_str(), "r");
  if (!f) return false;
  static char line[4096];
  while (fgets(line, sizeof line, f)) {
    if (line[0] == '#') { header.push_back(line); continue; }
    char* name = strtok(line, " \t\r\n");
    char* payload = strtok(nullptr, " \t\r\n");
    char* since = strtok(nullptr, " \t\r\n");
    char* canvas = strtok(nullptr, " \t\r\n");
    if (!name || !payload || !since || !canvas) continue;
    GoldenRender g;
    g.name = name;
    g.payload = goldenHex(payload);
    g.since = (uint32_t)strtoul(since, nullptr, 10);
    g.canvas = goldenHex(canvas);
    out.push_back(g);
  }
  fclose(f);
  return true;
}

inline bool writeGoldenRenders(const std::vector<std::string>& header, const std::vector<GoldenRender>& cases) {
  FILE* f = fopen(goldenRenderPath().c_str(), "w");
  if (!f) return false;
  for (const std::string& h : header) fputs(h.c_str(), f);
  for (const GoldenRender& g : cases) {
    fprintf(f, "%s ", g.name.c_str());
    for (uint8_t b : g.payload) fprintf(f, "%02X", b);
    fprintf(f, " %u ", (unsigned)g.since);
    if (g.canvas.empty()) fputc('-', f);
    for (uint8_t b : g.canvas) fprintf(f, "%02X", b);
    fputc('\n', f);
  }
  fclose(f);
  return true;
}
