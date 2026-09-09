'use strict';
const $ = id => document.getElementById(id);
const key = new URLSearchParams(location.hash.slice(1)).get('key') || '';
// The access key stays out of HTTP requests, browser history after load, and referrers.
history.replaceState(null, '', location.pathname);
let socket, pc, dc, context, worklet, legacy, rtcAudio, heartbeat, pingTimer, connectTimer;
let active = false, enabled = false, rtc = false, seq = 0, first = 48, joining = false;
let fallbackQueue = [], fallbackOffset = 0, fallbackSamples = 0, legacyReady = false;
const pointers = new Map(), keyButtons = new Map();
const white = n => ![1,3,6,8,10].includes(n % 12);
const noteName = n => ['C','C♯','D','D♯','E','F','F♯','G','G♯','A','A♯','B'][n % 12] + (Math.floor(n / 12) - 1);
function status(text) { $('status').textContent = text; }
function message(text) { $('message').textContent = text; }
function send(data) { if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(data)); }
function state() {
  if (!active) return;
  const data = JSON.stringify({type:'notes', seq:++seq, notes:enabled ? [...new Set(pointers.values())].filter(n=>n>=21) : []});
  if (dc?.readyState === 'open' && dc.bufferedAmount < 4096) dc.send(data);
  else if (socket?.readyState === WebSocket.OPEN && socket.bufferedAmount < 4096) socket.send(data);
}
function release() { pointers.clear(); paint(); state(); }
function paint() { const held = new Set(pointers.values()); keyButtons.forEach((button,n) => { button.classList.toggle('held',held.has(n)); button.setAttribute('aria-pressed',String(held.has(n))); }); }
function render() {
  release(); const keyboard = $('keyboard'); keyboard.replaceChildren(); keyButtons.clear();
  const octaves = matchMedia('(max-width:720px) and (orientation:portrait)').matches ? 2 : 3;
  const end = first + octaves * 12 - 1; const whiteCount = octaves * 7; let index = 0;
  for (let n = first; n <= end; n++) {
    const button = document.createElement('button'); const isWhite = white(n);
    button.className = 'key' + (isWhite ? '' : ' black'); button.dataset.note = String(n);
    button.setAttribute('aria-label',noteName(n)); button.setAttribute('aria-pressed','false');
    button.tabIndex = -1; button.textContent = n % 12 === 0 ? noteName(n) : '';
    if (isWhite) index++;
    else { button.style.width = `${65 / whiteCount}%`; button.style.left = `${(index - .325) * 100 / whiteCount}%`; }
    // Key geometry is calculated at runtime; use CSSOM rather than HTML style attributes.
    keyboard.append(button); keyButtons.set(n,button);
  }
  $('range').textContent = `${noteName(first)} — ${noteName(end)}`;
  $('lower').disabled = first <= 24; $('higher').disabled = first >= 72;
}
function pointerNote(event) {
  const element = document.elementFromPoint(event.clientX,event.clientY)?.closest('.key');
  return element && $('keyboard').contains(element) ? Number(element.dataset.note) : null;
}
$('keyboard').addEventListener('pointerdown',event => {
  if (!active || !enabled || event.button !== 0) return;
  event.preventDefault(); const note = pointerNote(event); if (note === null) return;
  $('keyboard').setPointerCapture(event.pointerId); pointers.set(event.pointerId,note); paint(); state();
});
$('keyboard').addEventListener('pointermove',event => {
  if (!pointers.has(event.pointerId)) return;
  const note = pointerNote(event) ?? -1;
  if (note === pointers.get(event.pointerId)) return;
  // Keep capture outside the keyboard so sliding back in starts the new note.
  pointers.set(event.pointerId,note); paint(); state();
});
for (const name of ['pointerup','pointercancel','lostpointercapture']) $('keyboard').addEventListener(name,event => { if (pointers.delete(event.pointerId)) { paint(); state(); } });
$('keyboard').addEventListener('contextmenu',event => event.preventDefault());
$('lower').onclick = () => { first = Math.max(24,first - 12); render(); };
$('higher').onclick = () => { first = Math.min(72,first + 12); render(); };
window.addEventListener('resize',render); window.addEventListener('blur',release);
document.addEventListener('visibilitychange',() => { if (document.hidden) release(); else if (active && context?.state !== 'running') { message('点击「恢复声音」继续演奏'); $('join').hidden=false; $('join').textContent='恢复声音'; } });
window.addEventListener('pagehide',() => { release(); socket?.close(); });

