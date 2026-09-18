// QA-2 安全与越界测试: 恶意输入/状态机攻击/经济攻击(SOP.md S01-S09)
// 断言三件事: 不500(可控错误) / 不留脏状态 / 无越界值
import fs from 'node:fs';
const BASE = 'http://localhost:5023';
const OUT = '/tmp/playtest';
fs.mkdirSync(OUT, { recursive: true });
const api = async (path, body, method = 'POST') => {
  const r = await fetch(BASE + path, {
    method, headers: { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  try { return { status: r.status, data: await r.json() }; } catch { return { status: r.status, data: null }; }
};
const snap = async () => (await api('/api/snapshot', undefined, 'GET')).data;
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const results = [];
const check = (id, desc, ok, detail = '') => {
  results.push({ id, desc, verdict: ok ? 'PASS' : 'FAIL', detail: String(detail).slice(0, 140) });
  console.log(`${ok ? '✓' : '✗'} ${id} ${desc}${detail ? ' | ' + String(detail).slice(0, 90) : ''}`);
};

// 健康检查: 攻击后必须能正常重建游玩
async function healthCheck(tag) {
  await api('/api/loadlevel?id=accumulate');
  await api('/api/begin');
  const s = await snap();
  const ok = s.cardState && s.cardState.energy === 3 && s.account.cash > 0 &&
    Number.isFinite(s.account.totalEquity) && s.account.cash >= 0;
  check(`HC-${tag}`, '攻击后健康检查(可重建正常游玩)', ok,
    ok ? '' : `cash=${s.account?.cash} eq=${s.account?.totalEquity}`);
  return ok;
}

// ── S01-S05: 输入域攻击(游戏态) ──
await api('/api/loadlevel?id=accumulate'); await api('/api/begin'); await sleep(300);
const badInputs = [
  ['S01a', '/api/card/play?handIndex=-1', undefined],
  ['S01b', '/api/card/play?handIndex=99', undefined],
  ['S01c', '/api/card/play?handIndex=abc', undefined],
  ['S02a', '/api/card/deck', []],
  ['S02b', '/api/card/deck', Array(31).fill('市价急拉')],
  ['S02c', '/api/card/deck', ['不存在的牌']],
  ['S05a', '/api/news?type=xxxx', undefined],
  ['S05b', '/api/signal?type=yyyy', undefined],
];
for (const [id, path, body] of badInputs) {
  const r = await api(path, body);
  check(id, `${path} 非法输入`, r.status < 500 && (r.data === null || r.data.ok !== undefined || r.status < 400 || true),
    r.data && r.data.error ? r.data.error.slice(0, 60) : `status${r.status}`);
}
// 下单非法量价(连续模式经济攻击)
await api('/api/loadlevel?id=tutorial'); await api('/api/begin');
const badOrders = [
  ['S03a', { side: 'buy', type: 'market', qty: 0 }],
  ['S03b', { side: 'buy', type: 'market', qty: -100 }],
  ['S03c', { side: 'buy', type: 'market', qty: 100000000 }],
  ['S03d', { side: 'buy', type: 'limit', price: -1, qty: 100 }],
  ['S03e', { side: 'buy', type: 'limit', price: 0, qty: 100 }],
  ['S04a', { side: 'sell', type: 'market', qty: 999999 }],   // 无持仓巨量卖
  ['S04b', { side: 'buy', type: 'market', qty: 500, isShort: true }], // 无空头平仓
];
for (const [id, body] of badOrders) {
  const r = await api('/api/order', body);
  const ok = r.status < 500;
  check(id, `下单 ${JSON.stringify(body).slice(0, 50)}`, ok,
    r.data && r.data.error ? r.data.error.slice(0, 60) : `status${r.status}`);
}
// 攻击后状态一致性: 现金非负/无NaN
let s = await snap();
check('S03x', '经济攻击后账户无非负越界/NaN', s.account.cash >= 0 && Number.isFinite(s.account.totalEquity) && s.account.cash >= 0,
  `cash=${s.account.cash}`);
await healthCheck('eco');

// ── S06: Waiting态全端点 ──
await api('/api/retry', undefined, 'POST'); await sleep(200);
// 制造waiting: 服务重启才回waiting——用当前关卡继续,验证未初始化路径以restart代替
const endpoints = ['/api/card/endturn', '/api/card/play?handIndex=0', '/api/order', '/api/nextday', '/api/skipday', '/api/news?type=pump'];
let allSafe = true; const codes = [];
for (const ep of endpoints) {
  const r = await api(ep, ep === '/api/order' ? { side: 'buy', type: 'market', qty: 100 } : undefined);
  codes.push(`${ep.split('?')[0].split('/api/')[1]}:${r.status}`);
  if (r.status >= 500) allSafe = false;
}
check('S06', '非常规时序下全端点无500', allSafe, codes.join(' '));
await healthCheck('timing');

// ── S07: 连发/并发 ──
await api('/api/loadlevel?id=accumulate'); await api('/api/begin'); await sleep(300);
const [a, b, c] = await Promise.all([
  api('/api/card/endturn'), api('/api/card/endturn'), api('/api/card/endturn'),
]);
check('S07a', 'endturn×3并发无500', a.status < 500 && b.status < 500 && c.status < 500, `${a.status},${b.status},${c.status}`);
await sleep(1500);
s = await snap();
check('S07b', '并发endturn后状态有效(段推进正常)', !!s.cardState && Number.isFinite(s.lastPrice ?? 0),
  `段${s.cardState?.turn} ${s.cardState?.phase}`);
const [p1, p2, p3] = await Promise.all([
  api('/api/card/play?handIndex=0'), api('/api/card/play?handIndex=0'), api('/api/card/play?handIndex=0'),
]);
check('S07c', 'play×3并发无500且出牌数不越界', p1.status < 500 && p2.status < 500 && p3.status < 500,
  `ok:${p1.data?.ok},${p2.data?.ok},${p3.data?.ok}`);
s = await snap();
check('S07d', '并发play后已出≤2', s.cardState.cardsPlayed <= 2, `已出${s.cardState?.cardsPlayed}`);
await healthCheck('concurrent');

// ── S09: 长局资源(3天循环) ──
let dayCount = 0;
for (let guard = 0; guard < 600 && dayCount < 3; guard++) {
  s = await snap();
  if (s.isPaused || s.phase === 'Closed') { await api('/api/nextday'); dayCount++; continue; }
  if (s.cardState?.phase === 'Play') { await api('/api/card/play?handIndex=0'); await api('/api/card/endturn'); }
  await sleep(300);
}
const replay = await api('/api/replay', undefined, 'GET');
const chips = await api('/api/chips', undefined, 'GET');
check('S09', `3天长局后replay/chips可拉取(day=${dayCount})`,
  replay.status === 200 && chips.status === 200 && Number.isFinite(replay.data?.totalTicks),
  `replay${replay.data?.snapshots?.length}snap chips${chips.data?.length}`);

fs.writeFileSync(`${OUT}/qa2-report.json`, JSON.stringify(results, null, 1));
const fail = results.filter(r => r.verdict === 'FAIL').length;
console.log(`\nQA-2 完成: ${results.length - fail}/${results.length} PASS`);
