import { describe, expect, it, vi } from 'vitest';
import { encodeWav, resample, UtteranceDetector } from './microphone';

const RATE = 48_000;
const block = (amplitude: number) => Float32Array.from({ length: 128 }, (_, i) => amplitude * Math.sin(i / 3));
const feed = (detector: UtteranceDetector, amplitude: number, ms: number) => {
  for (let i = 0; i < Math.round((RATE * ms) / 1000 / 128); i++) detector.push(block(amplitude));
};

describe('encodeWav', () => {
  it('writes a 16-bit mono PCM header for the given rate', async () => {
    const wav = encodeWav(new Float32Array([0, 0.5, -1]), 16_000);
    const view = new DataView(await wav.arrayBuffer());
    expect(wav.type).toBe('audio/wav');
    expect(wav.size).toBe(44 + 6);
    expect(view.getUint32(24, true)).toBe(16_000);
    expect(view.getUint16(34, true)).toBe(16);
    expect(view.getInt16(46, true)).toBe(Math.round(0.5 * 0x7fff - 0.5));
    expect(view.getInt16(48, true)).toBe(-0x7fff);
  });
});

describe('resample', () => {
  it('turns 48 kHz into 16 kHz by thirds', () => {
    expect(resample(new Float32Array(48_000), 48_000, 16_000)).toHaveLength(16_000);
  });
});

describe('UtteranceDetector', () => {
  it('cuts speech followed by silence into one utterance', () => {
    const onUtterance = vi.fn();
    const onSpeaking = vi.fn();
    const detector = new UtteranceDetector(RATE, { onUtterance, onSpeaking });

    feed(detector, 0.001, 500);
    feed(detector, 0.3, 1200);
    feed(detector, 0.001, 1000);

    expect(onUtterance).toHaveBeenCalledOnce();
    expect(onSpeaking.mock.calls).toEqual([[true], [false]]);
    const wav = onUtterance.mock.calls[0]![0] as Blob;
    expect(wav.size).toBeGreaterThan(44 + 16_000 * 2 * 1.2);
  });

  it('ignores a click shorter than a word', () => {
    const onUtterance = vi.fn();
    const detector = new UtteranceDetector(RATE, { onUtterance });

    feed(detector, 0.001, 500);
    feed(detector, 0.3, 90);
    feed(detector, 0.001, 1000);

    expect(onUtterance).not.toHaveBeenCalled();
  });
});
