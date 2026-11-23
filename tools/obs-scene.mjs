// obs-scene.mjs: makes (or refreshes) the OBS scene for streaming Cloudpunk VR, through obs-websocket v5 on
// 127.0.0.1:4455 (OBS must be running, websocket on, no password). Existing scenes are not changed.
//
//   node obs-scene.mjs [scene name] [layout scene]      defaults: 云朋克直播间, MC直播间
//   node obs-scene.mjs --shot <file.png>                 a screenshot of the Cloudpunk capture source
//
// The scene holds a game capture of the Cloudpunk window (the mod draws the stream view there: the steadied first
// person in VR, the simulated head when Coo plays) under references to the overlay and voice sources of the layout
// scene, at the same positions, so the stream looks like the others.
import { writeFileSync } from 'node:fs';

const CAPTURE = 'Cloudpunk 第一视角';
// sources of the layout scene that belong on every stream (overlays, subtitles, Coo's voice)
const SHARED = ['Corti Overlay', 'Stream Overlay', 'ASR字幕', '公告栏', 'CABLE Output'];

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket('ws://127.0.0.1:4455');
    let n = 0;
    const pending = new Map();
    const call = (requestType, requestData = {}) => new Promise((res) => {
      const requestId = `r${++n}`;
      pending.set(requestId, res);
      ws.send(JSON.stringify({ op: 6, d: { requestType, requestId, requestData } }));
    });
    ws.onmessage = (ev) => {
      const m = JSON.parse(String(ev.data));
      if (m.op === 0) ws.send(JSON.stringify({ op: 1, d: { rpcVersion: 1 } }));
      else if (m.op === 2) resolve({ call, close: () => ws.close() });
      else if (m.op === 7) { pending.get(m.d.requestId)?.(m.d); pending.delete(m.d.requestId); }
    };
    ws.onerror = () => reject(new Error('OBS websocket 127.0.0.1:4455 not reachable (is OBS running with the websocket server on?)'));
    setTimeout(() => reject(new Error('OBS websocket timeout')), 10000);
  });
}

const args = process.argv.slice(2);
const obs = await connect();
const ok = (r, what) => { if (!r.requestStatus.result) throw new Error(`${what}: ${r.requestStatus.comment ?? r.requestStatus.code}`); return r.responseData ?? {}; };

if (args[0] === '--shot') {
  const r = await obs.call('GetSourceScreenshot', { sourceName: CAPTURE, imageFormat: 'png', imageWidth: 1280 });
  const d = ok(r, 'screenshot');
  writeFileSync(args[1], Buffer.from(d.imageData.split(',')[1], 'base64'));
  console.log('saved', args[1]);
  obs.close();
  process.exit(0);
}

const sceneName = args[0] ?? '云朋克直播间';
const layoutName = args[1] ?? 'MC直播间';
const scenes = ok(await obs.call('GetSceneList'), 'scene list').scenes.map((s) => s.sceneName);
if (!scenes.includes(sceneName)) { ok(await obs.call('CreateScene', { sceneName }), 'create scene'); console.log('created scene', sceneName); }

const video = ok(await obs.call('GetVideoSettings'), 'video settings');
const inputs = ok(await obs.call('GetInputList'), 'inputs').inputs.map((i) => i.inputName);
const captureSettings = {
  capture_mode: 'window',
  window: 'Cloudpunk:UnityWndClass:Cloudpunk.exe',
  priority: 2,              // match the executable
  capture_cursor: false,
  capture_audio: true,      // the game's sound with the picture
  allow_transparency: false,
};
if (!inputs.includes(CAPTURE)) {
  ok(await obs.call('CreateInput', { sceneName, inputName: CAPTURE, inputKind: 'game_capture', inputSettings: captureSettings, sceneItemEnabled: true }), 'create capture');
  console.log('created', CAPTURE);
} else ok(await obs.call('SetInputSettings', { inputName: CAPTURE, inputSettings: captureSettings }), 'capture settings');

const items = ok(await obs.call('GetSceneItemList', { sceneName }), 'items').sceneItems;
const itemOf = (name) => items.find((i) => i.sourceName === name);
// the capture fills the canvas, at the bottom
let cap = itemOf(CAPTURE);
if (!cap) {
  const r = ok(await obs.call('CreateSceneItem', { sceneName, sourceName: CAPTURE }), 'add capture');
  cap = { sceneItemId: r.sceneItemId };
}
ok(await obs.call('SetSceneItemTransform', { sceneName, sceneItemId: cap.sceneItemId, sceneItemTransform: { boundsType: 'OBS_BOUNDS_SCALE_INNER', boundsWidth: video.baseWidth, boundsHeight: video.baseHeight, positionX: 0, positionY: 0, alignment: 5, boundsAlignment: 0 } }), 'capture transform');
ok(await obs.call('SetSceneItemIndex', { sceneName, sceneItemId: cap.sceneItemId, sceneItemIndex: 0 }), 'capture order');

// overlays and voice from the layout scene, same transform, above the capture
if (scenes.includes(layoutName)) {
  const layoutItems = ok(await obs.call('GetSceneItemList', { sceneName: layoutName }), 'layout items').sceneItems;
  for (const name of SHARED) {
    const src = layoutItems.find((i) => i.sourceName === name);
    if (!src) continue;
    let it = itemOf(name);
    if (!it) {
      const r = ok(await obs.call('CreateSceneItem', { sceneName, sourceName: name, sceneItemEnabled: src.sceneItemEnabled }), `add ${name}`);
      it = { sceneItemId: r.sceneItemId };
      console.log('added', name);
    }
    const t = ok(await obs.call('GetSceneItemTransform', { sceneName: layoutName, sceneItemId: src.sceneItemId }), `transform ${name}`).sceneItemTransform;
    delete t.sourceWidth; delete t.sourceHeight; delete t.width; delete t.height;
    if (t.boundsWidth < 1) { delete t.boundsWidth; delete t.boundsHeight; t.boundsType = 'OBS_BOUNDS_NONE'; }
    ok(await obs.call('SetSceneItemTransform', { sceneName, sceneItemId: it.sceneItemId, sceneItemTransform: t }), `place ${name}`);
  }
}
console.log(`scene "${sceneName}" ready: ${CAPTURE} + ${SHARED.filter((n) => itemOf(n) || true).join(', ')}`);
obs.close();
process.exit(0);
