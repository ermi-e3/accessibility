import { chromium } from '@playwright/test';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';

function argument(name) {
  const index = process.argv.indexOf(name);
  if (index < 0 || !process.argv[index + 1]) throw new Error(`Missing ${name}.`);
  return process.argv[index + 1];
}

const audioPath = resolve(argument('--audio'));
const outputPath = resolve(argument('--output'));
const lessonPath = resolve(argument('--lesson'));
const lesson = JSON.parse(await readFile(lessonPath, 'utf8'));
const audioBase64 = (await readFile(audioPath)).toString('base64');

await mkdir(dirname(outputPath), { recursive: true });
const browser = await chromium.launch({
  channel: 'chrome',
  headless: true,
  args: ['--autoplay-policy=no-user-gesture-required', '--disable-background-timer-throttling'],
});

try {
  const page = await browser.newPage();
  const recording = await page.evaluate(async ({ audioBase64, title, slides }) => {
    const fromBase64 = (value) => {
      const binary = atob(value);
      const bytes = new Uint8Array(binary.length);
      for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
      return bytes;
    };
    const toBase64 = (bytes) => {
      let binary = '';
      for (let index = 0; index < bytes.length; index += 0x8000) {
        binary += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
      }
      return btoa(binary);
    };
    const wrap = (context, text, width) => {
      const words = text.split(/\s+/);
      const lines = [];
      let line = '';
      for (const word of words) {
        const candidate = line ? `${line} ${word}` : word;
        if (context.measureText(candidate).width <= width || !line) line = candidate;
        else { lines.push(line); line = word; }
      }
      if (line) lines.push(line);
      return lines;
    };

    const audioBytes = fromBase64(audioBase64);
    const audioContext = new AudioContext();
    const audioBuffer = await audioContext.decodeAudioData(audioBytes.buffer.slice(0));
    await audioContext.resume();

    const canvas = document.createElement('canvas');
    canvas.width = 960;
    canvas.height = 540;
    const context = canvas.getContext('2d');
    const videoStream = canvas.captureStream(12);
    const audioDestination = audioContext.createMediaStreamDestination();
    const source = audioContext.createBufferSource();
    source.buffer = audioBuffer;
    source.connect(audioDestination);
    const stream = new MediaStream([
      ...videoStream.getVideoTracks(),
      ...audioDestination.stream.getAudioTracks(),
    ]);
    const mimeType = [
      'video/webm;codecs=vp9,opus',
      'video/webm;codecs=vp8,opus',
      'video/webm',
    ].find((candidate) => MediaRecorder.isTypeSupported(candidate));
    if (!mimeType) throw new Error('No supported WebM recorder is available.');

    const chunks = [];
    const recorder = new MediaRecorder(stream, { mimeType, videoBitsPerSecond: 900_000 });
    recorder.addEventListener('dataavailable', (event) => {
      if (event.data.size > 0) chunks.push(event.data);
    });
    const stopped = new Promise((resolveStopped) => recorder.addEventListener('stop', resolveStopped, { once: true }));
    const ended = new Promise((resolveEnded) => { source.onended = resolveEnded; });
    let animationFrame = 0;
    let startedAt = 0;

    const draw = (elapsedSeconds) => {
      const progress = Math.min(1, elapsedSeconds / audioBuffer.duration);
      const slideIndex = Math.min(slides.length - 1, Math.floor(progress * slides.length));
      const slide = slides[slideIndex];
      const gradient = context.createLinearGradient(0, 0, canvas.width, canvas.height);
      gradient.addColorStop(0, '#102f3b');
      gradient.addColorStop(1, '#075041');
      context.fillStyle = gradient;
      context.fillRect(0, 0, canvas.width, canvas.height);
      context.fillStyle = '#f4c95d';
      context.fillRect(0, 0, 28, canvas.height);
      context.fillStyle = 'rgba(255,255,255,0.08)';
      context.beginPath();
      context.arc(870, 80, 150, 0, Math.PI * 2);
      context.fill();

      context.fillStyle = '#f4c95d';
      context.font = '700 24px Arial, sans-serif';
      context.fillText('ACCESSLEARN ETHIOPIA', 78, 84);
      context.fillStyle = '#ffffff';
      context.font = '700 25px Arial, sans-serif';
      for (const [index, line] of wrap(context, title, 760).entries()) context.fillText(line, 78, 135 + index * 34);
      context.fillStyle = '#f4c95d';
      context.font = '700 38px Arial, sans-serif';
      context.fillText(slide.heading, 78, 268);
      context.fillStyle = '#ffffff';
      context.font = '400 31px Arial, sans-serif';
      for (const [index, line] of wrap(context, slide.text, 790).entries()) context.fillText(line, 78, 320 + index * 43);

      context.fillStyle = 'rgba(255,255,255,0.25)';
      context.fillRect(78, 486, 800, 8);
      context.fillStyle = '#f4c95d';
      context.fillRect(78, 486, 800 * progress, 8);
      context.fillStyle = '#ffffff';
      context.font = '600 18px Arial, sans-serif';
      context.fillText('Captions and transcript available', 78, 522);
    };
    const animate = (now) => {
      draw((now - startedAt) / 1000);
      animationFrame = requestAnimationFrame(animate);
    };

    draw(0);
    recorder.start(250);
    startedAt = performance.now();
    source.start();
    animationFrame = requestAnimationFrame(animate);
    await ended;
    cancelAnimationFrame(animationFrame);
    draw(audioBuffer.duration);
    await new Promise((resolveDelay) => setTimeout(resolveDelay, 300));
    recorder.stop();
    await stopped;

    const blob = new Blob(chunks, { type: mimeType });
    const bytes = new Uint8Array(await blob.arrayBuffer());
    for (const track of stream.getTracks()) track.stop();
    await audioContext.close();
    return { base64: toBase64(bytes), durationMilliseconds: Math.round(audioBuffer.duration * 1000), mimeType };
  }, {
    audioBase64,
    title: lesson.title,
    slides: lesson.narration,
  });

  await writeFile(outputPath, Buffer.from(recording.base64, 'base64'));
  console.log(`Generated ${outputPath} (${recording.durationMilliseconds} ms, ${recording.mimeType}).`);
} finally {
  await browser.close();
}
