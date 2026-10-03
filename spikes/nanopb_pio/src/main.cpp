// SPIKE ONLY: proves nanopb generation and decoding compile for the ESP32 target.
#include <Arduino.h>
#include <pb_decode.h>

#include "volco_config.pb.h"

static const uint8_t kScreenDefault[] = {0x12, 0x02, 0x22, 0x00};

void setup() {
  Serial.begin(115200);
  volco_config_ControllerConfig cfg = volco_config_ControllerConfig_init_default;
  pb_istream_t s = pb_istream_from_buffer(kScreenDefault, sizeof kScreenDefault);
  bool ok = pb_decode(&s, volco_config_ControllerConfig_fields, &cfg);
  Serial.printf("decode=%d buttons=%u\n", ok, (unsigned)cfg.buttons_count);
}

void loop() {}
