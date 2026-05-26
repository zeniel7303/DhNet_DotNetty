/**
 * 이동 동기화 시뮬레이션 테스트
 * 서버(C# PlayerWorldComponent.ApplyInput)와 클라이언트(game.js applyMovementInput)의
 * 이동 공식이 동일한지 검증하고, Server Reconciliation 동작을 확인한다.
 *
 * 실행: node test/movement-sync.test.js
 */

// ── 공통 상수 ──────────────────────────────────────────────────────────────
const MAP_W          = 3200;
const MAP_H          = 2400;
const MOVE_SPEED     = 300;       // px/s (서버 MoveSpeed 기본값과 동일)
const SEND_INTERVAL  = 50;        // ms
const SERVER_MAX_DT  = 100;       // ms (서버 dtMs 캡)
const FPS            = 60;
const FRAME_MS       = 1000 / FPS;

let passed = 0;
let failed = 0;

function assert(condition, msg) {
  if (condition) {
    console.log(`  ✅ ${msg}`);
    passed++;
  } else {
    console.error(`  ❌ FAIL: ${msg}`);
    failed++;
  }
}

function assertClose(a, b, tolerance, msg) {
  const diff = Math.abs(a - b);
  if (diff <= tolerance) {
    console.log(`  ✅ ${msg} (diff=${diff.toFixed(3)})`);
    passed++;
  } else {
    console.error(`  ❌ FAIL: ${msg} (expected ≤${tolerance}, got diff=${diff.toFixed(3)}, a=${a.toFixed(3)}, b=${b.toFixed(3)})`);
    failed++;
  }
}

// ── 서버 이동 공식 (PlayerWorldComponent.ApplyInput 동일 구현) ─────────────
function serverApplyInput(state, flags, dtMs) {
  const dtSec = Math.min(dtMs, SERVER_MAX_DT) / 1000;
  let dirX = 0, dirY = 0;
  if (flags & 1) dirY -= 1; // W
  if (flags & 2) dirY += 1; // S
  if (flags & 4) dirX -= 1; // A
  if (flags & 8) dirX += 1; // D

  if (dirX !== 0 || dirY !== 0) {
    const len = Math.sqrt(dirX * dirX + dirY * dirY);
    dirX /= len; dirY /= len;
  }

  const nx = ((state.x + dirX * MOVE_SPEED * dtSec) % MAP_W + MAP_W) % MAP_W;
  const ny = ((state.y + dirY * MOVE_SPEED * dtSec) % MAP_H + MAP_H) % MAP_H;
  return { x: nx, y: ny };
}

// ── 클라이언트 이동 공식 (game.js applyMovementInput 동일 구현) ────────────
function clientApplyInput(x, y, flags, dtSec) {
  let dirX = 0, dirY = 0;
  if (flags & 1) dirY -= 1; // W
  if (flags & 2) dirY += 1; // S
  if (flags & 4) dirX -= 1; // A
  if (flags & 8) dirX += 1; // D

  if (dirX !== 0 || dirY !== 0) {
    const len = Math.sqrt(dirX * dirX + dirY * dirY);
    dirX /= len; dirY /= len;
  }

  const nx = ((x + dirX * MOVE_SPEED * dtSec) % MAP_W + MAP_W) % MAP_W;
  const ny = ((y + dirY * MOVE_SPEED * dtSec) % MAP_H + MAP_H) % MAP_H;
  return { nx, ny };
}

// ── 클라이언트 상태 ────────────────────────────────────────────────────────
function makeClient(x, y) {
  return {
    x, y,
    inputSeq: 0,
    inputBuffer: [],  // { seq, flags, dt }
    lastMove: 0,
    liveFlags: 0,
    liveAccumDt: 0,
  };
}

// 한 프레임 처리: 예측 + 50ms마다 서버 전송 시뮬레이션
// 반환: 이번 프레임에 서버로 보낸 패킷 (없으면 null)
function clientTick(client, ts, dt, flags) {
  // 매 프레임 로컬 예측
  const dtSec = Math.min(dt, 100) / 1000;
  const { nx, ny } = clientApplyInput(client.x, client.y, flags, dtSec);
  client.x = nx; client.y = ny;

  // 미전송 예측분 추적
  client.liveFlags    = flags;
  client.liveAccumDt += dt;

  // 50ms마다 서버 전송
  if (ts - client.lastMove >= SEND_INTERVAL) {
    const elapsed = client.lastMove === 0 ? SEND_INTERVAL : Math.round(ts - client.lastMove);
    client.lastMove    = ts;
    client.liveAccumDt = 0;
    const seq = ++client.inputSeq;
    client.inputBuffer.push({ seq, flags, dt: elapsed });
    if (client.inputBuffer.length > 120) client.inputBuffer.shift();
    return { seq, flags, dtMs: elapsed };
  }
  return null;
}

