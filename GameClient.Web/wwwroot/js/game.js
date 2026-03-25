'use strict';

// ─────────────────────────────────────────────────────
// 설정
// ─────────────────────────────────────────────────────
const WS_URL        = 'ws://localhost:7778/ws';
const CANVAS_W      = 800;
const CANVAS_H      = 600;
const MAP_W         = 3200;
const MAP_H         = 2400;
const MOVE_SEND_INTERVAL = 50;  // ms — 서버 전송 주기 (20pps)
const MOVE_INTERVAL      = 50;  // ms (구버전 호환용, 미사용)

// ─────────────────────────────────────────────────────
// Long(uint64) → string 변환 헬퍼
// protobuf.js는 uint64를 Long 객체로 디코딩한다.
// ─────────────────────────────────────────────────────
function toId(v) {
  if (v == null) return '0';
  if (typeof v === 'object' && v.toString) return v.toString();
  return String(v);
}

// ─────────────────────────────────────────────────────
// 전역 상태
// ─────────────────────────────────────────────────────
let proto = null;
let ws    = null;
let _heartbeatTimer = null;

const me        = { id: '0', name: '', x: 100, y: 100 };
const character = { level: 1, hp: 100, maxHp: 100, exp: 0, nextExp: 100, atk: 15, def: 5 };

const rooms       = new Map(); // roomId(string) → { id, playerCount, maxPlayers, isStarted }
const roomPlayers = new Map(); // playerId(string) → { id, name, isReady }
let   currentRoomId = '0';

const gamePlayers  = new Map(); // playerId(string) → { id, name, x, y, hp, maxHp, level, alive }
const gameMonsters = new Map(); // monsterId(string) → { id, type, x, y, hp, maxHp, alive }
const gameGems     = new Map(); // gemId(string) → { id, x, y, expValue, spawnTime }
const effects      = [];        // 투사체 이펙트 { sx, sy, tx, ty, startTime, duration, color }

const keys        = {};
let   lastMove    = 0;
let   lastAttack  = 0;
let   animId      = null;
let   isReady     = false;
let   waveNumber  = 0;     // 현재 웨이브 번호
let   lastSentX   = 0;    // 서버에 마지막으로 보낸 X 좌표
let   lastSentY   = 0;    // 서버에 마지막으로 보낸 Y 좌표

const ATTACK_INTERVAL = 1000; // ms — 서버 쿨다운(1s)에 맞춤

// ─────────────────────────────────────────────────────
// 화면 전환
// ─────────────────────────────────────────────────────
const screens = ['login', 'lobby', 'room', 'game', 'result'];

function showScreen(name) {
  screens.forEach(s => {
    const el = document.getElementById(`screen-${s}`);
    if (el) el.classList.toggle('hidden', s !== name);
  });
  if (name === 'game') startGameLoop();
  else stopGameLoop();
}

function setStatus(id, msg, isOk = false) {
  const el = document.getElementById(id);
  if (!el) return;
  el.textContent = msg;
  el.className   = 'status-msg' + (isOk ? ' status-ok' : '');
}

// ─────────────────────────────────────────────────────
// 프로시저럴 스프라이트 시스템 (저작권 없음 — Canvas 2D 직접 드로잉)
// ─────────────────────────────────────────────────────
const sprites = {}; // name → HTMLCanvasElement

