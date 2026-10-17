#include "config_session.h"

#include <Arduino.h>

#include "debug_log.h"
#include "transport.h"
#include "volco_blob_receiver.h"
#include "volco_chip.h"
#include "volco_config.h"
#include "volco_crc32.h"
#include "volco_read_reply.h"

namespace {

uint8_t receiveBuf[volco::kMaxConfigBlob];
volco::BlobReceiver receiver(receiveBuf, volco::kMaxConfigBlob, volco::kMaxChunk);
volco_config_ControllerConfig candidate;  // static: kept off the loop task's stack
uint8_t replyBuf[volco::kMaxReadReply];

void sendResult(uint8_t transport, uint8_t seq, uint8_t code, uint8_t item, uint8_t field) {
  uint8_t f[16];
  transport_send(transport, f, volco::encodeConfigResult(seq, code, item, field, f));
}

// The transfer is complete and its CRC matched: decode, validate, store, answer once (spec §5.4
// rule 5). After OK nothing else runs: flush, wait for the reply to leave, reboot (spec §6.7).
void finishWrite(uint8_t transport, uint8_t seq) {
  if (!volco::decodeConfig(receiver.data(), receiver.size(), candidate)) {
    sendResult(transport, seq, volco::CFG_DECODE_FAILED, volco::kNoItem, volco::kNoItem);
    return;
  }
  const volco::ValidationResult v = volco::validateConfig(candidate, volco::esp32Chip());
  if (v.code != volco::CFG_OK) {
    sendResult(transport, seq, v.code, v.item, v.field);
    return;
  }
  if (!config_store_save(receiver.data(), receiver.size())) {
    sendResult(transport, seq, volco::CFG_STORAGE_FAILED, volco::kNoItem, volco::kNoItem);
    return;
  }
  sendResult(transport, seq, volco::CFG_OK, volco::kNoItem, volco::kNoItem);
  DEBUG_LOG("[DBG] Config: saved, restarting");
  Serial.flush();
  delay(200);
  ESP.restart();
}

void handle(const volco::ReceiverEvent& e) {
  if (e.kind == volco::ReceiverEvent::REPLY) sendResult(e.transport, e.seq, e.code, volco::kNoItem, volco::kNoItem);
  else if (e.kind == volco::ReceiverEvent::COMPLETE) finishWrite(e.transport, e.seq);
}

class Session : public volco::ConfigSink {
 public:
  void configRead(uint8_t transport, uint8_t seq, uint32_t) override {
    uint16_t len;
    const uint8_t* blob = config_store_blob(len);
    const uint32_t crc = len ? volco::crc32(blob, len) : 0;
    const size_t n = volco::buildReadReply(seq, blob, len, crc, replyBuf, sizeof replyBuf);
    if (n) transport_send(transport, replyBuf, n);  // one send: one serial write, one UDP datagram
  }
  void configBegin(uint8_t transport, uint8_t seq, uint16_t total, uint32_t crc, uint32_t now) override {
    handle(receiver.onBegin(transport, seq, total, crc, now));
  }
  void configChunk(uint8_t transport, uint8_t seq, uint16_t offset, const uint8_t* data, size_t len,
                   uint32_t now) override {
    handle(receiver.onChunk(transport, seq, offset, data, len, now));
  }
  void configEnd(uint8_t transport, uint8_t seq, uint32_t now) override {
    handle(receiver.onEnd(transport, seq, now));
  }
};

Session session;

}  // namespace

void config_session_begin(volco::Link& link) { link.setConfigSink(&session); }

void config_session_tick(uint32_t now) { receiver.tick(now); }
