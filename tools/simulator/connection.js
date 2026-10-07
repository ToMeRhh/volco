// The WebSocket to Volco and its pill and popover (spec §8.1). Browser only.
(function (root) {
  'use strict';

  /**
   * @param {object} o
   * @param {{host:string, port:string, reconnect:boolean}} o.settings  initial values (from prefs)
   * @param {() => void} o.onOpen
   * @param {(bytes:Uint8Array) => void} o.onBytes
   * @param {() => void} o.onClose            the socket closed (any reason)
   * @param {(settings:object) => void} o.onSettings  host/port/reconnect changed (to save them)
   */
  function create(o) {
    const $ = id => document.getElementById(id);
    let ws = null, retry = 0, userClosed = true, state = 'demo';
    $('host').value = o.settings.host;
    $('port').value = o.settings.port;
    $('reconnect').checked = o.settings.reconnect;
    const url = () => `ws://${$('host').value.trim() || 'localhost'}:${$('port').value.trim() || '8765'}/`;
    const where = () => `${$('host').value.trim() || 'localhost'}:${$('port').value.trim() || '8765'}`;

    function show(next) {
      state = next;
      $('conn-dot').className = 'dot' + (next === 'open' ? ' ok' : next === 'connecting' ? ' wait' : '');
      $('conn-text').textContent = next === 'open' ? where()
        : next === 'connecting' ? 'Connecting…'
        : next === 'unreachable' ? `Can't reach ${where()}` : 'Not connected';
      $('conn-demo').hidden = next === 'open';
      $('conn-go').textContent = next === 'open' || next === 'connecting' ? 'Disconnect' : 'Connect';
    }

    function connect() {
      clearTimeout(retry);
      userClosed = false;
      if (ws) return;
      show('connecting');
      const sock = new WebSocket(url());
      sock.binaryType = 'arraybuffer';
      ws = sock;
      let opened = false;
      sock.onopen = () => { opened = true; show('open'); o.onOpen(); };
      sock.onmessage = e => o.onBytes(new Uint8Array(e.data));
      sock.onclose = () => {
        if (ws !== sock) return;
        ws = null;
        show(opened || userClosed ? 'demo' : 'unreachable');
        o.onClose();
        if (!userClosed && $('reconnect').checked) retry = setTimeout(connect, 3000);
      };
    }
    /** Drops the socket now, without waiting for the close handshake. */
    function drop() {
      const sock = ws;
      ws = null;
      if (!sock) return;
      sock.onclose = null;
      sock.close();
      show('demo');
      o.onClose();
    }
    function disconnect() {
      userClosed = true;
      clearTimeout(retry);
      drop();
      show('demo');
    }
    /** Like the firmware after a save: drop the link and come back after 1 s, whatever the setting. */
    function reboot() {
      if (!ws && userClosed) return;   // not connected and not trying to be: stays in demo mode
      clearTimeout(retry);
      drop();
      userClosed = false;
      retry = setTimeout(connect, 1000);
    }

    const save = () => o.onSettings({ host: $('host').value.trim(), port: $('port').value.trim(), reconnect: $('reconnect').checked });
    for (const id of ['host', 'port', 'reconnect']) $(id).addEventListener('change', save);
    $('conn-btn').addEventListener('click', e => {
      e.stopPropagation();
      const pop = $('conn-pop');
      pop.hidden = !pop.hidden;
      $('conn-btn').setAttribute('aria-expanded', String(!pop.hidden));
    });
    document.addEventListener('pointerdown', e => {
      if (!$('conn-pop').hidden && !e.target.closest('.conn')) { $('conn-pop').hidden = true; $('conn-btn').setAttribute('aria-expanded', 'false'); }
    });
    $('conn-pop').addEventListener('submit', e => {
      e.preventDefault();
      save();
      if (state === 'open' || state === 'connecting') disconnect(); else connect();
    });
    show('demo');

    return {
      connect, disconnect, reboot,
      isOpen: () => !!ws && ws.readyState === WebSocket.OPEN,
      send(bytes) { if (ws && ws.readyState === WebSocket.OPEN) ws.send(bytes); },
      address: () => url(),
    };
  }

  root.VolcoConnection = { create };
})(globalThis);