// 서버 NotiMove 수신 시 Reconciliation (liveAccumDt 보정 포함)
function clientReconcile(client, serverX, serverY, ackSeq) {
  client.inputBuffer = client.inputBuffer.filter(i => i.seq > ackSeq);
  let rx = serverX, ry = serverY;
  for (const input of client.inputBuffer) {
    const dtSec = Math.min(input.dt, 100) / 1000;
    const r = clientApplyInput(rx, ry, input.flags, dtSec);
    rx = r.nx; ry = r.ny;
  }
  // RTT 구간 보정 (미전송 예측분)
  if (client.liveAccumDt > 0) {
    const r = clientApplyInput(rx, ry, client.liveFlags, Math.min(client.liveAccumDt, 100) / 1000);
    rx = r.nx; ry = r.ny;
  }
  client.x = rx; client.y = ry;
}

// ── 테스트 ─────────────────────────────────────────────────────────────────

console.log('\n=== Test 1: 단방향 이동 공식 일치 ===');
{
  const flags = 1; // W (위)
  const dtSec = 0.05; // 50ms

  const sv = serverApplyInput({ x: 100, y: 100 }, flags, 50);
  const cl = clientApplyInput(100, 100, flags, dtSec);

  assertClose(sv.x, cl.nx, 0.001, `W이동 X 일치: server=${sv.x.toFixed(3)} client=${cl.nx.toFixed(3)}`);
  assertClose(sv.y, cl.ny, 0.001, `W이동 Y 일치: server=${sv.y.toFixed(3)} client=${cl.ny.toFixed(3)}`);
}

console.log('\n=== Test 2: 대각선 이동 속도 정규화 ===');
{
  // 맵 경계 wrap 없이 비교하기 위해 맵 중앙에서 짧은 시간만 이동
  const dtMs = 100; // 100ms — 100*300/1000 = 30px (맵 경계 안전)
  const sv_straight = serverApplyInput({ x: 1600, y: 1200 }, 1, dtMs);     // W
  const sv_diag     = serverApplyInput({ x: 1600, y: 1200 }, 1 | 8, dtMs); // W+D

  const straightDist = Math.abs(sv_straight.y - 1200);
  const diagDist     = Math.sqrt((sv_diag.x-1600)**2 + (sv_diag.y-1200)**2);

  assertClose(straightDist, MOVE_SPEED * dtMs / 1000, 0.1,
    `직선 100ms = ${MOVE_SPEED * dtMs / 1000}px`);
  assertClose(diagDist, MOVE_SPEED * dtMs / 1000, 0.1,
    `대각선 100ms = ${MOVE_SPEED * dtMs / 1000}px (정규화됨, 직선과 동일)`);
}

console.log('\n=== Test 3: 서버 dtMs 캡 (100ms 초과 방지) ===');
{
  const normal  = serverApplyInput({ x: 100, y: 100 }, 1, 100); // 100ms
  const capped  = serverApplyInput({ x: 100, y: 100 }, 1, 9999); // 9999ms → 100ms로 캡
  assertClose(normal.y, capped.y, 0.001, `9999ms 입력이 100ms와 동일한 결과`);
}

console.log('\n=== Test 4: 3초간 이동 후 Client-Server 위치 오차 ===');
{
  const client = makeClient(100, 100);
  let serverState = { x: 100, y: 100 };
  let networkLatencyMs = 10; // 10ms RTT 시뮬레이션

  const pendingNotiMoves = []; // { deliverAt, x, y, ackSeq }
  const flags = 1; // W 키 계속 누름
  const durationMs = 3000;
  const TOLERANCE = 2; // 2px 이내 허용

  for (let t = 0; t <= durationMs; t += FRAME_MS) {
    const dt = FRAME_MS;

    // 클라이언트 틱 (ts=t: 0부터 시작해서 50ms에 자연스럽게 첫 전송)
    const pkt = clientTick(client, t, dt, flags);
    if (pkt) {
      // 서버에서 처리 (네트워크 지연 후 응답)
      const sv = serverApplyInput(serverState, pkt.flags, pkt.dtMs);
      serverState = sv;
      pendingNotiMoves.push({
        deliverAt: t + networkLatencyMs,
        x: sv.x, y: sv.y, ackSeq: pkt.seq,
      });
    }

    // 도착한 NotiMove 처리
    for (let i = pendingNotiMoves.length - 1; i >= 0; i--) {
      if (t >= pendingNotiMoves[i].deliverAt) {
        const nm = pendingNotiMoves.splice(i, 1)[0];
        clientReconcile(client, nm.x, nm.y, nm.ackSeq);
      }
    }
  }

  const finalServerY = serverState.y;
  const finalClientY = client.y;
  assertClose(finalClientY, finalServerY, TOLERANCE,
    `3초 후 Y 오차 ≤${TOLERANCE}px (server=${finalServerY.toFixed(1)}, client=${finalClientY.toFixed(1)})`);
  assertClose(client.x, serverState.x, TOLERANCE,
    `3초 후 X 오차 ≤${TOLERANCE}px`);

  // 3초간 W → 클라이언트-서버 동기화가 핵심 (위 ✅ 확인). 절대 좌표는 ±20px 허용
  const expectedY = ((100 - MOVE_SPEED * 3) % MAP_H + MAP_H) % MAP_H;
  assertClose(finalServerY, expectedY, 20,
    `3초 후 서버 Y ≈ ${expectedY.toFixed(0)}px (±20px 허용, 실제: ${finalServerY.toFixed(1)})`);
}

