// UX Review Agent: 捕获9个关键状态截图(手机8+桌面1),供启发式评审
import { chromium } from '/home/leo/.local/share/fnm/node-versions/v22.22.2/installation/lib/node_modules/playwright/index.mjs';
import fs from 'node:fs';
const OUT = '/tmp/playtest';
fs.mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch({ executablePath: '/home/leo/.cache/ms-playwright/chromium-1234/chrome-linux64/chrome', headless: true, args: ['--no-sandbox'] });
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const snap = (page) => page.evaluate(async () => await (await fetch('/api/snapshot')).json());

async function state(name, ctxOpts, fn) {
  const ctx = await browser.newContext({ viewport: { width: 412, height: 915 }, isMobile: true, hasTouch: true, locale: 'zh-CN', deviceScaleFactor: 1.5, ...ctxOpts });
  const page = await ctx.newPage();
  await page.goto('http://localhost:5023/', { waitUntil: 'domcontentloaded' });
  await sleep(2000);
  try { await fn(page); } catch (e) { console.log(`[${name}] setup err: ${e.message.slice(0, 60)}`); }
  await sleep(600);
  await page.screenshot({ path: `${OUT}/ux-${name}.png` });
  // DOM骨架(可见元素树,供结构分析)
  const outline = await page.evaluate(() => {
    const walk = (el, d) => {
      if (d > 4) return null;
      const kids = [...el.children].map(c => walk(c, d + 1)).filter(Boolean);
      const r = el.getBoundingClientRect(); const cs = getComputedStyle(el);
      if (cs.display === 'none' || r.width === 0) return null;
      const label = (el.children.length === 0 && el.textContent?.trim()) ? el.textContent.trim().slice(0, 30) : '';
      return { tag: el.tagName.toLowerCase(), id: el.id || undefined, label: label || undefined, kids: kids.length ? kids : undefined };
    };
    return walk(document.body, 0);
  });
  fs.writeFileSync(`${OUT}/ux-${name}-outline.json`, JSON.stringify(outline));
  await ctx.close();
  console.log(`[${name}] captured`);
}

// 1 冷启动选关
await state('01-levelselect', {}, async (p) => { /* Waiting态默认 */ });
// 2 组牌界面
await state('02-deckbuilder', {}, async (p) => {
  await p.evaluate(async () => { await fetch('/api/loadlevel?id=accumulate', { method: 'POST' }); });
  await sleep(1200);
});
// 3 盘前研究(日K)
await state('03-premarket', {}, async (p) => {
  await p.evaluate(async () => { await fetch('/api/loadlevel?id=accumulate', { method: 'POST' }); await fetch('/api/begin', { method: 'POST' }); });
  await sleep(1000); await p.reload(); await sleep(2000);
});
// 4 盘前申报(含新引导条)
await state('04-declare', {}, async (p) => {
  await p.evaluate(async () => { await fetch('/api/loadlevel?id=accumulate', { method: 'POST' }); await fetch('/api/card/deck', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(['市价急拉', '限价吸筹', '激进架势']) }); await fetch('/api/begin', { method: 'POST' }); });
  await sleep(1200);
});
// 5 盘中出牌(已出1张,看预览与反馈)
await state('05-play', {}, async (p) => {
  await p.evaluate(async () => { await fetch('/api/loadlevel?id=accumulate', { method: 'POST' }); await fetch('/api/begin', { method: 'POST' }); });
  await sleep(800);
  // 跳申报段
  await p.evaluate(async () => { await fetch('/api/card/endturn', { method: 'POST' }); });
  await sleep(9000);
  const card = p.locator('#cardHand .card-item:not(.disabled)').first();
  await card.tap({ timeout: 5000 }).catch(() => {});
  await sleep(900);
});
// 6 揭牌后
await state('06-reveal', {}, async (p) => {
  await p.evaluate(async () => { await fetch('/api/loadlevel?id=accumulate', { method: 'POST' }); await fetch('/api/begin', { method: 'POST' }); await fetch('/api/card/endturn', { method: 'POST' }); });
  await sleep(9000);
  const card = p.locator('#cardHand .card-item:not(.disabled)').first();
  await card.tap({ timeout: 5000 }).catch(() => {});
  await sleep(700);
  await p.locator('#endTurnBtn').tap().catch(() => {});
  await sleep(1500);
});
// 7 日切盘后
await state('07-dayclose', {}, async (p) => {
  await p.evaluate(async () => {
    await fetch('/api/loadlevel?id=accumulate', { method: 'POST' }); await fetch('/api/begin', { method: 'POST' });
    for (let i = 0; i < 14; i++) { await fetch('/api/card/endturn', { method: 'POST' }); await new Promise(r => setTimeout(r, 500)); }
  });
  await sleep(4000);
});
// 8 结算
await state('08-settle', {}, async (p) => {
  await p.evaluate(async () => { await fetch('/api/loadlevel?id=accumulate', { method: 'POST' }); await fetch('/api/begin', { method: 'POST' }); await fetch('/api/endlevel', { method: 'POST' }); });
  await sleep(1500);
});
// 9 桌面全景
await state('09-desktop', { viewport: { width: 1600, height: 950 }, isMobile: false, hasTouch: false, deviceScaleFactor: 1 }, async (p) => {
  await p.evaluate(async () => { await fetch('/api/loadlevel?id=accumulate', { method: 'POST' }); await fetch('/api/begin', { method: 'POST' }); await fetch('/api/card/endturn', { method: 'POST' }); });
  await sleep(9000);
});
await browser.close();
console.log('UX captures DONE');
