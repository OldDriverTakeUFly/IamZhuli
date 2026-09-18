// Agent 图形化测试 harness:三层评测
// A.逻辑点击:冷启动盲操作完整局(记录点击路径/死点/错误)
// B.布局点击:全量可点元素触达量测(桌面+手机,44px触控标准)
// C.视觉信息:关键节点截图+状态真值JSON(供视觉问卷比对)
import { chromium } from '/home/leo/.local/share/fnm/node-versions/v22.22.2/installation/lib/node_modules/playwright/index.mjs';
import fs from 'node:fs';

const BASE = 'http://localhost:5023';
const OUT = '/tmp/playtest';
fs.mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch({
  executablePath: '/home/leo/.cache/ms-playwright/chromium-1234/chrome-linux64/chrome',
  headless: true, args: ['--no-sandbox']
});
const report = { logic: [], layout: { mobile: [], desktop: [] }, vision: [], errors: [] };
const log = (msg) => console.log('[agent]', msg);

// ── 工具:交互元素清单 ──
async function interactiveInventory(page) {
  return page.evaluate(() => {
    const sels = 'button, .card-item, .mini-btn, .seg button, [onclick], input, .o-cancel, .level-card, .deck-btn';
    const seen = new Set();
    const out = [];
    for (const el of document.querySelectorAll(sels)) {
      if (seen.has(el)) continue; seen.add(el);
      const r = el.getBoundingClientRect();
      const cs = getComputedStyle(el);
      if (cs.display === 'none' || cs.visibility === 'hidden' || r.width === 0 || r.height === 0) continue;
      out.push({
        tag: el.tagName.toLowerCase(), id: el.id || null,
        text: (el.innerText || el.value || '').trim().slice(0, 24).replace(/\n/g, ' '),
        cls: (el.className || '').toString().slice(0, 40),
        x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height),
        disabled: el.disabled === true || el.classList.contains('disabled'),
      });
    }
    return out;
  });
}

async function snap(page) {
  return page.evaluate(async () => await (await fetch('/api/snapshot')).json());
}
async function settle(ms = 600) { await new Promise(r => setTimeout(r, ms)); }

// ═══ A. 逻辑点击:Agent 盲玩完整一局 ═══
async function logicPlay() {
  const ctx = await browser.newContext({ viewport: { width: 412, height: 915 }, isMobile: true, hasTouch: true, locale: 'zh-CN' });
  const page = await ctx.newPage();
  const journey = [];
  const click = async (desc, fn) => {
    const before = await snap(page);
    try { await fn(); await settle(); }
    catch (e) { report.errors.push(`${desc}: ${e.message.slice(0, 80)}`); }
    const after = await snap(page);
    journey.push({ step: desc, phase: after.phase, tick: after.tickOfDay, changed: JSON.stringify(before) !== JSON.stringify(after) });
    return after;
  };

  await page.goto(BASE, { waitUntil: 'domcontentloaded' });
  await settle(2500);
  journey.push({ step: '冷启动', phase: 'Waiting', inventory: (await interactiveInventory(page)).length });

  // A1 冷启动: 从选关模态开始(只凭可见元素决策)
  const levels = page.locator('#levelSelectModal button, #levelSelectModal .level-card');
  const nLevels = await levels.count();
  journey.push({ step: '发现关卡选项', count: nLevels });
  if (nLevels > 0) await click('点选第2个关卡', () => levels.nth(1).tap());

  // A2 组牌/直接开始
  await settle(1200);
  let inv = await interactiveInventory(page);
  const begin = inv.find(i => /开始|操盘|开/.test(i.text));
  if (begin) await click('开始操盘', () => page.locator(`#${begin.id}`).first().tap().catch(async () => page.locator('text=开始操盘').first().tap()));

  // A3 核心循环:6段(含申报/锁单/盘中),每段尝试出牌+结束回合
  let s = await snap(page);
  for (let seg = 0; seg < 6 && s.phase !== 'Closed'; seg++) {
    // 等出牌阶段
    for (let w = 0; w < 40 && (s.cardState || {}).phase !== 'Play' && s.phase !== 'Closed' && !s.isPaused; w++) { await settle(500); s = await snap(page); }
    if (s.phase === 'Closed' || s.isPaused) break;
    const cards = page.locator('#cardHand .card-item:not(.disabled)');   // 限定手牌(组牌库模态同class)
    const nCard = await cards.count();
    if (nCard > 0) s = await click(`段${s.cardState.turn}[${s.cardState.phaseName}]出第1张牌`, () => cards.first().tap());
    const endBtn = page.locator('#endTurnBtn');
    if (await endBtn.isVisible().catch(() => false))
      s = await click(`段${s.cardState.turn}结束回合`, () => endBtn.tap());
    // 推进Resolve
    for (let w = 0; w < 40; w++) { await settle(450); s = await snap(page); if ((s.cardState || {}).phase === 'Play' || s.phase === 'Closed' || s.isPaused) break; }
  }
  // A4 日切处理:点下一日(若有)
  inv = await interactiveInventory(page);
  const nextDay = inv.find(i => /下一日|下一天/.test(i.text));
  if (nextDay) await click('点下一日', () => page.locator(`#${nextDay.id}`).tap());

  const final = await snap(page);
  report.logic = journey;
  await page.screenshot({ path: `${OUT}/logic-final.png` });
  fs.writeFileSync(`${OUT}/logic-groundtruth.json`, JSON.stringify(final, null, 1));
  log(`逻辑点击完成:${journey.length}步, 死点(点击无变化)${journey.filter(j => j.changed === false).length}个`);
  await ctx.close();
}

