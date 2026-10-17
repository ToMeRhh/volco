#pragma once
// Receives a config write in chunks (spec §5.4, receiver rules 1-7). Pure: frames are decoded by
// the caller, time is passed in. At most one result per Begin: a REPLY, or a COMPLETE after which
// the caller decodes, validates, stores and sends exactly one ConfigResult itself.
#include <stddef.h>
#include <stdint.h>

#include "volco_config.h"

namespace volco {

constexpr uint32_t kTransferTimeoutMs = 2000;
constexpr size_t kMaxChunk = 240;  // the ESP32's chunk size

struct ReceiverEvent {
  enum Kind : uint8_t { NONE, REPLY, COMPLETE };
  Kind kind;
  uint8_t transport, seq, code;  // REPLY: send ConfigResult(seq, code) on transport
};

class BlobReceiver {
 public:
  // buf: where a write lands, `capacity` bytes (the chip's maximum blob); maxChunk: the largest
  // chunk accepted (the chip's chunk size).
  BlobReceiver(uint8_t* buf, uint16_t capacity, uint8_t maxChunk)
      : buf_(buf), capacity_(capacity), maxChunk_(maxChunk) {}
  ReceiverEvent onBegin(uint8_t transport, uint8_t seq, uint16_t total, uint32_t crc, uint32_t now);
  ReceiverEvent onChunk(uint8_t transport, uint8_t seq, uint16_t offset, const uint8_t* data, size_t len, uint32_t now);
  ReceiverEvent onEnd(uint8_t transport, uint8_t seq, uint32_t now);
  void tick(uint32_t now);
  bool isOpen() const { return open_; }
  const uint8_t* data() const { return buf_; }
  uint16_t size() const { return total_; }

 private:
  ReceiverEvent reply(uint8_t code);
  uint8_t* buf_;
  uint16_t capacity_;
  uint8_t maxChunk_;
  bool open_ = false;
  uint8_t transport_ = 0, seq_ = 0;
  uint16_t total_ = 0, received_ = 0;
  uint32_t crc_ = 0, last_ = 0;
};

}  // namespace volco