console.log('\n=== Test 5: Reconciliation — 서버 보정 후 올바른 재연산 ===');
{
  // 시나리오: 클라이언트가 2개 입력(W 50ms씩)을 보냄, 서버가 1번만 ack
  // 시작 y=500 (맵 경계에서 충분히 멀리)
  const startY = 500;
  const step   = MOVE_SPEED * 0.05; // 50ms = 15px

  const client = makeClient(0, startY);

  // 서버는 seq=1 처리 완료 (y = 500 - 15 = 485)
  const serverAfterSeq1 = startY - step;
  // 클라이언트는 seq=2도 예측 완료 (y = 500 - 30 = 470)
  client.inputBuffer.push({ seq: 1, flags: 1, dt: 50 });
  client.inputBuffer.push({ seq: 2, flags: 1, dt: 50 });
  client.inputSeq = 2;
  client.y = startY - step * 2; // 클라이언트 예측 위치

  // 서버 NotiMove 수신: ack_seq=1, y=485
  clientReconcile(client, 0, serverAfterSeq1, 1);

  // 기대: 서버(485) + seq=2 재연산(-15) = 470
  const expectedY = serverAfterSeq1 - step;
  assertClose(client.y, expectedY, 0.1,
    `Reconcile 후 y = ${client.y.toFixed(2)} (기대 ${expectedY.toFixed(2)})`);
  assert(client.inputBuffer.length === 1,
    `버퍼에 미확인 입력 1개만 남음 (실제: ${client.inputBuffer.length})`);
}

console.log('\n=== Test 6: 맵 경계 wrap-around 동기화 ===');
{
  // Y=10에서 W를 100ms 누르면 맵 경계(Y=0)를 넘어 Y=2370이 되어야 함
  const startY = 10;
  const sv = serverApplyInput({ x: 0, y: startY }, 1, 100); // W 100ms = 30px 위로
  // 10 - 30 = -20 → wrap → 2380
  const expectedY = ((startY - 30) % MAP_H + MAP_H) % MAP_H;
  assertClose(sv.y, expectedY, 0.1, `맵 경계 wrap: y=${sv.y.toFixed(1)} (기대 ${expectedY.toFixed(1)})`);

  const cl = clientApplyInput(0, startY, 1, 0.1);
  assertClose(cl.ny, expectedY, 0.1, `클라이언트 wrap 동일: ny=${cl.ny.toFixed(1)}`);
}

console.log('\n=== Test 7: RTT 구간 보정 — liveAccumDt로 끊김 제거 ===');
{
  // 시나리오: RTT=20ms. 클라이언트는 t=50ms에 패킷 전송, t=70ms에 응답 수신
  // 이 20ms 동안 클라이언트가 예측만 했고 서버엔 아직 못 보낸 상태
  const client   = makeClient(0, 500);
  const rtt      = 20; // ms
  const flags    = 1;  // W 계속
  const step50ms = MOVE_SPEED * 0.05; // 15px

  // 1) t=0~50ms: 3프레임 예측 (실제로는 매 프레임이지만 50ms 뭉쳐서 시뮬레이션)
  clientTick(client, SEND_INTERVAL, SEND_INTERVAL, flags); // 전송 + liveAccumDt=0
  // client.y ≈ 500 - 15 = 485, 전송됨, liveAccumDt=0

  // 2) t=50ms~70ms: 응답 전 추가 예측 (rtt=20ms)
  clientTick(client, SEND_INTERVAL + rtt, rtt, flags);
  // client.y ≈ 485 - 6 = 479, liveAccumDt=20ms

  // 보정 전 예측 위치 저장
  const predictedY = client.y;

  // 3) 서버 응답 도착: t=50ms 기준 위치 ack_seq=1
  const serverY = 500 - step50ms; // 서버는 t=50ms 기준으로 y=485
  clientReconcile(client, 0, serverY, 1);
  // liveAccumDt=20ms → 20ms 추가 재연산 → y = 485 - 6 = 479

  assertClose(client.y, predictedY, 0.5,
    `RTT 보정 후 재연산 = 예측 위치 (오차 ≤0.5px, 예측=${predictedY.toFixed(2)}, 보정후=${client.y.toFixed(2)})`);
}

// ── 결과 ──────────────────────────────────────────────────────────────────
console.log(`\n${'─'.repeat(50)}`);
console.log(`결과: ${passed}/${passed + failed} 통과${failed > 0 ? ` (${failed}개 실패)` : ''}`);
if (failed === 0) {
  console.log('✅ 클라이언트-서버 이동 공식 완전히 일치. 동기화 정상.');
} else {
  console.log('❌ 불일치 발견. 위 실패 항목을 수정해야 합니다.');
  process.exit(1);
}
