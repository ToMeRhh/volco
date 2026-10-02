#include <unity.h>
#include <string.h>
#include "volco_bidi.h"

using volco::bidiVisual;

static void expectVisual(const char* logical, const char* visual, bool rtl) {
  char out[128];
  bool isRtl = !rtl;
  size_t n = bidiVisual(logical, strlen(logical), out, sizeof out, &isRtl);
  TEST_ASSERT_EQUAL(strlen(visual), n);
  TEST_ASSERT_EQUAL_STRING(visual, out);
  TEST_ASSERT_EQUAL(rtl, isRtl);
}

void setUp() {}
void tearDown() {}

void test_latin_only_is_unchanged() { expectVisual("Speakers", "Speakers", false); }
void test_empty() { expectVisual("", "", false); }
void test_no_strong_characters_is_ltr() { expectVisual("123 - 4", "123 - 4", false); }
void test_hebrew_only_is_reversed() { expectVisual("שלום", "םולש", true); }

void test_hebrew_with_latin_in_brackets() {
  // רמקולים (USB Audio) → visual "(USB Audio) םילוקמר"
  expectVisual("רמקולים (USB Audio)", "(USB Audio) םילוקמר", true);
}

void test_latin_paragraph_with_hebrew_word() { expectVisual("Mic מיקרופון", "Mic ןופורקימ", false); }
void test_time_inside_hebrew_keeps_its_order() { expectVisual("שעה 10:30", "10:30 העש", true); }
void test_fraction_at_the_start_of_hebrew() { expectVisual("1/2 כוס", "סוכ 1/2", true); }
void test_brackets_around_hebrew_are_mirrored() { expectVisual("(שלום)", "(םולש)", true); }

void test_hebrew_with_time_and_brackets() {
  // Review Focus #4: פגישה (10:30)
  expectVisual("פגישה (10:30)", "(10:30) השיגפ", true);
}

void test_latin_number_after_word() { expectVisual("Volume 10:30", "Volume 10:30", false); }

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_latin_only_is_unchanged);
  RUN_TEST(test_empty);
  RUN_TEST(test_no_strong_characters_is_ltr);
  RUN_TEST(test_hebrew_only_is_reversed);
  RUN_TEST(test_hebrew_with_latin_in_brackets);
  RUN_TEST(test_latin_paragraph_with_hebrew_word);
  RUN_TEST(test_time_inside_hebrew_keeps_its_order);
  RUN_TEST(test_fraction_at_the_start_of_hebrew);
  RUN_TEST(test_brackets_around_hebrew_are_mirrored);
  RUN_TEST(test_hebrew_with_time_and_brackets);
  RUN_TEST(test_latin_number_after_word);
  return UNITY_END();
}
