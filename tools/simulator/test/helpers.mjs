// Shared helpers for the simulator's Node tests: the golden files and a fake clock.
import path from 'node:path';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
export const repo = path.join(here, '..', '..', '..');
const dir = path.join(repo, 'firmware', 'test', 'golden');
const lines = file => fs.readFileSync(path.join(dir, file), 'utf8').split('\n').filter(l => l.trim() && !l.startsWith('#'));

export const golden = {
  /** frames.txt: name → whole frame bytes. */
  frames: () => Object.fromEntries(lines('frames.txt').map(l => {
    const [name, ...hx] = l.trim().split(/\s+/);
    return [name, hx.map(h => parseInt(h, 16))];
  })),
  /** render.txt: [{ name, payload (Screen payload bytes), since, canvas (1024 bytes, page order) }]. */
  renders: () => lines('render.txt').map(l => {
    const [name, payload, since, canvas] = l.trim().split(/\s+/);
    const bytes = s => s.match(/../g).map(h => parseInt(h, 16));
    return { name, payload: bytes(payload), since: +since, canvas: canvas === '-' ? null : bytes(canvas) };
  }),
};

/** A manual clock: now() and advance(ms). */
export function clock(start = 1000) {
  let t = start;
  return { now: () => t, advance: ms => (t += ms) };
}
