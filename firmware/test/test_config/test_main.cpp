#include <pb_decode.h>
#include <unity.h>

#include "volco_config.pb.h"

void setUp() {}
void tearDown() {}

void test_generated_defaults() {
  volco_config_ControllerConfig c = volco_config_ControllerConfig_init_default;
  TEST_ASSERT_EQUAL_UINT(255, c.screens.brightness);
  TEST_ASSERT_EQUAL_UINT(12345, c.udp.pc_port);
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_generated_defaults);
  return UNITY_END();
}
