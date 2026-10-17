#include "volco_blob_receiver.h"

#include <string.h>

#include "volco_crc32.h"

namespace volco {

ReceiverEvent BlobReceiver::reply(uint8_t code) {
  open_ = false;
  return {ReceiverEvent::REPLY, transport_, seq_, code};
}

ReceiverEvent BlobReceiver::onBegin(uint8_t transport, uint8_t seq, uint16_t total, uint32_t crc, uint32_t now) {
  open_ = false;  // rule 1: a Begin always discards an open transfer
  transport_ = transport;
  seq_ = seq;
  if (total == 0 || total > capacity_) return reply(CFG_TOO_LARGE);
  open_ = true;
  total_ = total;
  received_ = 0;
  crc_ = crc;
  last_ = now;
  return {ReceiverEvent::NONE, transport, seq, CFG_OK};
}

ReceiverEvent BlobReceiver::onChunk(uint8_t transport, uint8_t seq, uint16_t offset, const uint8_t* data, size_t len,
                                    uint32_t now) {
  tick(now);  // rule 6 holds even if the caller hasn't ticked since a long pause
  if (!open_ || transport != transport_) return {ReceiverEvent::NONE, transport, seq, CFG_OK};  // rules 2-3
  if (seq != seq_ || offset != received_ || len == 0 || len > maxChunk_ || received_ + len > total_)
    return reply(CFG_BAD_SEQUENCE);  // rule 4
  memcpy(buf_ + received_, data, len);
  received_ = (uint16_t)(received_ + len);
  last_ = now;
  return {ReceiverEvent::NONE, transport, seq, CFG_OK};
}

ReceiverEvent BlobReceiver::onEnd(uint8_t transport, uint8_t seq, uint32_t now) {
  tick(now);
  if (!open_ || transport != transport_) return {ReceiverEvent::NONE, transport, seq, CFG_OK};
  if (seq != seq_ || received_ != total_) return reply(CFG_BAD_SEQUENCE);
  if (crc32(buf_, total_) != crc_) return reply(CFG_BAD_CRC);
  open_ = false;  // rule 5: the caller decodes, validates, stores and sends the one result
  return {ReceiverEvent::COMPLETE, transport_, seq_, CFG_OK};
}

void BlobReceiver::tick(uint32_t now) {
  if (open_ && now - last_ >= kTransferTimeoutMs) open_ = false;  // rule 6: silent
}

}  // namespace volco
