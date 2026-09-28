// Capture de video.html image par image avec le Chrome installé.
//   node capture.mjs stills 1.5 3.2 ...   -> stills/<t>.png
//   node capture.mjs frames               -> frames/00000.jpg ... (30 i/s) + timeline.json
import { chromium } from 'playwright-core';
import fs from 'node:fs';
import path from 'node:path';

const FPS = 30;
const [mode = 'frames', ...times] = process.argv.slice(2);
const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
const page = await browser.newPage({ viewport: { width: 1080, height: 1920 }, deviceScaleFactor: 1 });
await page.goto('file:///' + path.resolve('video.html').replace(/\\/g, '/'));
await page.evaluate(() => document.fonts.ready);
const fonts = await page.evaluate(() => ['Segoe UI Variable Display', 'Cascadia Mono'].map(f => `${f}: ${document.fonts.check(`40px "${f}"`)}`));
console.log(fonts.join(' | '));

const timeline = await page.evaluate(() => window.TIMELINE);
fs.writeFileSync('timeline.json', JSON.stringify(timeline, null, 1));

async function shot(t, file, type) {
  await page.evaluate(t => window.render(t), t);
  await page.screenshot({ path: file, type, ...(type === 'jpeg' ? { quality: 95 } : {}) });
}

if (mode === 'stills') {
  fs.mkdirSync('stills', { recursive: true });
  for (const t of times) await shot(Number(t), `stills/${t}.png`, 'png');
} else {
  fs.rmSync('frames', { recursive: true, force: true });
  fs.mkdirSync('frames');
  const n = Math.round(timeline.duration * FPS);
  for (let i = 0; i < n; i++) {
    await shot(i / FPS, `frames/${String(i).padStart(5, '0')}.jpg`, 'jpeg');
    if (i % 60 === 0) console.log(`image ${i}/${n}`);
  }
  console.log(`${n} images`);
}
await browser.close();
