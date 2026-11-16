import { chromium } from '@playwright/test';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';

function argument(name) {
  const index = process.argv.indexOf(name);
  if (index < 0 || !process.argv[index + 1]) throw new Error(`Missing ${name}.`);
  return process.argv[index + 1];
}

const inputPath = resolve(argument('--input'));
const outputPath = resolve(argument('--output'));
const inputBase64 = (await readFile(inputPath)).toString('base64');
await mkdir(dirname(outputPath), { recursive: true });

const browser = await chromium.launch({ channel: 'chrome', headless: true });
try {
  const page = await browser.newPage();
  const wavBase64 = await page.evaluate(async (inputBase64) => {
    const binary = atob(inputBase64);
    const encoded = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) encoded[index] = binary.charCodeAt(index);
    const context = new AudioContext();
    const decoded = await context.decodeAudioData(encoded.buffer.slice(0));
    await context.close();
    const sampleRate = 16_000;
    const frameCount = Math.ceil(decoded.duration * sampleRate);
    const offline = new OfflineAudioContext(1, frameCount, sampleRate);
    const source = offline.createBufferSource();
    source.buffer = decoded;
    source.connect(offline.destination);
    source.start();
    const rendered = await offline.startRendering();
    const samples = rendered.getChannelData(0);
    const buffer = new ArrayBuffer(44 + samples.length * 2);
    const view = new DataView(buffer);
    const writeText = (offset, value) => {
      for (let index = 0; index < value.length; index += 1) view.setUint8(offset + index, value.charCodeAt(index));
    };
    writeText(0, 'RIFF');
    view.setUint32(4, 36 + samples.length * 2, true);
    writeText(8, 'WAVE');
    writeText(12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);
    view.setUint16(22, 1, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, sampleRate * 2, true);
    view.setUint16(32, 2, true);
    view.setUint16(34, 16, true);
    writeText(36, 'data');
    view.setUint32(40, samples.length * 2, true);
    for (let index = 0; index < samples.length; index += 1) {
      const sample = Math.max(-1, Math.min(1, samples[index]));
      view.setInt16(44 + index * 2, sample < 0 ? sample * 0x8000 : sample * 0x7fff, true);
    }
    const bytes = new Uint8Array(buffer);
    let output = '';
    for (let index = 0; index < bytes.length; index += 0x8000) {
      output += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
    }
    return btoa(output);
  }, inputBase64);
  await writeFile(outputPath, Buffer.from(wavBase64, 'base64'));
  console.log(`Extracted mono WAV audio to ${outputPath}.`);
} finally {
  await browser.close();
}
