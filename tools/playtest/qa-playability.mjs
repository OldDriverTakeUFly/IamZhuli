// QA-3 可玩性测试: 双画像真实游玩,量化节奏与信息可达性(SOP.md)
import { chromium } from '/home/leo/.local/share/fnm/node-versions/v22.22.2/installation/lib/node_modules/playwright/index.mjs';
import fs from 'node:fs';
const OUT = '/tmp/playtest';
fs.mkdirSync(OUT, { recursive: true });
const browser = await chromium.launch({ executablePath: '/home/leo/.cache/ms-playwright/chromium-1234/chrome-linux64/chrome', headless: true, args: ['--no-sandbox'] });

const api = async (path, method = 'POST') => {
  const r = await fetch('http://localhost:5023' + path, { method });
  try { return await r.json(); } catch { return null; }
};

async function playSession(profile, segs) {
  const ctx = await browser.newContext({ viewport: { width: 412, height: 915 }, isMobile: true, hasTouch: true, locale: 'zh-CN' });
  const page = await ctx.newPage();
  await page.goto('http://localhost:5023/', { waitUntil: 'domcontentloaded' });
  await new Promise(r => setTimeout(r, 2000));
  await api('/api/loadlevel?id=accumulate');
  await api('/api/begin');
  await new Promise(r => setTimeout(r, 1500));

  const snap = () => page.evaluate(async () => await (await fetch('/api/snapshot')).json());
  const metrics = { profile, segs: [], clicks: 0, waits: 0 };
  let s = await snap();

  for (let seg = 0; seg < segs; seg++) {
    // 等出牌阶段
    let waitStart = Date.now();
    for (let w = 0; w < 50; w++) { await new Promise(r => setTimeout(r, 400)); s = await snap(); if (s.cardState?.phase === 'Play') break; }
    const resolveWait = Date.now() - waitStart; metrics.waits += resolveWait;

    const segStart = s.cardState.turn;
    const t0 = Date.now();
    let clicks = 0;

    // 画像决策
    if (profile === 'novice') {
      // 乱点: 出1张能出的牌
      const card = page.locator('#cardHand .card-item:not(.disabled)').first();
      if (await card.count()) { const c0 = await snap(); await card.tap({ timeout: 4000 }).catch(() => {}); clicks++; 
        await page.waitForFunction((n) => (window._lastSnap?.cardState?.cardsPlayed ?? -1) >= n || true, c0.cardState.cardsPlayed + 1, { timeout: 1500 }).catch(() => {});
      }
    } else {
      // 理性: 能量≥5才出双牌(操盘+架势若在)
      if (s.cardState.energy >= 5) {
        const stance = page.locator('#cardHand .card-item', { hasText: '架势' }).first();
        if (await stance.count()) { await stance.tap({ timeout: 4000 }).catch(() => {}); clicks++; await new Promise(r => setTimeout(r, 700)); }
        const op = page.locator('#cardHand .card-item:not(.disabled)').first();
        if (await op.count()) { await op.tap({ timeout: 4000 }).catch(() => {}); clicks++; await new Promise(r => setTimeout(r, 700)); }
      }
    }
    // 结束回合
    const eb = page.locator('#endTurnBtn');
    const feedT0 = Date.now();
    await eb.tap({ timeout: 4000 }).catch(() => {});
    clicks++; metrics.clicks += clicks;
    // 反馈延迟: 等cardMsg变化或快照变化
    let feedbackMs = 9999;
    for (let w = 0; w < 20; w++) { await new Promise(r => setTimeout(r, 100)); const s2 = await snap(); if (s2.cardState?.phase === 'Resolve' || s2.cardState?.turn !== segStart) { feedbackMs = Date.now() - feedT0; break; } }

    const opMs = Date.now() - t0;
    // 段末状态
    let sEnd = await snap();
    metrics.segs.push({ seg: segStart, phase: s.cardState.phaseName, clicks, opMs, feedbackMs,
      price: s.lastPrice, priceEnd: sEnd.lastPrice, energy: s.cardState.energy,
      played: sEnd.cardState?.cardsPlayed, auctionVol: sEnd.cardState?.lastAuction?.volume });
    // 体验日记一行
    console.log(`  [${profile}] 段${segStart}${s.cardState.phaseName}: ${clicks}次点击 ${opMs}ms 反馈${feedbackMs}ms 能量${s.cardState.energy}→${sEnd.cardState?.energy} 揭牌量${sEnd.cardState?.lastAuction?.volume ?? '-'}`);
  }

  // 同屏信息检测: 价格/手牌/能量/监管 四要素
  const infoReach = await page.evaluate(() => {
    const vis = (sel) => { const el = document.querySelector(sel); if (!el) return null; const r = el.getBoundingClientRect(); return r.top >= 0 && r.bottom <= window.innerHeight ? r.top : Math.round(r.top); };
    return { price: vis('#lastPrice'), hand: vis('#cardHand'), energy: vis('#cardEnergy'), heat: vis('#regulatorBar, #heatTxt, #regulatorTxt') };
  });
  metrics.infoReach = infoReach;
  await page.screenshot({ path: `${OUT}/qa3-${profile}.png` });
  await ctx.close();
  return metrics;
}

console.log('画像A: 新手乱点');
const novice = await playSession('novice', 6);
console.log('画像B: 理性策略');
const rational = await playSession('rational', 6);

// 评分
const score = (m) => {
  const segs = m.segs;
  const avgFeedback = segs.reduce((a, s) => a + s.feedbackMs, 0) / segs.length;
  const avgClicks = segs.reduce((a, s) => a + s.clicks, 0) / segs.length;
  const effective = segs.filter(s => (s.auctionVol ?? 0) > 0 || s.played > 0).length;
  const infoOk = Object.values(m.infoReach).filter(v => v !== null && v !== true && typeof v === 'number' ? false : v !== null).length;
  return { avgFeedback: Math.round(avgFeedback), avgClicks: +avgClicks.toFixed(1), effectiveSegs: `${effective}/${segs.length}`, infoReach: m.infoReach };
};
const report = { novice: { ...score(novice), segs: novice.segs }, rational: { ...score(rational), segs: rational.segs } };
fs.writeFileSync(`${OUT}/qa3-report.json`, JSON.stringify(report, null, 1));
console.log('\nQA-3 汇总:', JSON.stringify({ novice: score(novice), rational: score(rational) }, null, 1));
await browser.close();
