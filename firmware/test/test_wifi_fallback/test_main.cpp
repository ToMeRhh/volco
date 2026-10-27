#include <pb_encode.h>
#include <string.h>
#include <unity.h>

#include "volco_config.h"
#include "volco_wifi_fallback.h"

using namespace volco;

void setUp() {}
void tearDown() {}

static size_t encode(const char* ssid, uint8_t* buf, size_t cap) {
  volco_config_ControllerConfig c = volco_config_ControllerConfig_init_default;
  c.sliders_count = 2;
  c.sliders[0].has_pin = c.sliders[1].has_pin = true;
  c.sliders[0].pin = 34;
  c.sliders[1].pin = 35;
  c.has_udp = true;
  c.udp.enabled = true;
  c.udp.has_ssid = true;
  strcpy(c.udp.ssid, ssid);
  pb_ostream_t s = pb_ostream_from_buffer(buf, cap);
  TEST_ASSERT_TRUE(pb_encode(&s, volco_config_ControllerConfig_fields, &c));
  return s.bytes_written;
}

void test_replace_field_swaps_udp() {
  uint8_t a[512], b[512], out[512];
  const size_t la = encode("HomeNet", a, sizeof a);
  const size_t lb = encode("Office", b, sizeof b);
  const uint8_t *va, *vb;
  size_t na, nb;
  TEST_ASSERT_TRUE(findField(a, la, kUdpField, va, na));
  TEST_ASSERT_TRUE(findField(b, lb, kUdpField, vb, nb));
  const size_t n = replaceField(a, la, kUdpField, vb, nb, out, sizeof out);
  TEST_ASSERT_GREATER_THAN(0, n);
  volco_config_ControllerConfig c;
  TEST_ASSERT_TRUE(decodeConfig(out, n, c));
  TEST_ASSERT_EQUAL_STRING("Office", c.udp.ssid);
  TEST_ASSERT_EQUAL_UINT(2, c.sliders_count);
  TEST_ASSERT_EQUAL_UINT(34, c.sliders[0].pin);
  TEST_ASSERT_EQUAL_UINT(35, c.sliders[1].pin);
  // Removing the field leaves a blob without it.
  const size_t m = replaceField(a, la, kUdpField, nullptr, 0, out, sizeof out);
  TEST_ASSERT_GREATER_THAN(0, m);
  const uint8_t* v;
  size_t vn;
  TEST_ASSERT_FALSE(findField(out, m, kUdpField, v, vn));
  (void)va;
}

void test_replace_field_rejects_malformed() {
  uint8_t a[512], out[512];
  const size_t la = encode("HomeNet", a, sizeof a);
  const uint8_t v[1] = {0};
  TEST_ASSERT_EQUAL_UINT(0, replaceField(a, la - 1, kUdpField, v, 1, out, sizeof out));
  const uint8_t trunc[] = {0x2A, 0x7F, 0x01};  // field 5, length 127, 1 byte
  TEST_ASSERT_EQUAL_UINT(0, replaceField(trunc, sizeof trunc, kUdpField, v, 1, out, sizeof out));
  const uint8_t group[] = {0x0B, 0x0C};  // field 1 start-group
  TEST_ASSERT_EQUAL_UINT(0, replaceField(group, sizeof group, kUdpField, v, 1, out, sizeof out));
  const uint8_t badVarint[] = {0x80, 0x80};
  TEST_ASSERT_EQUAL_UINT(0, replaceField(badVarint, sizeof badVarint, kUdpField, v, 1, out, sizeof out));
  TEST_ASSERT_EQUAL_UINT(0, replaceField(a, la, kUdpField, v, 1, out, 3));
  const uint8_t* fv;
  size_t fn;
  TEST_ASSERT_FALSE(findField(trunc, sizeof trunc, kUdpField, fv, fn));
}

void test_find_field_absent() {
  const uint8_t blob[] = {0x08, 0x01, 0x12, 0x01, 0x41};  // fields 1 and 2
  const uint8_t* v;
  size_t n;
  TEST_ASSERT_FALSE(findField(blob, sizeof blob, kUdpField, v, n));
  TEST_ASSERT_FALSE(findField(blob, 0, kUdpField, v, n));
}

void test_keeps_copy_only_when_udp_changes() {
  const uint8_t A[] = {1, 2, 3}, B[] = {1, 2, 4};
  TEST_ASSERT_TRUE(keepsWifiCopy(true, true, A, 3, B, 3));
  TEST_ASSERT_FALSE(keepsWifiCopy(true, true, A, 3, A, 3));
  TEST_ASSERT_FALSE(keepsWifiCopy(true, false, A, 3, B, 3));
  TEST_ASSERT_FALSE(keepsWifiCopy(false, true, A, 3, B, 3));
}

void test_fallback_confirms_on_udp_frame() {
  WifiFallback f;
  f.begin(true, 0, 30000);
  f.onValidFrame(0);
  TEST_ASSERT_EQUAL(WifiFallback::NONE, f.tick(5000));
  f.onValidFrame(1);
  TEST_ASSERT_EQUAL(WifiFallback::CONFIRM, f.tick(6000));
  TEST_ASSERT_EQUAL(WifiFallback::NONE, f.tick(6001));
  TEST_ASSERT_EQUAL(WifiFallback::NONE, f.tick(40000));
}

void test_fallback_restores_after_window() {
  WifiFallback f;
  f.begin(true, 0, 30000);
  TEST_ASSERT_EQUAL(WifiFallback::NONE, f.tick(29999));
  TEST_ASSERT_EQUAL(WifiFallback::RESTORE, f.tick(30000));
  TEST_ASSERT_EQUAL(WifiFallback::NONE, f.tick(30001));
}

void test_not_provisional_does_nothing() {
  WifiFallback f;
  f.begin(false, 0, 30000);
  f.onValidFrame(1);
  TEST_ASSERT_EQUAL(WifiFallback::NONE, f.tick(1));
  TEST_ASSERT_EQUAL(WifiFallback::NONE, f.tick(100000));
  TEST_ASSERT_FALSE(f.provisional());
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_replace_field_swaps_udp);
  RUN_TEST(test_replace_field_rejects_malformed);
  RUN_TEST(test_find_field_absent);
  RUN_TEST(test_keeps_copy_only_when_udp_changes);
  RUN_TEST(test_fallback_confirms_on_udp_frame);
  RUN_TEST(test_fallback_restores_after_window);
  RUN_TEST(test_not_provisional_does_nothing);
  return UNITY_END();
}