function buildSprites() {
  const S = 64;
  function mk(fn) {
    const c = document.createElement('canvas');
    c.width = c.height = S;
    fn(c.getContext('2d'));
    return c;
  }
  function eye(ctx, x, y, r) {
    ctx.fillStyle = '#fff'; ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI*2); ctx.fill();
    ctx.fillStyle = '#111'; ctx.beginPath(); ctx.arc(x+r*.15, y+r*.15, r*.55, 0, Math.PI*2); ctx.fill();
    ctx.fillStyle = 'rgba(255,255,255,.6)'; ctx.beginPath(); ctx.arc(x-r*.3, y-r*.3, r*.28, 0, Math.PI*2); ctx.fill();
  }
  function xEye(ctx, x, y, r) {
    ctx.save(); ctx.strokeStyle = '#dc2626'; ctx.lineWidth = 2.5; ctx.lineCap = 'round';
    ctx.beginPath(); ctx.moveTo(x-r,y-r); ctx.lineTo(x+r,y+r); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(x+r,y-r); ctx.lineTo(x-r,y+r); ctx.stroke();
    ctx.restore();
  }

  // ── SLIME (녹색 블롭) ──────────────────────────────
  sprites['slime'] = mk(ctx => {
    ctx.fillStyle = '#4ade80';
    ctx.beginPath(); ctx.arc(23,22,10,Math.PI,0); ctx.fill();
    ctx.beginPath(); ctx.arc(32,17,12,Math.PI,0); ctx.fill();
    ctx.beginPath(); ctx.arc(41,22,10,Math.PI,0); ctx.fill();
    ctx.beginPath(); ctx.ellipse(32,38,24,20,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#15803d'; ctx.lineWidth=2.5;
    ctx.beginPath(); ctx.arc(23,22,10,Math.PI,0); ctx.stroke();
    ctx.beginPath(); ctx.arc(32,17,12,Math.PI,0); ctx.stroke();
    ctx.beginPath(); ctx.arc(41,22,10,Math.PI,0); ctx.stroke();
    ctx.beginPath(); ctx.ellipse(32,38,24,20,0,0,Math.PI*2); ctx.stroke();
    eye(ctx,25,36,5); eye(ctx,39,36,5);
    ctx.fillStyle='rgba(255,255,255,.3)'; ctx.beginPath(); ctx.ellipse(24,28,7,5,-0.5,0,Math.PI*2); ctx.fill();
  });

  // ── ORC (뿔 달린 악마) ────────────────────────────
  sprites['orc'] = mk(ctx => {
    ctx.fillStyle='#92400e';
    ctx.beginPath(); ctx.moveTo(20,14); ctx.lineTo(13,2); ctx.lineTo(26,16); ctx.fill();
    ctx.beginPath(); ctx.moveTo(44,14); ctx.lineTo(51,2); ctx.lineTo(38,16); ctx.fill();
    ctx.fillStyle='#a855f7';
    ctx.beginPath(); ctx.ellipse(32,31,18,20,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#6b21a8'; ctx.lineWidth=2.5; ctx.stroke();
    ctx.fillStyle='#a855f7';
    ctx.beginPath(); ctx.ellipse(15,31,6,9,-0.3,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#6b21a8'; ctx.lineWidth=2; ctx.stroke();
    ctx.beginPath(); ctx.ellipse(49,31,6,9,0.3,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#6b21a8'; ctx.lineWidth=2; ctx.stroke();
    eye(ctx,25,28,5.5); eye(ctx,39,28,5.5);
    ctx.save(); ctx.strokeStyle='#4c0d9e'; ctx.lineWidth=3; ctx.lineCap='round';
    ctx.beginPath(); ctx.moveTo(20,22); ctx.lineTo(30,24); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(44,22); ctx.lineTo(34,24); ctx.stroke();
    ctx.restore();
    ctx.fillStyle='#fef9c3';
    ctx.beginPath(); ctx.moveTo(27,41); ctx.lineTo(24,51); ctx.lineTo(30,41); ctx.fill();
    ctx.beginPath(); ctx.moveTo(37,41); ctx.lineTo(40,51); ctx.lineTo(34,41); ctx.fill();
    ctx.fillStyle='#9333ea';
    ctx.beginPath(); ctx.ellipse(32,55,14,8,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#6b21a8'; ctx.lineWidth=2; ctx.stroke();
  });

  // ── DRAGON (보스 — 날개 펼친 용) ──────────────────
  sprites['dragon'] = mk(ctx => {
    ctx.fillStyle='#dc2626';
    ctx.beginPath(); ctx.moveTo(32,30); ctx.bezierCurveTo(12,22,2,38,8,52); ctx.bezierCurveTo(14,46,22,42,32,42); ctx.fill();
    ctx.beginPath(); ctx.moveTo(32,30); ctx.bezierCurveTo(52,22,62,38,56,52); ctx.bezierCurveTo(50,46,42,42,32,42); ctx.fill();
    ctx.strokeStyle='#7f1d1d'; ctx.lineWidth=2;
    ctx.beginPath(); ctx.moveTo(32,30); ctx.bezierCurveTo(12,22,2,38,8,52); ctx.bezierCurveTo(14,46,22,42,32,42); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(32,30); ctx.bezierCurveTo(52,22,62,38,56,52); ctx.bezierCurveTo(50,46,42,42,32,42); ctx.stroke();
    ctx.fillStyle='#ef4444';
    ctx.beginPath(); ctx.ellipse(32,28,18,22,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#7f1d1d'; ctx.lineWidth=2.5; ctx.stroke();
    ctx.fillStyle='#fcd34d';
    [20,26,32,38,44].forEach(x => {
      ctx.beginPath(); ctx.moveTo(x,13); ctx.lineTo(x-3,4); ctx.lineTo(x+3,13); ctx.fill();
    });
    ctx.fillStyle='#fef08a'; ctx.shadowColor='#fde047'; ctx.shadowBlur=8;
    ctx.beginPath(); ctx.arc(25,26,5,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.arc(39,26,5,0,Math.PI*2); ctx.fill();
    ctx.shadowBlur=0; ctx.fillStyle='#111';
    ctx.beginPath(); ctx.arc(25,26,2.5,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.arc(39,26,2.5,0,Math.PI*2); ctx.fill();
    ctx.fillStyle='#7f1d1d';
    ctx.beginPath(); ctx.arc(28,35,2.5,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.arc(36,35,2.5,0,Math.PI*2); ctx.fill();
  });

  // ── BAT (박쥐) ────────────────────────────────────
  sprites['bat'] = mk(ctx => {
    ctx.fillStyle='#64748b';
    ctx.beginPath(); ctx.moveTo(32,33); ctx.bezierCurveTo(22,23,6,19,4,31); ctx.bezierCurveTo(4,39,18,39,32,37); ctx.fill();
    ctx.beginPath(); ctx.moveTo(32,33); ctx.bezierCurveTo(42,23,58,19,60,31); ctx.bezierCurveTo(60,39,46,39,32,37); ctx.fill();
    ctx.strokeStyle='#334155'; ctx.lineWidth=1.5;
    ctx.beginPath(); ctx.moveTo(32,33); ctx.bezierCurveTo(22,23,6,19,4,31); ctx.bezierCurveTo(4,39,18,39,32,37); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(32,33); ctx.bezierCurveTo(42,23,58,19,60,31); ctx.bezierCurveTo(60,39,46,39,32,37); ctx.stroke();
    ctx.save(); ctx.strokeStyle='#1e293b'; ctx.lineWidth=1;
    [[32,33,10,22],[32,33,6,27],[32,33,54,22],[32,33,58,27]].forEach(([ax,ay,bx,by]) => {
      ctx.beginPath(); ctx.moveTo(ax,ay); ctx.lineTo(bx,by); ctx.stroke();
    });
    ctx.restore();
    ctx.fillStyle='#475569';
    ctx.beginPath(); ctx.ellipse(32,33,10,13,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#1e293b'; ctx.lineWidth=2; ctx.stroke();
    ctx.fillStyle='#475569';
    ctx.beginPath(); ctx.moveTo(26,24); ctx.lineTo(22,14); ctx.lineTo(30,22); ctx.fill();
    ctx.beginPath(); ctx.moveTo(38,24); ctx.lineTo(42,14); ctx.lineTo(34,22); ctx.fill();
    ctx.strokeStyle='#1e293b'; ctx.lineWidth=1.5;
    ctx.beginPath(); ctx.moveTo(26,24); ctx.lineTo(22,14); ctx.lineTo(30,22); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(38,24); ctx.lineTo(42,14); ctx.lineTo(34,22); ctx.stroke();
    ctx.fillStyle='#dc2626';
    ctx.beginPath(); ctx.arc(27,31,3.5,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.arc(37,31,3.5,0,Math.PI*2); ctx.fill();
    ctx.fillStyle='#111';
    ctx.beginPath(); ctx.arc(27,31,1.5,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.arc(37,31,1.5,0,Math.PI*2); ctx.fill();
    ctx.fillStyle='#fff';
    ctx.beginPath(); ctx.moveTo(29,38); ctx.lineTo(27,44); ctx.lineTo(31,38); ctx.fill();
    ctx.beginPath(); ctx.moveTo(35,38); ctx.lineTo(37,44); ctx.lineTo(33,38); ctx.fill();
  });

  // ── ZOMBIE (좀비) ─────────────────────────────────
  sprites['zombie'] = mk(ctx => {
    ctx.fillStyle='#86efac'; ctx.strokeStyle='#166534'; ctx.lineWidth=2;
    ctx.fillRect(22,38,20,22); ctx.strokeRect(22,38,20,22);
    ctx.fillRect(4,37,18,10); ctx.strokeRect(4,37,18,10);
    ctx.fillRect(42,37,18,10); ctx.strokeRect(42,37,18,10);
    ctx.fillStyle='#4ade80';
    ctx.beginPath(); ctx.ellipse(32,26,14,16,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#166534'; ctx.lineWidth=2.5; ctx.stroke();
    xEye(ctx,26,24,4); xEye(ctx,38,24,4);
    ctx.save(); ctx.strokeStyle='#15803d'; ctx.lineWidth=2;
    ctx.beginPath(); ctx.moveTo(25,34); ctx.lineTo(28,32); ctx.lineTo(32,34); ctx.lineTo(36,32); ctx.lineTo(39,34); ctx.stroke();
    ctx.restore();
    ctx.save(); ctx.strokeStyle='#166534'; ctx.lineWidth=2;
    ctx.beginPath(); ctx.moveTo(28,11); ctx.lineTo(26,5); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(32,10); ctx.lineTo(32,4); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(36,11); ctx.lineTo(38,5); ctx.stroke();
    ctx.restore();
  });

  // ── SKELETON (해골) ───────────────────────────────
  sprites['skeleton'] = mk(ctx => {
    ctx.fillStyle='#f1f5f9';
    ctx.beginPath(); ctx.ellipse(32,22,16,18,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#94a3b8'; ctx.lineWidth=2.5; ctx.stroke();
    ctx.fillStyle='#e2e8f0';
    ctx.beginPath(); ctx.arc(32,34,10,0,Math.PI); ctx.fill();
    ctx.strokeStyle='#94a3b8'; ctx.lineWidth=2; ctx.stroke();
    ctx.fillStyle='#fff';
    for (let i=0;i<4;i++) { ctx.fillRect(24+i*5,34,3,5); ctx.strokeStyle='#94a3b8'; ctx.lineWidth=1; ctx.strokeRect(24+i*5,34,3,5); }
    ctx.fillStyle='#0f172a';
    ctx.beginPath(); ctx.ellipse(25,20,5.5,6.5,0,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.ellipse(39,20,5.5,6.5,0,0,Math.PI*2); ctx.fill();
    ctx.fillStyle='#a3e635';
    ctx.beginPath(); ctx.arc(25,20,2.5,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.arc(39,20,2.5,0,Math.PI*2); ctx.fill();
    ctx.save(); ctx.strokeStyle='#cbd5e1'; ctx.lineWidth=1.5;
    ctx.beginPath(); ctx.arc(32,50,10,-Math.PI*.8,-Math.PI*.2); ctx.stroke();
    ctx.beginPath(); ctx.arc(32,53,13,-Math.PI*.75,-Math.PI*.25); ctx.stroke();
    ctx.strokeStyle='#cbd5e1'; ctx.lineWidth=2;
    ctx.beginPath(); ctx.moveTo(32,40); ctx.lineTo(32,56); ctx.stroke();
    for (let i=0;i<4;i++) { ctx.beginPath(); ctx.moveTo(28,42+i*4); ctx.lineTo(36,42+i*4); ctx.stroke(); }
    ctx.restore();
  });

  // ── GHOST (유령) ──────────────────────────────────
  sprites['ghost'] = mk(ctx => {
    ctx.globalAlpha=0.88;
    ctx.fillStyle='#67e8f9';
    ctx.beginPath();
    ctx.arc(32,26,20,Math.PI,0);
    ctx.lineTo(52,52);
    ctx.bezierCurveTo(48,60,44,52,40,57);
    ctx.bezierCurveTo(36,62,34,55,32,57);
    ctx.bezierCurveTo(30,59,28,62,24,57);
    ctx.bezierCurveTo(20,52,16,60,12,52);
    ctx.closePath();
    ctx.fill();
    ctx.strokeStyle='#0891b2'; ctx.lineWidth=2; ctx.stroke();
    ctx.globalAlpha=1;
    ctx.fillStyle='#0c4a6e';
    ctx.beginPath(); ctx.ellipse(26,26,5,7,0,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.ellipse(38,26,5,7,0,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.ellipse(32,36,4,5,0,0,Math.PI*2); ctx.fill();
    ctx.fillStyle='rgba(224,242,254,.45)';
    ctx.beginPath(); ctx.ellipse(27,21,8,6,-0.5,0,Math.PI*2); ctx.fill();
  });

  // ── GIANT ZOMBIE (거대좀비) ───────────────────────
  sprites['giant-zombie'] = mk(ctx => {
    ctx.fillStyle='#fb923c'; ctx.strokeStyle='#9a3412'; ctx.lineWidth=2.5;
    ctx.fillRect(14,34,36,28); ctx.strokeRect(14,34,36,28);
    ctx.fillRect(1,32,13,16); ctx.strokeRect(1,32,13,16);
    ctx.fillRect(50,32,13,16); ctx.strokeRect(50,32,13,16);
    ctx.fillStyle='#fdba74';
    ctx.beginPath(); ctx.ellipse(32,22,19,19,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#9a3412'; ctx.lineWidth=2.5; ctx.stroke();
    xEye(ctx,24,20,5.5); xEye(ctx,40,20,5.5);
    ctx.fillStyle='#7c2d12';
    ctx.beginPath(); ctx.ellipse(32,31,8,5,0,0,Math.PI); ctx.fill();
    ctx.fillStyle='#fff';
    [26,31,36].forEach(x => ctx.fillRect(x,30,4,5));
    ctx.save(); ctx.strokeStyle='#7c2d12'; ctx.lineWidth=2;
    ctx.beginPath(); ctx.moveTo(20,11); ctx.lineTo(24,20); ctx.moveTo(22,11); ctx.lineTo(26,20); ctx.stroke();
    ctx.restore();
  });

  // ── REAPER (보스 — 저승사자) ──────────────────────
  sprites['reaper'] = mk(ctx => {
    ctx.save(); ctx.strokeStyle='#78716c'; ctx.lineWidth=3.5; ctx.lineCap='round';
    ctx.beginPath(); ctx.moveTo(50,8); ctx.lineTo(40,60); ctx.stroke();
    ctx.fillStyle='#d6d3d1';
    ctx.beginPath(); ctx.moveTo(50,8); ctx.bezierCurveTo(66,12,70,30,54,30); ctx.bezierCurveTo(64,20,60,10,50,8); ctx.fill();
    ctx.strokeStyle='#a8a29e'; ctx.lineWidth=1.5; ctx.stroke();
    ctx.restore();
    ctx.fillStyle='#1c1917';
    ctx.beginPath(); ctx.moveTo(32,8); ctx.bezierCurveTo(14,18,8,36,10,62); ctx.lineTo(54,62); ctx.bezierCurveTo(56,36,50,18,32,8); ctx.fill();
    ctx.strokeStyle='#44403c'; ctx.lineWidth=2; ctx.stroke();
    ctx.fillStyle='#0c0a09';
    ctx.beginPath(); ctx.ellipse(32,22,16,14,0,0,Math.PI*2); ctx.fill();
    ctx.fillStyle='#dc2626'; ctx.shadowColor='#ef4444'; ctx.shadowBlur=12;
    ctx.beginPath(); ctx.arc(26,21,3.5,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.arc(38,21,3.5,0,Math.PI*2); ctx.fill();
    ctx.shadowBlur=0;
    ctx.save(); ctx.strokeStyle='#292524'; ctx.lineWidth=1.5;
    ctx.beginPath(); ctx.moveTo(32,32); ctx.lineTo(28,62); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(32,32); ctx.lineTo(36,62); ctx.stroke();
    ctx.restore();
  });

  // ── PLAYER ME (마법사) ────────────────────────────
  sprites['player-me'] = mk(ctx => {
    ctx.fillStyle='#1d4ed8';
    ctx.beginPath(); ctx.moveTo(32,28); ctx.bezierCurveTo(18,33,14,49,12,62); ctx.lineTo(52,62); ctx.bezierCurveTo(50,49,46,33,32,28); ctx.fill();
    ctx.strokeStyle='#1e3a8a'; ctx.lineWidth=2; ctx.stroke();
    ctx.fillStyle='#3b82f6';
    ctx.beginPath(); ctx.moveTo(32,28); ctx.bezierCurveTo(20,35,16,50,14,62); ctx.lineTo(50,62); ctx.bezierCurveTo(48,50,44,35,32,28); ctx.fill();
    ctx.fillStyle='#93c5fd';
    ctx.beginPath(); ctx.arc(22,46,2.5,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.arc(32,52,2.5,0,Math.PI*2); ctx.fill();
    ctx.beginPath(); ctx.arc(42,46,2.5,0,Math.PI*2); ctx.fill();
    ctx.fillStyle='#fde68a';
    ctx.beginPath(); ctx.ellipse(32,22,11,12,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#92400e'; ctx.lineWidth=1.5; ctx.stroke();
    ctx.fillStyle='#1d4ed8';
    ctx.beginPath(); ctx.moveTo(32,1); ctx.lineTo(19,14); ctx.lineTo(45,14); ctx.closePath(); ctx.fill();
    ctx.fillStyle='#2563eb';
    ctx.beginPath(); ctx.ellipse(32,14,14,4.5,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#1e40af'; ctx.lineWidth=1.5; ctx.stroke();
    ctx.fillStyle='#fde68a';
    ctx.beginPath(); ctx.arc(32,6,2.5,0,Math.PI*2); ctx.fill();
    eye(ctx,27,21,4); eye(ctx,37,21,4);
    ctx.save(); ctx.strokeStyle='#92400e'; ctx.lineWidth=1.5;
    ctx.beginPath(); ctx.arc(32,26,5,0.1,Math.PI-0.1); ctx.stroke();
    ctx.restore();
    ctx.save(); ctx.strokeStyle='#92400e'; ctx.lineWidth=3; ctx.lineCap='round';
    ctx.beginPath(); ctx.moveTo(52,28); ctx.lineTo(58,60); ctx.stroke();
    ctx.fillStyle='#a78bfa'; ctx.shadowColor='#8b5cf6'; ctx.shadowBlur=10;
    ctx.beginPath(); ctx.arc(52,26,5.5,0,Math.PI*2); ctx.fill();
    ctx.shadowBlur=0; ctx.restore();
  });

  // ── PLAYER OTHER (기사) ───────────────────────────
  sprites['player'] = mk(ctx => {
    ctx.fillStyle='#7e22ce';
    ctx.beginPath(); ctx.moveTo(32,28); ctx.bezierCurveTo(18,33,14,49,12,62); ctx.lineTo(52,62); ctx.bezierCurveTo(50,49,46,33,32,28); ctx.fill();
    ctx.strokeStyle='#581c87'; ctx.lineWidth=2; ctx.stroke();
    ctx.fillStyle='#9333ea';
    ctx.beginPath(); ctx.moveTo(32,28); ctx.bezierCurveTo(20,35,16,50,14,62); ctx.lineTo(50,62); ctx.bezierCurveTo(48,50,44,35,32,28); ctx.fill();
    ctx.fillStyle='#fde68a';
    ctx.beginPath(); ctx.ellipse(32,22,11,12,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#92400e'; ctx.lineWidth=1.5; ctx.stroke();
    ctx.fillStyle='#6b21a8';
    ctx.beginPath(); ctx.arc(32,14,13,Math.PI,0); ctx.fill();
    ctx.beginPath(); ctx.ellipse(32,14,13,5,0,0,Math.PI*2); ctx.fill();
    ctx.strokeStyle='#4c0d9e'; ctx.lineWidth=1.5;
    ctx.beginPath(); ctx.arc(32,14,13,Math.PI,0); ctx.stroke();
    eye(ctx,27,21,4); eye(ctx,37,21,4);
    ctx.save(); ctx.strokeStyle='#e2e8f0'; ctx.lineWidth=3; ctx.lineCap='round';
    ctx.beginPath(); ctx.moveTo(53,28); ctx.lineTo(59,58); ctx.stroke();
    ctx.strokeStyle='#f59e0b'; ctx.lineWidth=5;
    ctx.beginPath(); ctx.moveTo(50,34); ctx.lineTo(62,34); ctx.stroke();
    ctx.fillStyle='#e2e8f0';
    ctx.beginPath(); ctx.ellipse(53,28,3,5,0,0,Math.PI*2); ctx.fill();
    ctx.restore();
  });

  console.log('[sprites] 빌드 완료:', Object.keys(sprites).length, '개');
}

const SPRITE_NAMES = ['slime','orc','dragon','bat','zombie','skeleton','ghost','giant-zombie','reaper'];

// ─────────────────────────────────────────────────────
// protobuf 초기화
// ─────────────────────────────────────────────────────
async function initProto() {
  const resp = await fetch('/js/proto-bundle.json');
  const json = await resp.json();
  proto = protobuf.Root.fromJSON(json);
  console.log('[proto] 로드 완료:', !!proto.lookupType('dhnet.GamePacket'));
}

function getGamePacketType() {
  return proto.lookupType('dhnet.GamePacket');
}

function encode(packet) {
  const T   = getGamePacketType();
  const msg = T.create(packet);
  return T.encode(msg).finish();
}

function decode(buf) {
  return getGamePacketType().decode(new Uint8Array(buf));
}

function send(packet) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  ws.send(encode(packet));
}

// ─────────────────────────────────────────────────────
// WebSocket 연결
// ─────────────────────────────────────────────────────
function connect(onOpen) {
  if (ws && ws.readyState === WebSocket.OPEN) { onOpen(); return; }

  ws = new WebSocket(WS_URL);
  ws.binaryType = 'arraybuffer';

  ws.onopen = () => {
    console.log('[ws] 연결 성공');
    setStatus('login-status', '');
    // 20초마다 heartbeat 전송 (서버 idle 타임아웃 30초 방지)
    _heartbeatTimer = setInterval(() => {
      if (ws && ws.readyState === WebSocket.OPEN) send({ reqHeartbeat: {} });
    }, 20000);
    onOpen();
  };

  ws.onmessage = e => {
    try {
      const pkt = decode(e.data);
      console.log('[ws] 패킷 수신:', pkt.payload);
      handlePacket(pkt);
    } catch (err) { console.error('[ws] 패킷 처리 오류:', err); }
  };

  ws.onclose = e => {
    console.warn('[ws] 연결 종료 — code:', e.code, 'reason:', e.reason);
    clearInterval(_heartbeatTimer);
    _heartbeatTimer = null;
    ws = null;
    showScreen('login');
    setStatus('login-status', '서버 연결이 끊어졌습니다.');
  };

  ws.onerror = e => {
    console.error('[ws] 연결 오류:', e);
    setStatus('login-status', '연결 실패: 서버를 확인해주세요.');
  };
}

// ─────────────────────────────────────────────────────
// 패킷 디스패치
// protobuf.js oneof: pkt.payload → 현재 설정된 필드 이름 문자열
// ─────────────────────────────────────────────────────
function handlePacket(pkt) {
  switch (pkt.payload) {
    case 'resRegister':      onResRegister(pkt.resRegister);           break;
    case 'resLogin':         onResLogin(pkt.resLogin);                 break;
    case 'resCharacterInfo': onResCharacterInfo(pkt.resCharacterInfo); break;

    case 'resRoomList':      onResRoomList(pkt.resRoomList);           break;
    case 'resRoomEnter':     onResRoomEnter(pkt.resRoomEnter);         break;
    case 'resRoomExit':      onResRoomExit();                          break;

    case 'notiRoomEnter':    onNotiRoomEnter(pkt.notiRoomEnter);       break;
    case 'notiRoomExit':     onNotiRoomExit(pkt.notiRoomExit);         break;
    case 'notiReadyGame':    onNotiReadyGame(pkt.notiReadyGame);       break;
    case 'notiGameStart':    onNotiGameStart();                        break;

    case 'resEnterGame':     onResEnterGame(pkt.resEnterGame);         break;
    case 'notiMove':         onNotiMove(pkt.notiMove);                 break;
    case 'notiHpChange':     onNotiHpChange(pkt.notiHpChange);         break;
    case 'notiDeath':        onNotiDeath(pkt.notiDeath);               break;
    case 'notiRespawn':      onNotiRespawn(pkt.notiRespawn);           break;
    case 'notiExpGain':      onNotiExpGain(pkt.notiExpGain);           break;
    case 'notiLevelUp':      onNotiLevelUp(pkt.notiLevelUp);           break;
    case 'notiCombat':       onNotiCombat(pkt.notiCombat);             break;
    case 'notiMonsterAttack':onNotiMonsterAttack(pkt.notiMonsterAttack);break;
    case 'notiMonsterMove':  onNotiMonsterMove(pkt.notiMonsterMove);   break;
    case 'notiWaveStart':    onNotiWaveStart(pkt.notiWaveStart);       break;
    case 'notiSpawnMonster': onNotiSpawnMonster(pkt.notiSpawnMonster); break;
    case 'notiGemSpawn':     onNotiGemSpawn(pkt.notiGemSpawn);         break;
    case 'notiGemCollect':   onNotiGemCollect(pkt.notiGemCollect);     break;
    case 'notiWeaponChoice':  onNotiWeaponChoice(pkt.notiWeaponChoice);   break;
    case 'notiSurvivalTime':  onNotiSurvivalTime(pkt.notiSurvivalTime);   break;
    case 'notiGameEnd':      onNotiGameEnd(pkt.notiGameEnd);           break;
    case 'notiGameChat':     onNotiGameChat(pkt.notiGameChat);         break;

    case 'resHeartbeat':     /* 타이머가 주기적으로 전송하므로 별도 처리 불필요 */ break;

    default: break;
  }
}

// ─────────────────────────────────────────────────────
// 로그인 / 등록
// ─────────────────────────────────────────────────────
// ErrorCode enum 숫자 (proto 정의 기준)
const EC = {
  SUCCESS:               0,
  SERVER_FULL:        1000,
  DB_ERROR:           1001,
  USERNAME_TAKEN:     1004,
  INVALID_CREDENTIALS:1005,
  ALREADY_LOGGED_IN:  1006,
  LOBBY_FULL:         2000,
  ALREADY_IN_ROOM:    3000,
  ROOM_NOT_FOUND:     3001,
  ROOM_FULL:          3002,
  ROOM_ALREADY_STARTED:3003,
};

function onResRegister(res) {
  if (res.errorCode === EC.SUCCESS) {
    setStatus('login-status', '계정 생성 완료! 로그인해주세요.', true);
    switchTab('login');
  } else {
    const msgs = {
      [EC.USERNAME_TAKEN]: '이미 존재하는 아이디입니다.',
      [EC.DB_ERROR]:       'DB 오류가 발생했습니다.',
    };
    setStatus('login-status', msgs[res.errorCode] || `오류 코드: ${res.errorCode}`);
  }
}

function onResLogin(res) {
  if (res.errorCode === EC.SUCCESS) {
    me.id   = toId(res.playerId);
    me.name = res.playerName || '';
    document.getElementById('lobby-username').textContent = me.name;
    // ResCharacterInfo가 뒤따라오면 거기서 showScreen('lobby')
  } else {
    const msgs = {
      [EC.INVALID_CREDENTIALS]: '아이디 또는 비밀번호가 틀렸습니다.',
      [EC.ALREADY_LOGGED_IN]:   '이미 로그인된 계정입니다.',
      [EC.SERVER_FULL]:         '서버가 가득 찼습니다.',
      [EC.LOBBY_FULL]:          '로비가 가득 찼습니다.',
    };
    setStatus('login-status', msgs[res.errorCode] || `로그인 실패 (${res.errorCode})`);
  }
}

function onResCharacterInfo(info) {
  character.level   = info.level   || 1;
  character.hp      = info.hp      || 100;
  character.maxHp   = info.maxHp   || 100;
  character.exp     = Number(toId(info.exp))          || 0;
  character.nextExp = Number(toId(info.nextLevelExp)) || 100;
  character.atk     = info.attack  || 15;
  character.def     = info.defense || 5;
  me.x = info.x || 100;
  me.y = info.y || 100;

  if (me.id !== '0') {
    showScreen('lobby');
    requestLobbyRoomList();
  }
}

// ─────────────────────────────────────────────────────
// 로비
// ─────────────────────────────────────────────────────
function requestLobbyRoomList() {
  send({ reqRoomList: {} });
}

function onResRoomList(res) {
  rooms.clear();
  (res.rooms || []).forEach(r => {
    const id = toId(r.roomId);
    rooms.set(id, {
      id,
      playerCount: r.playerCount || 0,
      maxPlayers:  r.maxPlayers  || 2,
      isStarted:   r.isStarted   || false,
    });
  });
  renderRoomList();
}

function renderRoomList() {
  const container = document.getElementById('room-list');
  container.innerHTML = '';

  if (rooms.size === 0) {
    container.innerHTML = '<div style="color:#666;padding:20px;text-align:center">방이 없습니다 — 새 방을 만들어보세요</div>';
    return;
  }

  rooms.forEach(room => {
    const full    = room.playerCount >= room.maxPlayers;
    const started = room.isStarted;
    const div = document.createElement('div');
    div.className = 'room-item';
    div.innerHTML = `
      <div class="room-item-info">
        <div class="room-item-id">ID: ${room.id}</div>
        <div class="room-item-count">${room.playerCount} / ${room.maxPlayers}명</div>
        ${started ? '<div class="room-item-started">게임 중</div>' : ''}
      </div>
      <button class="btn-secondary" ${started || full ? 'disabled' : ''}
        onclick="joinRoom('${room.id}')">입장</button>`;
    container.appendChild(div);
  });
}

function createRoom() { send({ reqCreateRoom: {} }); }

function joinRoom(id) {
  // uint64는 Long으로 전송해야 하지만, protobuf.js는 숫자 문자열도 처리함
  send({ reqRoomEnter: { roomId: id } });
}

function onResRoomEnter(res) {
  if (res.errorCode === EC.SUCCESS) {
    currentRoomId = toId(res.roomId);
    roomPlayers.clear();
    roomPlayers.set(me.id, { id: me.id, name: me.name, isReady: false });
    renderRoomPlayers();
    document.getElementById('room-id-label').textContent = `Room #${currentRoomId}`;
    isReady = false;
    document.getElementById('btn-ready').textContent = '준비';
    document.getElementById('btn-ready').className   = 'btn-success';
    showScreen('room');
  } else {
    const msgs = {
      [EC.ALREADY_IN_ROOM]:      '이미 방에 있습니다.',
      [EC.ROOM_NOT_FOUND]:       '방을 찾을 수 없습니다.',
      [EC.ROOM_FULL]:            '방이 가득 찼습니다.',
      [EC.ROOM_ALREADY_STARTED]: '이미 게임이 시작된 방입니다.',
    };
    setStatus('lobby-status', msgs[res.errorCode] || `입장 실패 (${res.errorCode})`);
  }
}

// ─────────────────────────────────────────────────────
// 룸 대기실
// ─────────────────────────────────────────────────────
function onNotiRoomEnter(noti) {
  const pid = toId(noti.playerId);
  roomPlayers.set(pid, { id: pid, name: noti.playerName || '', isReady: false });
  renderRoomPlayers();
}

function onNotiRoomExit(noti) {
  roomPlayers.delete(toId(noti.playerId));
  renderRoomPlayers();
}

function onNotiReadyGame(noti) {
  const pid = toId(noti.playerId);
  const p   = roomPlayers.get(pid);
  if (p) p.isReady = noti.isReady;
  renderRoomPlayers();
}

function onNotiGameStart() {
  gamePlayers.clear();
  gameMonsters.clear(); gameGems.clear();
  // ResEnterGame이 뒤따라오면 거기서 showScreen('game')
}

function onResRoomExit() {
  currentRoomId = '0';
  roomPlayers.clear();
  showScreen('lobby');
  requestLobbyRoomList();
}

function renderRoomPlayers() {
  const container = document.getElementById('room-players');
  container.innerHTML = '';
  roomPlayers.forEach(p => {
    const div = document.createElement('div');
    div.className = 'player-slot';
    div.innerHTML = `<span>${p.name}</span>
      <span class="${p.isReady ? 'ready-badge' : 'waiting-badge'}">${p.isReady ? 'READY' : '대기'}</span>`;
    container.appendChild(div);
  });
}

function toggleReady() {
  isReady = !isReady;
  send({ reqReadyGame: {} });
  const btn = document.getElementById('btn-ready');
  btn.textContent = isReady ? '준비 취소' : '준비';
  btn.className   = isReady ? 'btn-danger' : 'btn-success';
}

function exitRoom() {
  isReady = false;
  send({ reqRoomExit: {} });
}

// ─────────────────────────────────────────────────────
// 게임 인게임
// ─────────────────────────────────────────────────────
function onResEnterGame(res) {
  gamePlayers.clear();
  gameMonsters.clear(); gameGems.clear();

  (res.players || []).forEach(p => {
    const pid = toId(p.playerId);
    gamePlayers.set(pid, {
      id: pid, name: p.name || '',
      x: p.x || 0, y: p.y || 0,
      hp: p.hp || 0, maxHp: p.maxHp || 1,
      level: p.level || 1, alive: true,
    });
    if (pid === me.id) { me.x = p.x; me.y = p.y; }
  });

  (res.monsters || []).forEach(m => {
    const mid = toId(m.monsterId);
    gameMonsters.set(mid, {
      id: mid, type: m.monsterType || 0,
      x: m.x || 0, y: m.y || 0,
      hp: m.hp || 0, maxHp: m.maxHp || 1,
      alive: true,
    });
  });

  updateHud();
  showScreen('game');
  addChat('시스템', '게임 시작! WASD 이동 · 몬스터 클릭 공격');
}

function onNotiMove(noti) {
  const pid = toId(noti.playerId);
  const p   = gamePlayers.get(pid);
  if (p) { p.x = noti.x; p.y = noti.y; }
  // 다른 플레이어 위치는 서버 권위를 따름; 자신은 클라이언트 예측 유지
  // (서버 위치와 크게 어긋날 경우만 보정)
  if (pid === me.id) {
    const dx = noti.x - me.x, dy = noti.y - me.y;
    if (dx * dx + dy * dy > 10000) { me.x = noti.x; me.y = noti.y; }
  }
}

function onNotiHpChange(noti) {
  const id = toId(noti.entityId);
  if (noti.isMonster) {
    const m = gameMonsters.get(id);
    if (m) { m.hp = noti.hp; m.maxHp = noti.maxHp; }
  } else {
    const p = gamePlayers.get(id);
    if (p) { p.hp = noti.hp; p.maxHp = noti.maxHp; }
    if (id === me.id) { character.hp = noti.hp; character.maxHp = noti.maxHp; updateHud(); }
  }
}

function onNotiDeath(noti) {
  const id = toId(noti.entityId);
  if (noti.isMonster) {
    const m = gameMonsters.get(id);
    if (m) { m.alive = false; m.hp = 0; }
  } else {
    const p = gamePlayers.get(id);
    if (p) { p.alive = false; p.hp = 0; }
    if (id === me.id) { character.hp = 0; updateHud(); }
  }
}

function onNotiRespawn(noti) {
  const mid = toId(noti.monsterId);
  const m   = gameMonsters.get(mid);
  if (m) { m.alive = true; m.hp = noti.hp; m.x = noti.x; m.y = noti.y; }
}

function onNotiSpawnMonster(noti) {
  const mid = toId(noti.monsterId);
  gameMonsters.set(mid, {
    id: mid, type: noti.monsterType || 0,
    x: noti.x || 0, y: noti.y || 0,
    hp: noti.hp || 0, maxHp: noti.maxHp || 0, alive: true,
  });
}

function onNotiWaveStart(noti) {
  waveNumber = noti.waveNumber || 0;
  showWaveBanner(`Wave ${waveNumber}`);
  updateWaveHud();
}

function showWaveBanner(text) {
  const el = document.getElementById('wave-banner');
  if (!el) return;
  el.textContent = text;
  el.style.opacity = '1';
  setTimeout(() => { el.style.opacity = '0'; }, 2000);
}

function updateWaveHud() {
  const el = document.getElementById('hud-wave');
  if (el) el.textContent = `Wave ${waveNumber}`;
}

function onNotiSurvivalTime(noti) {
  const sec = noti.elapsedSeconds || 0;
  const mm  = String(Math.floor(sec / 60)).padStart(2, '0');
  const ss  = String(sec % 60).padStart(2, '0');
  const el  = document.getElementById('hud-survival');
  if (el) el.textContent = `${mm}:${ss}`;
}

function onNotiWeaponChoice(noti) {
  const choices = noti.choices || [];
  if (choices.length === 0) return;

  const overlay = document.getElementById('weapon-choice-overlay');
  const list    = document.getElementById('weapon-choice-list');
  if (!overlay || !list) return;

  list.innerHTML = '';
  choices.forEach(c => {
    const btn = document.createElement('button');
    btn.className   = 'weapon-choice-btn';
    btn.textContent = c.isUpgrade
      ? `${c.name} Lv.${c.nextLevel} (업그레이드)`
      : `${c.name} (신규)`;
    btn.onclick = () => {
      send({ reqChooseWeapon: { weaponId: c.weaponId } });
      overlay.classList.add('hidden');
    };
    list.appendChild(btn);
  });

  overlay.classList.remove('hidden');
}

function onNotiGemSpawn(noti) {
  const gid = toId(noti.gemId);
  gameGems.set(gid, { id: gid, x: noti.x, y: noti.y, expValue: noti.expValue, spawnTime: performance.now() });
}

function onNotiGemCollect(noti) {
  gameGems.delete(toId(noti.gemId));
  // 수집 이펙트 — 젬 위치 → 플레이어 위치
  const p = gamePlayers.get(toId(noti.playerId));
  if (p) spawnEffect(noti.x ?? p.x, noti.y ?? p.y, p.x, p.y, '#f1c40f', 300);
}

// 배치 몬스터 이동 — 서버 100ms 틱에서 수신. 목표 위치를 lerp 타깃으로 설정.
function onNotiMonsterMove(noti) {
  if (!noti || !noti.moves) return;
  const now = performance.now();
  for (const info of noti.moves) {
    const m = gameMonsters.get(toId(info.monsterId));
    if (!m || !m.alive) continue;
    // 보간 타깃 설정 (렌더러가 lerp로 부드럽게 이동시킴)
    m.targetX    = info.x;
    m.targetY    = info.y;
    m.lerpStart  = now;
    m.lerpFromX  = m.x;
    m.lerpFromY  = m.y;
  }
}

function onNotiCombat(noti) {
  const attacker = gamePlayers.get(toId(noti.attackerPlayerId));
  const target   = gameMonsters.get(toId(noti.targetMonsterId));
  if (!attacker || !target) return;
  spawnEffect(attacker.x, attacker.y, target.x, target.y, '#f1c40f', 250);
}

// 원거리 투사체를 쏘는 몬스터 타입 (Ghost=6, Reaper=8)
const RANGED_MONSTER_TYPES = new Set([6, 8]);

function onNotiMonsterAttack(noti) {
  const attacker = gameMonsters.get(toId(noti.monsterId));
  const target   = gamePlayers.get(toId(noti.targetPlayerId));
  if (!attacker || !target) return;
  if (RANGED_MONSTER_TYPES.has(attacker.type)) {
    // 원거리: 투사체 이펙트
    spawnEffect(attacker.x, attacker.y, target.x, target.y, '#e74c3c', 250);
  } else {
    // 근접: 타깃 위치에 히트 플래시
    effects.push({ type: 'flash', x: target.x, y: target.y, color: '#e74c3c', duration: 180, startTime: performance.now() });
  }
}

function spawnEffect(sx, sy, tx, ty, color, duration) {
  effects.push({ sx, sy, tx, ty, color, duration, startTime: performance.now() });
}

function onNotiExpGain(noti) {
  if (toId(noti.playerId) === me.id) {
    character.exp     = Number(toId(noti.totalExp))    || 0;
    character.nextExp = Number(toId(noti.nextLevelExp)) || 100;
    updateHud();
  }
}

function onNotiLevelUp(noti) {
  if (toId(noti.playerId) === me.id) {
    character.level = noti.newLevel;
    character.maxHp = noti.newMaxHp;
    character.atk   = noti.newAttack;
    character.def   = noti.newDefense;
    addChat('시스템', `레벨 업! Lv.${character.level}`);
    updateHud();
  }
}

function onNotiGameEnd(noti) {
  stopGameLoop();
  const isClear = noti.isClear;
  const survived = noti.survivedSeconds || 0;
  const mm = String(Math.floor(survived / 60)).padStart(2, '0');
  const ss = String(survived % 60).padStart(2, '0');

  document.getElementById('result-title').textContent = isClear ? '클리어!' : '전멸...';
  document.getElementById('result-title').className   = 'result-title ' + (isClear ? 'clear' : 'fail');
  document.getElementById('result-level').textContent = `Lv.${character.level}`;
  document.getElementById('result-exp').textContent   = `${character.exp} / ${character.nextExp}`;

  const survEl = document.getElementById('result-survived');
  if (survEl) survEl.textContent = `생존 시간: ${mm}:${ss}`;

  const waveEl = document.getElementById('result-wave');
  if (waveEl) waveEl.textContent = `도달 웨이브: ${waveNumber}`;

  showScreen('result');
}

function onNotiGameChat(noti) {
  addChat(noti.playerName || '?', noti.message || '');
}

function addChat(name, msg) {
  const log = document.getElementById('chat-log');
  if (!log) return;
  const line = document.createElement('div');
  line.textContent = `[${name}] ${msg}`;
  log.appendChild(line);
  log.scrollTop = log.scrollHeight;
}

// ─────────────────────────────────────────────────────
// HUD
// ─────────────────────────────────────────────────────
function updateHud() {
  const set = (id, v) => { const el = document.getElementById(id); if (el) el.textContent = v; };
  set('hud-name',  me.name);
  set('hud-level', character.level);
  set('hud-hp',    `${character.hp}/${character.maxHp}`);
  set('hud-exp',   `${character.exp}/${character.nextExp}`);
  set('hud-atk',   character.atk);
  set('hud-def',   character.def);
}

// ─────────────────────────────────────────────────────
// 캔버스 렌더링
// ─────────────────────────────────────────────────────
// 인덱스 = MonsterType enum 값 (Slime=0 … Reaper=8)
const MONSTER_COLORS = ['#27ae60', '#8e44ad', '#c0392b', '#7f8c8d', '#2ecc71', '#bdc3c7', '#1abc9c', '#d35400', '#8e0000'];
const MONSTER_NAMES  = ['슬라임', '오크', '드래곤', '박쥐', '좀비', '스켈레톤', '유령', '거대좀비', '리퍼'];
const MONSTER_SIZES  = [24, 28, 48, 18, 26, 26, 22, 48, 40]; // 이모지 렌더 기준 px (index = type)
const MONSTER_EMOJI  = ['🐛', '👹', '🐉', '🦇', '🧟', '💀', '👻', '🧟', '☠️'];

// 카메라 오프셋 — 내 플레이어를 뷰포트 중앙에 고정 (클램프 없음 → 무한 순환)
function getCameraOffset() {
  return {
    x: me.x - CANVAS_W / 2,
    y: me.y - CANVAS_H / 2,
  };
}

// 월드 좌표 → 화면 좌표 변환 (wrap-aware: 카메라에서 가장 가까운 복사본 사용)
function toScreen(wx, wy, cam) {
  const camCX = cam.x + CANVAS_W / 2;
  const camCY = cam.y + CANVAS_H / 2;
  // 맵 순환 기준으로 카메라 중심에 가장 가까운 위치 선택
  let dx = ((wx - camCX) % MAP_W + MAP_W + MAP_W / 2) % MAP_W - MAP_W / 2;
  let dy = ((wy - camCY) % MAP_H + MAP_H + MAP_H / 2) % MAP_H - MAP_H / 2;
  return { x: camCX + dx - cam.x, y: camCY + dy - cam.y };
}

function render() {
  const canvas = document.getElementById('game-canvas');
  if (!canvas) return;
  const ctx = canvas.getContext('2d');
  const cam = getCameraOffset();

  ctx.fillStyle = '#0d1117';
  ctx.fillRect(0, 0, CANVAS_W, CANVAS_H);

  // 격자 (카메라 오프셋 적용)
  ctx.strokeStyle = '#1a2332';
  ctx.lineWidth   = 1;
  const startX = Math.floor(cam.x / 40) * 40;
  const startY = Math.floor(cam.y / 40) * 40;
  for (let wx = startX; wx < cam.x + CANVAS_W + 40; wx += 40) {
    const sx = wx - cam.x;
    ctx.beginPath(); ctx.moveTo(sx, 0); ctx.lineTo(sx, CANVAS_H); ctx.stroke();
  }
  for (let wy = startY; wy < cam.y + CANVAS_H + 40; wy += 40) {
    const sy = wy - cam.y;
    ctx.beginPath(); ctx.moveTo(0, sy); ctx.lineTo(CANVAS_W, sy); ctx.stroke();
  }

  // 맵 경계 — 어두운 선으로만 표시 (게임 월드 느낌 유지)
  ctx.strokeStyle = '#2a3f5f';
  ctx.lineWidth   = 2;
  ctx.strokeRect(-cam.x, -cam.y, MAP_W, MAP_H);

  gameGems.forEach(g     => drawGem(ctx, g, cam));
  gameMonsters.forEach(m => drawMonster(ctx, m, cam));
  gamePlayers.forEach(p  => drawPlayer(ctx, p, cam));
  drawEffects(ctx, cam);
}

function drawEffects(ctx, cam) {
  const now = performance.now();
  for (let i = effects.length - 1; i >= 0; i--) {
    const e = effects[i];
    const t = (now - e.startTime) / e.duration; // 0.0 → 1.0
    if (t >= 1) { effects.splice(i, 1); continue; }

    if (e.type === 'flash') {
      // 근접 히트 플래시 — 팽창하며 사라지는 원
      const s = toScreen(e.x, e.y, cam);
      ctx.globalAlpha = (1 - t) * 0.8;
      ctx.strokeStyle = e.color;
      ctx.lineWidth   = 2;
      ctx.beginPath();
      ctx.arc(s.x, s.y, 6 + t * 14, 0, Math.PI * 2);
      ctx.stroke();
      ctx.lineWidth   = 1;
      ctx.globalAlpha = 1;
      continue;
    }

    // 투사체 위치 (선형 보간) + 카메라 오프셋
    const wx = e.sx + (e.tx - e.sx) * t;
    const wy = e.sy + (e.ty - e.sy) * t;
    const s  = toScreen(wx, wy, cam);

    ctx.globalAlpha = 1 - t;
    ctx.fillStyle   = e.color;
    ctx.beginPath();
    ctx.arc(s.x, s.y, 5, 0, Math.PI * 2);
    ctx.fill();
    ctx.globalAlpha = 1;
  }
}

// SVG 스프라이트 or 이모지 폴백으로 중앙에 그리기
function _drawSprite(ctx, name, cx, cy, size, emojiFallback) {
  const img = sprites[name];
  if (img) {
    ctx.drawImage(img, cx - size / 2, cy - size / 2, size, size);
  } else {
    ctx.font         = `${size}px serif`;
    ctx.textAlign    = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(emojiFallback, cx, cy);
    ctx.textBaseline = 'alphabetic';
  }
}

function drawHpBar(ctx, sx, sy, w, hp, maxHp) {
  const ratio = maxHp > 0 ? Math.max(0, hp / maxHp) : 0;
  ctx.fillStyle = '#333';
  ctx.fillRect(sx - w / 2, sy, w, 5);
  ctx.fillStyle = ratio > 0.5 ? '#27ae60' : ratio > 0.25 ? '#f39c12' : '#e74c3c';
  ctx.fillRect(sx - w / 2, sy, w * ratio, 5);
}

function drawPlayer(ctx, p, cam) {
  const isMe  = p.id === me.id;
  const size  = 32;
  const s     = toScreen(p.x, p.y, cam);

  if (!p.alive) {
    ctx.globalAlpha = 0.35;
    _drawSprite(ctx, isMe ? 'player-me' : 'player', s.x, s.y, size, isMe ? '🧙' : '🧝');
    ctx.globalAlpha = 1;
    ctx.fillStyle   = '#7f8c8d';
    ctx.font        = '11px sans-serif';
    ctx.textAlign   = 'center';
    ctx.fillText('사망', s.x, s.y - size / 2 - 4);
    return;
  }

  // 내 플레이어 강조 링
  if (isMe) {
    ctx.save();
    ctx.strokeStyle = '#60a5fa';
    ctx.lineWidth   = 2;
    ctx.shadowColor = '#3b82f6';
    ctx.shadowBlur  = 12;
    ctx.beginPath();
    ctx.arc(s.x, s.y, size / 2 + 4, 0, Math.PI * 2);
    ctx.stroke();
    ctx.restore();
  }

  // SVG 스프라이트 (없으면 이모지 폴백)
  _drawSprite(ctx, isMe ? 'player-me' : 'player', s.x, s.y, size, isMe ? '🧙' : '🧝');

  drawHpBar(ctx, s.x, s.y - size / 2 - 10, 44, p.hp, p.maxHp);

  ctx.fillStyle = isMe ? '#a8dadc' : '#c0b4e0';
  ctx.font      = '11px sans-serif';
  ctx.textAlign = 'center';
  ctx.fillText(`${p.name} Lv.${p.level}`, s.x, s.y - size / 2 - 14);
}

function drawGem(ctx, g, cam) {
  // 등장 펄스 애니메이션 (0.3초)
  const age   = (performance.now() - g.spawnTime) / 300;
  const scale = age < 1 ? 0.5 + age * 0.5 : 1;
  // 획득 가능 펄스 (1초 주기 크기 변동)
  const pulse = 1 + Math.sin(performance.now() / 400) * 0.08;
  const size  = Math.round(18 * scale * pulse);
  const s     = toScreen(g.x, g.y, cam);

  ctx.save();
  ctx.shadowColor = '#f1c40f';
  ctx.shadowBlur  = 10;
  ctx.font         = `${size}px serif`;
  ctx.textAlign    = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText('💎', s.x, s.y);
  ctx.textBaseline = 'alphabetic';
  ctx.restore();
}

// 맵 순환을 고려한 lerp — 절반 맵 기준 최단 방향으로 보간
function lerpWrap(from, to, t, mapSize) {
  let delta = to - from;
  if (delta >  mapSize / 2) delta -= mapSize;
  else if (delta < -mapSize / 2) delta += mapSize;
  return ((from + t * delta) % mapSize + mapSize) % mapSize;
}

function drawMonster(ctx, m, cam) {
  if (!m.alive) return;

  // 이동 보간 (NotiMonsterMove lerp) — wrap-aware 최단 경로 보간
  if (m.targetX !== undefined) {
    const LERP_MS = 120;
    const t = Math.min(1, (performance.now() - m.lerpStart) / LERP_MS);
    m.x = lerpWrap(m.lerpFromX, m.targetX, t, MAP_W);
    m.y = lerpWrap(m.lerpFromY, m.targetY, t, MAP_H);
    if (t >= 1) {
      m.x = m.targetX; m.y = m.targetY;
      delete m.targetX; delete m.targetY;
    }
  }

  const emoji  = MONSTER_EMOJI[m.type]  ?? '👾';
  const name   = MONSTER_NAMES[m.type]  ?? '몬스터';
  const size   = MONSTER_SIZES[m.type]  ?? 26;
  const radius = size / 2;
  const s      = toScreen(m.x, m.y, cam);
  const isBoss = m.type === 2 || m.type === 8; // Dragon, Reaper

  // 보스 몬스터 글로우 링
  if (isBoss) {
    ctx.save();
    ctx.strokeStyle = m.type === 8 ? '#8e0000' : '#c0392b';
    ctx.lineWidth   = 2;
    ctx.shadowColor = ctx.strokeStyle;
    ctx.shadowBlur  = 14;
    ctx.beginPath();
    ctx.arc(s.x, s.y, radius + 4, 0, Math.PI * 2);
    ctx.stroke();
    ctx.restore();
  }

  // SVG 스프라이트 (없으면 이모지 폴백)
  _drawSprite(ctx, SPRITE_NAMES[m.type] ?? 'slime', s.x, s.y, size, emoji);

  drawHpBar(ctx, s.x, s.y - radius - 10, 50, m.hp, m.maxHp);

  ctx.fillStyle = isBoss ? '#ef4444' : '#ccc';
  ctx.font      = `${isBoss ? 'bold ' : ''}11px sans-serif`;
  ctx.textAlign = 'center';
  ctx.fillText(name, s.x, s.y - radius - 14);
}

// ─────────────────────────────────────────────────────
// 게임 루프
// ─────────────────────────────────────────────────────
function startGameLoop() {
  if (animId) return;
  lastAttack = 0;
  lastSentX = me.x; lastSentY = me.y;
  document.addEventListener('keydown', onKeyDown);
  document.addEventListener('keyup',   onKeyUp);
  const canvas = document.getElementById('game-canvas');
  if (canvas) canvas.addEventListener('click', onCanvasClick);

  function loop(ts) {
    handleMovement(ts);
    handleAutoAttack(ts);
    render();
    animId = requestAnimationFrame(loop);
  }
  animId = requestAnimationFrame(loop);
}

function stopGameLoop() {
  if (animId) { cancelAnimationFrame(animId); animId = null; }
  document.removeEventListener('keydown', onKeyDown);
  document.removeEventListener('keyup',   onKeyUp);
  const canvas = document.getElementById('game-canvas');
  if (canvas) canvas.removeEventListener('click', onCanvasClick);
}

function onKeyDown(e) { if (e.target.tagName !== 'INPUT') keys[e.code] = true; }
function onKeyUp(e)   { keys[e.code] = false; }

function handleMovement(ts) {
  const myPlayer = gamePlayers.get(me.id);
  if (!myPlayer || !myPlayer.alive) return;

  const speed = 5; // 픽셀/프레임 (60fps ≈ 300px/s)
  let nx = me.x, ny = me.y;
  let moving = false;
  if (keys['KeyW'] || keys['ArrowUp'])    { ny -= speed; moving = true; }
  if (keys['KeyS'] || keys['ArrowDown'])  { ny += speed; moving = true; }
  if (keys['KeyA'] || keys['ArrowLeft'])  { nx -= speed; moving = true; }
  if (keys['KeyD'] || keys['ArrowRight']) { nx += speed; moving = true; }

  if (!moving) {
    // 키에서 손을 뗀 순간 — 서버 위치가 클라이언트와 다를 경우 즉시 sync
    if (me.x !== lastSentX || me.y !== lastSentY) {
      lastSentX = me.x; lastSentY = me.y;
      send({ reqMove: { x: me.x, y: me.y } });
    }
    return;
  }

  // 맵 순환 wrap-around
  nx = ((nx % MAP_W) + MAP_W) % MAP_W;
  ny = ((ny % MAP_H) + MAP_H) % MAP_H;

  // 클라이언트 예측 — 즉시 로컬 위치 업데이트 (서버 응답 대기 없음)
  me.x = nx; me.y = ny;
  myPlayer.x = nx; myPlayer.y = ny;

  // 서버에는 throttle해서 전송
  if (ts - lastMove >= MOVE_SEND_INTERVAL) {
    lastMove = ts;
    lastSentX = nx; lastSentY = ny;
    send({ reqMove: { x: nx, y: ny } });
  }
}

function handleAutoAttack(ts) {
  if (ts - lastAttack < ATTACK_INTERVAL) return;

  const myPlayer = gamePlayers.get(me.id);
  if (!myPlayer || !myPlayer.alive) return;

  let target = null, minDist = Infinity;
  gameMonsters.forEach(m => {
    if (!m.alive) return;
    const dx = me.x - m.x, dy = me.y - m.y;
    const d  = Math.sqrt(dx * dx + dy * dy);
    if (d < minDist) { minDist = d; target = m; }
  });

  if (!target) return;

  lastAttack = ts;
  send({ reqAttack: { targetMonsterId: target.id } });
}

function onCanvasClick(e) {
  const canvas = document.getElementById('game-canvas');
  const rect   = canvas.getBoundingClientRect();
  const cx     = e.clientX - rect.left;
  const cy     = e.clientY - rect.top;

  // 스크린 좌표 → 월드 좌표
  const cam = getCameraOffset();
  const wx  = cx + cam.x;
  const wy  = cy + cam.y;

  let target = null, minDist = Infinity;
  gameMonsters.forEach(m => {
    if (!m.alive) return;
    const radius = (MONSTER_SIZES[m.type] ?? 24) / 2;
    const dx = wx - m.x, dy = wy - m.y;
    const d  = Math.sqrt(dx * dx + dy * dy);
    if (d < radius + 12 && d < minDist) { minDist = d; target = m; }
  });

  if (target) send({ reqAttack: { targetMonsterId: target.id } });
}

// ─────────────────────────────────────────────────────
// 채팅
// ─────────────────────────────────────────────────────
function sendGameChat() {
  const input = document.getElementById('chat-input');
  const msg   = input.value.trim();
  if (!msg) return;
  send({ reqGameChat: { message: msg } });
  input.value = '';
}

// ─────────────────────────────────────────────────────
// 결과 화면
// ─────────────────────────────────────────────────────
function backToLobby() {
  send({ reqRoomExit: {} });
  currentRoomId = '0';
  gamePlayers.clear();
  gameMonsters.clear(); gameGems.clear();
  showScreen('lobby');
  requestLobbyRoomList();
}

// ─────────────────────────────────────────────────────
// 로그인 / 등록 UI
// ─────────────────────────────────────────────────────
function switchTab(tab) {
  document.getElementById('tab-login').classList.toggle('active',    tab === 'login');
  document.getElementById('tab-register').classList.toggle('active', tab === 'register');
  document.getElementById('form-login').classList.toggle('hidden',    tab !== 'login');
  document.getElementById('form-register').classList.toggle('hidden', tab !== 'register');
}

function doLogin() {
  const user = document.getElementById('login-user').value.trim();
  const pass = document.getElementById('login-pass').value.trim();
  if (!user || !pass) { setStatus('login-status', '아이디와 비밀번호를 입력해주세요.'); return; }
  setStatus('login-status', '연결 중...');
  connect(() => send({ reqLogin: { username: user, password: pass } }));
}

function doRegister() {
  const user = document.getElementById('reg-user').value.trim();
  const pass = document.getElementById('reg-pass').value.trim();
  if (!user || !pass) { setStatus('login-status', '아이디와 비밀번호를 입력해주세요.'); return; }
  setStatus('login-status', '연결 중...');
  connect(() => send({ reqRegister: { username: user, password: pass } }));
}

// ─────────────────────────────────────────────────────
// 초기화
// ─────────────────────────────────────────────────────
window.addEventListener('DOMContentLoaded', async () => {
  buildSprites();
  await initProto();
  showScreen('login');
});

// HTML onclick에서 호출되는 전역 함수
Object.assign(window, {
  switchTab, doLogin, doRegister,
  createRoom, joinRoom,
  toggleReady, exitRoom,
  sendGameChat, backToLobby,
  requestLobbyRoomList,
});
