#include "volco_bidi.h"

namespace volco {
namespace {

constexpr int kMax = 128;  // code points; text slots are at most 60 bytes
enum Cls : uint8_t { L, R, EN, CS, N };

uint32_t decode(const char* s, size_t len, size_t& i) {
  uint8_t c = (uint8_t)s[i++];
  if (c < 0x80) return c;
  int extra = c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : c >= 0xC0 ? 1 : 0;
  uint32_t cp = c & (0x3F >> extra);
  for (int k = 0; k < extra && i < len; k++) cp = (cp << 6) | ((uint8_t)s[i++] & 0x3F);
  return cp;
}

size_t encode(uint32_t cp, char* out) {
  if (cp < 0x80) { out[0] = (char)cp; return 1; }
  if (cp < 0x800) { out[0] = (char)(0xC0 | (cp >> 6)); out[1] = (char)(0x80 | (cp & 0x3F)); return 2; }
  if (cp < 0x10000) {
    out[0] = (char)(0xE0 | (cp >> 12)); out[1] = (char)(0x80 | ((cp >> 6) & 0x3F)); out[2] = (char)(0x80 | (cp & 0x3F));
    return 3;
  }
  out[0] = (char)(0xF0 | (cp >> 18)); out[1] = (char)(0x80 | ((cp >> 12) & 0x3F));
  out[2] = (char)(0x80 | ((cp >> 6) & 0x3F)); out[3] = (char)(0x80 | (cp & 0x3F));
  return 4;
}

Cls classify(uint32_t cp) {
  if ((cp >= 0x5D0 && cp <= 0x5EA) || (cp >= 0x5F0 && cp <= 0x5F4)) return R;
  if (cp >= '0' && cp <= '9') return EN;
  if (cp == '.' || cp == ',' || cp == ':' || cp == '/') return CS;
  if ((cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z')) return L;
  if (cp >= 0xC0 && cp <= 0x24F && cp != 0xD7 && cp != 0xF7) return L;
  if (cp >= 0x370 && cp < 0x590) return L;  // Greek, Cyrillic, …
  return N;
}

uint32_t mirror(uint32_t cp) {
  switch (cp) {
    case '(': return ')'; case ')': return '(';
    case '[': return ']'; case ']': return '[';
    case '{': return '}'; case '}': return '{';
    case '<': return '>'; case '>': return '<';
    default: return cp;
  }
}

}  // namespace

size_t bidiVisual(const char* in, size_t len, char* out, size_t cap, bool* rtl) {
  uint32_t cp[kMax];
  Cls cls[kMax];
  int n = 0;
  for (size_t i = 0; i < len && n < kMax;) {
    cp[n] = decode(in, len, i);
    cls[n] = classify(cp[n]);
    n++;
  }

  // W4 (simplified): a separator between two digits joins the number.
  for (int i = 1; i + 1 < n; i++)
    if (cls[i] == CS && cls[i - 1] == EN && cls[i + 1] == EN) cls[i] = EN;

  // Paragraph direction: the first strong character.
  bool paraRtl = false;
  for (int i = 0; i < n; i++)
    if (cls[i] == L || cls[i] == R) { paraRtl = cls[i] == R; break; }
  if (rtl) *rtl = paraRtl;

  // Resolve every character to a direction (true = RTL).
  bool dir[kMax];
  bool lastStrongRtl = paraRtl;
  for (int i = 0; i < n; i++) {
    if (cls[i] == L || cls[i] == R) lastStrongRtl = cls[i] == R;
    if (cls[i] == L) dir[i] = false;
    else if (cls[i] == R) dir[i] = true;
    else if (cls[i] == EN) dir[i] = lastStrongRtl;   // a number joins the run before it
  }
  for (int i = 0; i < n; i++) {
    if (cls[i] != N && cls[i] != CS) continue;
    int j = i;
    while (j < n && (cls[j] == N || cls[j] == CS)) j++;
    bool before = i > 0 ? dir[i - 1] : paraRtl;
    bool after = j < n ? dir[j] : paraRtl;
    bool d = before == after ? before : paraRtl;
    for (int k = i; k < j; k++) dir[k] = d;
    i = j - 1;
  }

  // Runs of one direction; RTL runs are reversed (numbers inside keep their order) and mirrored,
  // and an RTL paragraph also reverses the order of its runs.
  int runStart[kMax], runEnd[kMax], runs = 0;
  for (int i = 0; i < n;) {
    int j = i;
    while (j < n && dir[j] == dir[i]) j++;
    runStart[runs] = i;
    runEnd[runs] = j;
    runs++;
    i = j;
  }

  size_t w = 0;
  auto put = [&](uint32_t c) {
    char buf[4];
    size_t k = encode(c, buf);
    for (size_t b = 0; b < k && w < cap; b++) out[w++] = buf[b];
  };
  for (int r = 0; r < runs; r++) {
    int idx = paraRtl ? runs - 1 - r : r;
    int s = runStart[idx], e = runEnd[idx];
    if (!dir[s]) {
      for (int i = s; i < e; i++) put(cp[i]);
      continue;
    }
    for (int i = e - 1; i >= s;) {
      if (cls[i] == EN) {
        int k = i;
        while (k - 1 >= s && cls[k - 1] == EN) k--;
        for (int m = k; m <= i; m++) put(cp[m]);
        i = k - 1;
      } else {
        put(mirror(cp[i]));
        i--;
      }
    }
  }
  if (w < cap) out[w] = 0;
  return w;
}

}  // namespace volco