async function prepareAudio() {
  context = new (window.AudioContext || window.webkitAudioContext)({latencyHint:'interactive',sampleRate:48000});
  await context.resume();
  rtcAudio = document.createElement('audio');
  rtcAudio.autoplay = true; rtcAudio.setAttribute('playsinline',''); document.body.append(rtcAudio);
  if (context.audioWorklet) {
    await context.audioWorklet.addModule('/audio-worklet.js');
    worklet = new AudioWorkletNode(context,'piano-stream',{numberOfInputs:0,numberOfOutputs:1,outputChannelCount:[2]});
    worklet.connect(context.destination);
  } else {
    // HTTP on a LAN is not a secure context. ScriptProcessor keeps that optional path usable.
    legacy = context.createScriptProcessor(512,0,2);
    legacy.onaudioprocess = event => {
      if (rtc) return;
      if (!legacyReady && fallbackSamples >= 2880) legacyReady = true;
      if (!legacyReady) return;
      const left = event.outputBuffer.getChannelData(0), right = event.outputBuffer.getChannelData(1), step = 48000 / context.sampleRate;
      for (let i = 0; i < left.length; i++) {
        while (fallbackQueue.length && fallbackOffset >= fallbackQueue[0].length / 2) { fallbackOffset -= fallbackQueue[0].length / 2; fallbackQueue.shift(); }
        if (!fallbackQueue.length) { legacyReady=false; fallbackSamples=0; break; }
        const pcm = fallbackQueue[0], offset = Math.floor(fallbackOffset)*2;
        left[i] = pcm[offset]/32768; right[i] = pcm[offset+1]/32768;
        fallbackOffset += step; fallbackSamples = Math.max(0,fallbackSamples-step*2);
      }
    };
    legacy.connect(context.destination);
  }
}
function switchAudio(useRtc) {
  rtc = useRtc;
  worklet?.port.postMessage({reset:true,enabled:!rtc});
  fallbackQueue=[]; fallbackSamples=0; fallbackOffset=0; legacyReady=false;
  if (rtcAudio) rtcAudio.muted = !rtc;
  send({type:'fallback',enabled:!rtc});
  $('route').textContent = rtc ? 'WebRTC · 立体声' : '网页通道 · 立体声';
}
async function setupRtc(iceServers) {
  if (!window.RTCPeerConnection) return;
  pc = new RTCPeerConnection({iceServers,bundlePolicy:'max-bundle'});
  const transceiver = pc.addTransceiver('audio',{direction:'recvonly'});
  const codecs = window.RTCRtpReceiver?.getCapabilities?.('audio')?.codecs.filter(codec=>codec.mimeType.toLowerCase()==='audio/opus');
  if (codecs?.length && transceiver.setCodecPreferences) transceiver.setCodecPreferences(codecs);
  try { transceiver.receiver.jitterBufferTarget=20; transceiver.receiver.playoutDelayHint=.02; } catch {}
  dc = pc.createDataChannel('keys',{ordered:false,maxRetransmits:0}); dc.onopen = state;
  let offerSent = false; const localIce = [];
  pc.onicecandidate = event => {
    if (!event.candidate) return;
    const candidate = event.candidate.toJSON();
    if (offerSent) send({type:'ice',candidate}); else localIce.push(candidate);
  };
  pc.ontrack = event => {
    const stream = new MediaStream([event.track]);
    rtcAudio.srcObject = stream;
    event.track.onunmute = playRtc;
    event.track.onmute = () => { if(active) switchAudio(false); };
    if (!event.track.muted && active) playRtc();
  };
  pc.onconnectionstatechange = () => {
    if (['failed','disconnected','closed'].includes(pc.connectionState) && active) { switchAudio(false); message('正在使用网页音频通道；较差网络下可能有额外延迟'); }
  };
  const offer = await pc.createOffer();
  // Ask the sender for stereo music instead of WebRTC's default mono speech settings.
  offer.sdp = offer.sdp.replace(/a=fmtp:(\d+) ([^\r\n]*)/g,(line,id,params) => line.includes('useinbandfec') ? `a=fmtp:${id} ${params};stereo=1;sprop-stereo=1;maxaveragebitrate=192000` : line);
  await pc.setLocalDescription(offer); send({type:'offer',sdp:offer.sdp}); offerSent=true;
  for (const candidate of localIce) send({type:'ice',candidate});
}
let pendingIce=[];
async function playRtc() {
  if (!active || !rtcAudio?.srcObject) return;
  const element = rtcAudio;
  try {
    element.muted=false; await element.play();
    if(active && element===rtcAudio) switchAudio(true);
  } catch {
    element.muted=true;
    message('点击「恢复声音」启用 WebRTC 音频'); $('join').hidden=false; $('join').textContent='恢复声音';
  }
}
async function receive(event) {
  if (event.data instanceof ArrayBuffer) {
    if (rtc) return;
    if (worklet) worklet.port.postMessage(event.data,[event.data]);
    else {
      const pcm = new Int16Array(event.data); fallbackQueue.push(pcm); fallbackSamples += pcm.length;
      if(fallbackSamples>9600) { while(fallbackQueue.length>3) fallbackQueue.shift(); fallbackOffset=0; fallbackSamples=fallbackQueue.reduce((sum,p)=>sum+p.length,0); }
    }
    return;
  }
  const data=JSON.parse(event.data);
  if(data.type==='ready') {
    clearTimeout(connectTimer);
    active=true; joining=false; enabled=data.enabled; document.body.classList.add('connected');
    $('keyboard').setAttribute('aria-disabled',String(!enabled)); status('已连接琴房');
    $('join').hidden=true; $('join').disabled=false; $('leave').hidden=false;
    $('hint').textContent=enabled?'轻触琴键 · 支持多指和弦':'主机正在录制，暂时仅可聆听';
    message('支持多指和弦 · 横屏弹奏更舒展'); switchAudio(false);
    heartbeat=setInterval(state,150);
    pingTimer=setInterval(()=>send({type:'ping',time:performance.now()}),2000);
    try { await setupRtc(data.iceServers); } catch { message('使用网页音频通道；WebRTC 当前不可用'); }
  } else if(data.type==='answer' && pc) {
    await pc.setRemoteDescription({type:'answer',sdp:data.sdp});
    for(const candidate of pendingIce) await pc.addIceCandidate(candidate); pendingIce=[];
  } else if(data.type==='ice' && pc) {
    const candidate=typeof data.candidate==='string'?JSON.parse(data.candidate):data.candidate;
    if(pc.remoteDescription) await pc.addIceCandidate(candidate); else pendingIce.push(candidate);
  } else if(data.type==='pong') $('latency').textContent=`网络往返 ${Math.round(performance.now()-data.time)} ms`;
  else if(data.type==='input') { enabled=data.enabled; release(); $('keyboard').setAttribute('aria-disabled',String(!enabled)); $('hint').textContent=enabled?'轻触琴键 · 支持多指和弦':'主机正在录制，暂时仅可聆听'; }
  else if(data.type==='error') { message(data.message); status('主机需要处理'); }
}
async function leave(text='已离开琴房') {
  release(); active=false; joining=false; enabled=false;
  clearInterval(heartbeat); clearInterval(pingTimer); clearTimeout(connectTimer); pendingIce=[];
  const old=socket; socket=null; if(old) { old.onclose=null; old.close(); }
  if(pc) { pc.onconnectionstatechange=null; pc.close(); pc=null; } dc=null;
  worklet?.disconnect(); worklet=null; legacy?.disconnect(); legacy=null;
  if (rtcAudio) { rtcAudio.srcObject=null; rtcAudio.remove(); rtcAudio=null; }
  if(context) { await context.close().catch(()=>{}); context=null; }
  fallbackQueue=[]; fallbackSamples=0; document.body.classList.remove('connected');
  $('keyboard').setAttribute('aria-disabled','true'); $('join').hidden=false; $('join').disabled=false; $('join').textContent='重新进入琴房 ↗'; $('leave').hidden=true;
  $('route').textContent='声音已断开'; $('latency').textContent='LIVE AUDIO'; status(text);
}
$('join').onclick=async()=>{
  if(active) { $('join').hidden=true; const resumed=context.resume(); const played=playRtc(); await Promise.all([resumed,played]); return; }
  if(joining) return;
  if(!key) { message('请使用主机分享的完整邀请链接，链接需要包含访问密钥。'); return; }
  joining=true; $('join').disabled=true; status('正在连接…');
  try {
    await prepareAudio();
    socket=new WebSocket(`${location.protocol==='https:'?'wss':'ws'}://${location.host}/ws`); socket.binaryType='arraybuffer';
    connectTimer=setTimeout(()=>{ if(!active) { leave('连接超时'); message('请检查主机共享是否开启，以及穿透服务是否支持 WebSocket。'); } },10000);
    socket.onopen=()=>send({type:'auth',key});
    // Serialize signaling so trickled ICE cannot overtake the SDP answer.
    let receiving=Promise.resolve();
    socket.onmessage=event=>{ receiving=receiving.then(()=>receive(event)).catch(()=>message('连接协商遇到问题，请重新进入琴房')); };
    socket.onclose=()=>{ leave('连接已断开'); message('请重新进入；若仍失败，请检查邀请链接或主机共享状态。'); };
    socket.onerror=()=>message('无法连接主机，请检查网络和内网穿透配置。');
  } catch(error) { await leave('未能开启音频'); message(error.message); }
};
$('leave').onclick=()=>leave(); render();
