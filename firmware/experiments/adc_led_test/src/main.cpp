// Throwaway experiment: how much does each indicator LED move the slider readings?
//
// Pins: proto/presets/diy-reference.json.
//   Sliders 0..4 on 33, 35, 32, 36, 34.
//   Button 0 (pin 4)  toggles LED0 (pin 12, active low).
//   Button 1 (pin 14) toggles LED1 (pin 21, active low).
//   Button 2 (pin 5)  cycles the pair: off -> A (18) -> B (19) -> off (active low).
//
// Serial commands (type the letter, then Enter if your monitor needs it):
//   a  auto test: every LED state against all-off, averaged, 3 rounds
//   f  firmware mode: the real firmware's EMA + deadband; prints SEND like the app would get
//   r  (firmware mode) re-baseline, like a Hello: last sent = current smoothed value
//   +/-  (firmware mode) deadband +-5
//   w  watch: averaged readings every 300 ms
//   x  stop (idle)
//   1/2/3  same as pressing button 0/1/2
//   h  help

#include <Arduino.h>

static const uint8_t SLIDER_PINS[] = {33, 35, 32, 36, 34};
constexpr int NS = sizeof SLIDER_PINS;
static const uint8_t BUTTON_PINS[] = {4, 14, 5};
constexpr int NB = sizeof BUTTON_PINS;

struct Led { uint8_t pin; bool activeLow; const char* name; };
static const Led LEDS[] = {
    {12, true, "LED0"}, {21, true, "LED1"}, {18, true, "PAIR-A"}, {19, true, "PAIR-B"}};
constexpr int NL = sizeof LEDS / sizeof LEDS[0];
static bool ledOn[NL];

enum Mode { IDLE, FIRMWARE, WATCH };
static Mode mode = FIRMWARE;

// Firmware mode state (defaults from proto/volco_config.proto InputTuning).
static const uint32_t SLIDER_INTERVAL_MS = 10;
static const float FILTER_WEIGHT = 0.20f;
static int deadband = 25;
static float smoothed[NS];
static uint16_t lastSent[NS];
static uint32_t lastSliderRead = 0;
static uint32_t lastWatch = 0;

// Buttons: same timestamp debounce as the firmware (30 ms).
static bool rawState[NB], reportedState[NB];
static uint32_t changeTime[NB];

static void setLed(int i, bool on) {
  ledOn[i] = on;
  digitalWrite(LEDS[i].pin, (on != LEDS[i].activeLow) ? HIGH : LOW);
}

static void setMask(uint8_t mask) {
  for (int i = 0; i < NL; i++) setLed(i, mask & (1 << i));
}

static uint8_t currentMask() {
  uint8_t m = 0;
  for (int i = 0; i < NL; i++) if (ledOn[i]) m |= 1 << i;
  return m;
}

static void printLeds() {
  Serial.print("LEDs on: [");
  bool any = false;
  for (int i = 0; i < NL; i++)
    if (ledOn[i]) { Serial.printf("%s%s", any ? " " : "", LEDS[i].name); any = true; }
  Serial.print(any ? "]" : "none]");
}

static void printMask(uint8_t mask) {
  bool any = false;
  for (int i = 0; i < NL; i++)
    if (mask & (1 << i)) { Serial.printf("%s%s", any ? "+" : "", LEDS[i].name); any = true; }
}

// Round-robin reads like the firmware's loop; mean, min and max per slider.
struct Stats { float mean[NS]; uint16_t lo[NS], hi[NS]; };
static Stats measure(int rounds) {
  Stats s;
  uint32_t sum[NS] = {0};
  for (int i = 0; i < NS; i++) { s.lo[i] = 4095; s.hi[i] = 0; }
  for (int r = 0; r < rounds; r++)
    for (int i = 0; i < NS; i++) {
      uint16_t v = analogRead(SLIDER_PINS[i]);
      sum[i] += v;
      if (v < s.lo[i]) s.lo[i] = v;
      if (v > s.hi[i]) s.hi[i] = v;
    }
  for (int i = 0; i < NS; i++) s.mean[i] = (float)sum[i] / rounds;
  return s;
}

static void baseline() {
  for (int i = 0; i < NS; i++) lastSent[i] = (uint16_t)smoothed[i];
}

static void seedFirmwareMode() {
  // Like setup() in the firmware: 8 reads averaged, then a Hello sends these values.
  Stats s = measure(8);
  for (int i = 0; i < NS; i++) smoothed[i] = s.mean[i];
  baseline();
  Serial.printf("[fw] seeded; deadband %d, EMA weight %.2f, every %lu ms. Last sent:", deadband,
                FILTER_WEIGHT, (unsigned long)SLIDER_INTERVAL_MS);
  for (int i = 0; i < NS; i++) Serial.printf(" s%d=%u", i, lastSent[i]);
  Serial.println();
}

static void help() {
  Serial.println();
  Serial.println("=== ADC vs LED experiment ===");
  Serial.println("a auto test | f firmware mode | r re-baseline | +/- deadband | w watch | x idle | 1/2/3 buttons | h help");
  Serial.println("Buttons: 0 toggles LED0, 1 toggles LED1, 2 cycles pair off/A/B. Don't touch the sliders during a test.");
}

static const uint8_t AUTO_STATES[] = {0x1, 0x2, 0x4, 0x8, 0x3, 0xF};

