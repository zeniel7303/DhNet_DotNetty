'use strict';

// ─────────────────────────────────────────────────────
// 설정
// ─────────────────────────────────────────────────────
const WS_URL        = 'ws://localhost:7778/ws';
const CANVAS_W      = 800;
const CANVAS_H      = 600;
const MOVE_INTERVAL = 100; // ms

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

const keys     = {};
let   lastMove = 0;
let   animId   = null;
let   isReady  = false;

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
  gameMonsters.clear();
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
  gameMonsters.clear();

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
  if (pid === me.id) { me.x = noti.x; me.y = noti.y; }
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
  document.getElementById('result-title').textContent = isClear ? '🏆 클리어!' : '💀 전멸...';
  document.getElementById('result-title').className   = 'result-title ' + (isClear ? 'clear' : 'fail');
  document.getElementById('result-level').textContent = `Lv.${character.level}`;
  document.getElementById('result-exp').textContent   = `${character.exp} / ${character.nextExp}`;
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
const MONSTER_COLORS = ['#27ae60', '#8e44ad', '#c0392b'];
const MONSTER_NAMES  = ['슬라임', '오크', '드래곤'];

function render() {
  const canvas = document.getElementById('game-canvas');
  if (!canvas) return;
  const ctx = canvas.getContext('2d');

  ctx.fillStyle = '#0d1117';
  ctx.fillRect(0, 0, CANVAS_W, CANVAS_H);

  ctx.strokeStyle = '#1a2332';
  ctx.lineWidth   = 1;
  for (let x = 0; x < CANVAS_W; x += 40) { ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, CANVAS_H); ctx.stroke(); }
  for (let y = 0; y < CANVAS_H; y += 40) { ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(CANVAS_W, y); ctx.stroke(); }

  gameMonsters.forEach(m => drawMonster(ctx, m));
  gamePlayers.forEach(p  => drawPlayer(ctx, p));
}

function drawHpBar(ctx, x, y, w, hp, maxHp) {
  const ratio = maxHp > 0 ? Math.max(0, hp / maxHp) : 0;
  ctx.fillStyle = '#333';
  ctx.fillRect(x - w / 2, y, w, 5);
  ctx.fillStyle = ratio > 0.5 ? '#27ae60' : ratio > 0.25 ? '#f39c12' : '#e74c3c';
  ctx.fillRect(x - w / 2, y, w * ratio, 5);
}

function drawPlayer(ctx, p) {
  const isMe  = p.id === me.id;
  const size  = 20;

  if (!p.alive) {
    ctx.globalAlpha = 0.3;
    ctx.fillStyle   = '#7f8c8d';
    ctx.fillRect(p.x - size / 2, p.y - size / 2, size, size);
    ctx.globalAlpha = 1;
    ctx.fillStyle   = '#e0e0e0';
    ctx.font        = '11px sans-serif';
    ctx.textAlign   = 'center';
    ctx.fillText('💀', p.x, p.y + 4);
    return;
  }

  ctx.fillStyle = isMe ? '#3498db' : '#9b59b6';
  ctx.fillRect(p.x - size / 2, p.y - size / 2, size, size);

  if (isMe) {
    ctx.strokeStyle = '#fff';
    ctx.lineWidth   = 1.5;
    ctx.strokeRect(p.x - size / 2, p.y - size / 2, size, size);
  }

  drawHpBar(ctx, p.x, p.y - size / 2 - 10, 44, p.hp, p.maxHp);

  ctx.fillStyle = '#e0e0e0';
  ctx.font      = '11px sans-serif';
  ctx.textAlign = 'center';
  ctx.fillText(`${p.name} Lv.${p.level}`, p.x, p.y - size / 2 - 14);
}

function drawMonster(ctx, m) {
  if (!m.alive) return;
  const color = MONSTER_COLORS[m.type] ?? '#e74c3c';
  const name  = MONSTER_NAMES[m.type]  ?? '몬스터';
  const size  = m.type === 2 ? 40 : 24;

  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.arc(m.x, m.y, size / 2, 0, Math.PI * 2);
  ctx.fill();

  drawHpBar(ctx, m.x, m.y - size / 2 - 10, 50, m.hp, m.maxHp);

  ctx.fillStyle = '#fff';
  ctx.font      = '11px sans-serif';
  ctx.textAlign = 'center';
  ctx.fillText(name, m.x, m.y - size / 2 - 14);
}

// ─────────────────────────────────────────────────────
// 게임 루프
// ─────────────────────────────────────────────────────
function startGameLoop() {
  if (animId) return;
  document.addEventListener('keydown', onKeyDown);
  document.addEventListener('keyup',   onKeyUp);
  const canvas = document.getElementById('game-canvas');
  if (canvas) canvas.addEventListener('click', onCanvasClick);

  function loop(ts) {
    handleMovement(ts);
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
  if (ts - lastMove < MOVE_INTERVAL) return;
  lastMove = ts;

  const myPlayer = gamePlayers.get(me.id);
  if (!myPlayer || !myPlayer.alive) return;

  const speed = 5;
  let nx = me.x, ny = me.y;
  if (keys['KeyW'] || keys['ArrowUp'])    ny -= speed;
  if (keys['KeyS'] || keys['ArrowDown'])  ny += speed;
  if (keys['KeyA'] || keys['ArrowLeft'])  nx -= speed;
  if (keys['KeyD'] || keys['ArrowRight']) nx += speed;

  if (nx === me.x && ny === me.y) return;

  nx = Math.max(10, Math.min(CANVAS_W - 10, nx));
  ny = Math.max(10, Math.min(CANVAS_H - 10, ny));
  send({ reqMove: { x: nx, y: ny } });
}

function onCanvasClick(e) {
  const canvas = document.getElementById('game-canvas');
  const rect   = canvas.getBoundingClientRect();
  const cx = e.clientX - rect.left;
  const cy = e.clientY - rect.top;

  let target = null, minDist = Infinity;
  gameMonsters.forEach(m => {
    if (!m.alive) return;
    const size = m.type === 2 ? 40 : 24;
    const dx = cx - m.x, dy = cy - m.y;
    const d  = Math.sqrt(dx * dx + dy * dy);
    if (d < size / 2 + 12 && d < minDist) { minDist = d; target = m; }
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
  gameMonsters.clear();
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