// ═══ B. 布局点击评测 ═══
async function layoutAudit(width, height, isMobile, label) {
  const ctx = await browser.newContext({ viewport: { width, height }, isMobile, hasTouch: isMobile, locale: 'zh-CN' });
  const page = await ctx.newPage();
  await page.goto(BASE, { waitUntil: 'domcontentloaded' });
  await settle(2000);
  // 进局(直接API准备,布局审计关注游戏态)
  await page.evaluate(async () => {
    await fetch('/api/loadlevel?id=accumulate', { method: 'POST' });
    await fetch('/api/begin', { method: 'POST' });
  });
  await settle(1500);
  const inv = await interactiveInventory(page);
  const TH = isMobile ? 44 : 24;   // 触控44px(Apple HIG)/桌面24px
  const issues = inv.filter(i => i.h < TH || i.w < TH).map(i => ({ ...i, issue: `尺寸${i.w}×${i.h}<${TH}` }));
  const offscreen = inv.filter(i => i.y + i.h > height || i.x + i.w > width || i.y < 0 || i.x < 0).map(i => ({ ...i, issue: '视口外' }));
  report.layout[label] = { total: inv.length, belowThreshold: issues.length, issues, offscreen };
  log(`布局[${label}]: ${inv.length}个可点元素, ${issues.length}个低于${TH}px阈值`);
  await page.screenshot({ path: `${OUT}/layout-${label}.png`, fullPage: isMobile });
  await ctx.close();
}

// ═══ C. 视觉信息截图+真值 ═══
async function visionCapture() {
  const ctx = await browser.newContext({ viewport: { width: 412, height: 915 }, isMobile: true, hasTouch: true, locale: 'zh-CN', deviceScaleFactor: 2 });
  const page = await ctx.newPage();
  await page.goto(BASE, { waitUntil: 'domcontentloaded' });
  await settle(2000);
  await page.evaluate(async () => {
    await fetch('/api/loadlevel?id=accumulate', { method: 'POST' });
    await fetch('/api/begin', { method: 'POST' });
  });
  await settle(1500);
  const moments = {};
  // C1 盘前申报
  let s = await snap(page);
  moments['01-盘前申报'] = s; await page.screenshot({ path: `${OUT}/vision-01.png` });
  // C2 出牌中(跳到盘中)
  for (let w = 0; w < 50; w++) { await settle(400); s = await snap(page); if ((s.cardState || {}).turn === 2 && s.cardState.phase === 'Play') break; }
  moments['02-盘中出牌'] = s; await page.screenshot({ path: `${OUT}/vision-02.png` });
  // C3 出过一张牌后
  const card = page.locator('#cardHand .card-item:not(.disabled)').first();
  if (await card.count()) { await card.tap(); await settle(800); }
  s = await snap(page);
  moments['03-已出牌'] = s; await page.screenshot({ path: `${OUT}/vision-03.png` });
  // C4 揭牌后
  await page.locator('#endTurnBtn').tap().catch(() => {});
  await settle(1500);
  s = await snap(page);
  moments['04-揭牌后'] = s; await page.screenshot({ path: `${OUT}/vision-04.png` });
  // 真值(仅问卷相关字段)
  const truth = {};
  for (const [k, v] of Object.entries(moments)) {
    truth[k] = {
      price: v.lastPrice, phase: v.cardState?.phaseName, turn: v.cardState?.turn,
      energy: v.cardState?.energy, hand: (v.cardState?.hand || []).map(h => h.name),
      heat: v.regulatorHeat, penalty: v.penaltyLevel, cash: Math.round(v.account.cash / 10000),
      pos: v.account.positionTotal, sentiment: v.sentiment,
      auction: v.cardState?.lastAuction && { price: v.cardState.lastAuction.price, vol: v.cardState.lastAuction.volume },
    };
  }
  fs.writeFileSync(`${OUT}/vision-groundtruth.json`, JSON.stringify(truth, null, 1));
  log('视觉截图+真值已保存');
  await ctx.close();
}

await logicPlay();
await layoutAudit(412, 915, true, 'mobile');
await layoutAudit(1600, 950, false, 'desktop');
await visionCapture();
fs.writeFileSync(`${OUT}/report.json`, JSON.stringify(report, null, 1));
await browser.close();
console.log('DONE', JSON.stringify({ logicSteps: report.logic.length, errors: report.errors.length }));