static void autoTest() {
  const int ROUNDS = 512;
  const int REPEATS = 3;
  uint8_t restore = currentMask();
  Serial.println();
  Serial.printf("[auto] %d rounds x %d sliders per measurement, 300 ms settle; delta = state - all-off\n", ROUNDS, NS);

  setMask(0);
  delay(300);
  Stats pos = measure(ROUNDS);
  Serial.print("[auto] positions (all off):");
  for (int i = 0; i < NS; i++) Serial.printf("  s%d=%.1f (p-p %u)", i, pos.mean[i], pos.hi[i] - pos.lo[i]);
  Serial.println();

  float worst[sizeof AUTO_STATES] = {0};
  for (int rep = 0; rep < REPEATS; rep++) {
    Serial.printf("[auto] round %d\n", rep + 1);
    for (size_t k = 0; k < sizeof AUTO_STATES; k++) {
      setMask(0);
      delay(300);
      Stats off = measure(ROUNDS);
      setMask(AUTO_STATES[k]);
      delay(300);
      Stats on = measure(ROUNDS);
      setMask(0);

      Serial.print("  ");
      printMask(AUTO_STATES[k]);
      Serial.print("\t delta:");
      for (int i = 0; i < NS; i++) {
        float d = on.mean[i] - off.mean[i];
        float pct = off.mean[i] > 1 ? 100.0f * d / off.mean[i] : 0;
        Serial.printf("  s%d %+6.1f (%+5.2f%%)", i, d, pct);
        if (fabsf(d) > worst[k]) worst[k] = fabsf(d);
      }
      Serial.println();
    }
  }

  Serial.printf("[auto] summary (worst |delta| over %d rounds; firmware deadband %d):\n", REPEATS, deadband);
  for (size_t k = 0; k < sizeof AUTO_STATES; k++) {
    Serial.print("  ");
    printMask(AUTO_STATES[k]);
    Serial.printf("\t%.1f\t%s\n", worst[k], worst[k] > deadband ? "CROSSES deadband" : "under deadband");
  }
  setMask(restore);
  Serial.println("[auto] done");
}

static void pressButton(int b) {
  switch (b) {
    case 0: setLed(0, !ledOn[0]); break;
    case 1: setLed(1, !ledOn[1]); break;
    case 2:
      if (!ledOn[2] && !ledOn[3]) setLed(2, true);
      else if (ledOn[2]) { setLed(2, false); setLed(3, true); }
      else setLed(3, false);
      break;
  }
  Serial.printf("%8lu ms  button %d -> ", (unsigned long)millis(), b);
  printLeds();
  Serial.println();
}

static void pollButtons(uint32_t now) {
  for (int i = 0; i < NB; i++) {
    bool raw = digitalRead(BUTTON_PINS[i]);
    if (raw != rawState[i]) { rawState[i] = raw; changeTime[i] = now; }
    if (now - changeTime[i] >= 30 && rawState[i] != reportedState[i]) {
      reportedState[i] = rawState[i];
      if (reportedState[i] == LOW) pressButton(i);
    }
  }
}

static void pollSerial() {
  while (Serial.available()) {
    char c = Serial.read();
    switch (c) {
      case 'a': autoTest(); if (mode == FIRMWARE) seedFirmwareMode(); break;
      case 'f': mode = FIRMWARE; seedFirmwareMode(); break;
      case 'r': baseline(); Serial.printf("%8lu ms  [fw] re-baselined (like a Hello)\n", (unsigned long)millis()); break;
      case '+': deadband += 5; Serial.printf("[fw] deadband %d\n", deadband); break;
      case '-': deadband = max(0, deadband - 5); Serial.printf("[fw] deadband %d\n", deadband); break;
      case 'w': mode = WATCH; Serial.println("[watch] averaged (64 rounds) every 300 ms"); break;
      case 'x': mode = IDLE; Serial.println("[idle]"); break;
      case '1': case '2': case '3': pressButton(c - '1'); break;
      case 'h': case '?': help(); break;
      default: break;
    }
  }
}

void setup() {
  Serial.begin(115200);
  for (int i = 0; i < NS; i++) pinMode(SLIDER_PINS[i], INPUT);
  for (int i = 0; i < NB; i++) {
    pinMode(BUTTON_PINS[i], INPUT_PULLUP);
    rawState[i] = reportedState[i] = HIGH;
  }
  for (int i = 0; i < NL; i++) { pinMode(LEDS[i].pin, OUTPUT); setLed(i, false); }
  analogReadResolution(12);
  delay(1500);  // time to open the monitor
  help();
  autoTest();
  seedFirmwareMode();
  Serial.println("[fw] firmware mode: press buttons; SEND lines are what the app would receive");
}

void loop() {
  const uint32_t now = millis();
  pollSerial();
  pollButtons(now);

  if (mode == FIRMWARE && now - lastSliderRead >= SLIDER_INTERVAL_MS) {
    lastSliderRead = now;
    for (int i = 0; i < NS; i++) {
      uint16_t raw = analogRead(SLIDER_PINS[i]);
      smoothed[i] = raw * FILTER_WEIGHT + smoothed[i] * (1.0f - FILTER_WEIGHT);
      uint16_t filtered = (uint16_t)smoothed[i];
      int delta = (int)filtered - (int)lastSent[i];
      if (abs(delta) > deadband) {
        Serial.printf("%8lu ms  SEND s%d=%u (%+d from last sent %u)\n", (unsigned long)now, i, filtered, delta,
                      lastSent[i]);
        lastSent[i] = filtered;
      }
    }
  }

  if (mode == WATCH && now - lastWatch >= 300) {
    lastWatch = now;
    Stats s = measure(64);
    Serial.printf("%8lu ms ", (unsigned long)now);
    for (int i = 0; i < NS; i++) Serial.printf(" s%d=%7.1f", i, s.mean[i]);
    Serial.print("  ");
    printLeds();
    Serial.println();
  }
}
