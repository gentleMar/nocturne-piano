const { chromium } = require('playwright');
const fs=require('node:fs'), assert=require('node:assert/strict');
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
async function until(fn,label){for(let i=0;i<60;i++){try{if(await fn())return;}catch{} await sleep(100);}throw Error(label);}
function serverState(){return JSON.parse(fs.readFileSync('state.json','utf8'));}
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try{
 const url=JSON.parse(fs.readFileSync('host.json','utf8')).url;
 const errors=[];
 async function page(options={},fallback=false){const p=await browser.newPage(options);p.on('pageerror',e=>errors.push(e.message));p.on('console',m=>{if(m.type()==='error')errors.push(m.text());});if(fallback)await p.addInitScript(()=>{window.RTCPeerConnection=undefined;});await p.goto(url);await p.locator('#join').click();await p.waitForFunction(()=>active);return p;}
 const p=await page({viewport:{width:1280,height:850}});
 await p.waitForFunction(()=>rtc && dc.readyState==='open');
 const bounds=await p.locator('[data-note="60"]').boundingBox();const point={x:bounds.x+bounds.width/2,y:bounds.y+bounds.height-25};
 await p.mouse.move(point.x,point.y);await p.mouse.down();
 await until(()=>serverState().notes['60']===1,'RTC note on');
 await p.waitForTimeout(900);
 const stats=await p.evaluate(async()=>[...(await pc.getStats()).values()].find(s=>s.type==='inbound-rtp'));
 assert(stats.totalAudioEnergy>.001,'RTC decoded piano energy');assert(stats.packetsReceived>20);
 assert(await p.evaluate(()=>!rtcAudio.paused && !rtcAudio.muted),'RTC media sink playing');
 await p.mouse.move(4,4);await until(()=>Object.keys(serverState().notes).length===0,'slide outside releases');
 await p.mouse.move(point.x,point.y);await until(()=>serverState().notes['60']===1,'slide back in plays');
 await p.mouse.up();await until(()=>Object.keys(serverState().notes).length===0,'RTC release');
 console.log('PASS WebRTC stereo audio / data channel / glissando',JSON.stringify({packets:stats.packetsReceived,energy:stats.totalAudioEnergy,lost:stats.packetsLost}));
 await p.screenshot({path:'desktop.png',fullPage:true});
 const m=await page({viewport:{width:390,height:844},isMobile:true,hasTouch:true,deviceScaleFactor:1},true);
 assert.equal(await m.locator('.key').count(),24);assert.equal(await m.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
 await m.evaluate(()=>{window.measure=context.createAnalyser();worklet.connect(window.measure);});
 const cdp=await m.context().newCDPSession(m);
 const points=[];for(const n of [60,64,67]){const b=await m.locator(`[data-note="${n}"]`).boundingBox();points.push({x:b.x+b.width/2,y:b.y+b.height-20,id:n});}
 await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:points});
 await until(()=>Object.keys(serverState().notes).length===3,'mobile chord');
 await sleep(400);
 const peak=await m.evaluate(()=>{const samples=new Float32Array(measure.fftSize);measure.getFloatTimeDomainData(samples);return Math.max(...samples.map(Math.abs));});
 assert(peak>.001,'fallback AudioWorklet decoded piano');
 await m.screenshot({path:'mobile.png',fullPage:true});
 await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});await until(()=>Object.keys(serverState().notes).length===0,'mobile release');
 console.log('PASS mobile multi-touch / PCM AudioWorklet / responsive layout peak='+peak);
 await p.bringToFront();await p.mouse.move(point.x,point.y);await p.mouse.down();await until(()=>serverState().notes['60']===1,'first participant');
 // Protocol-level second participant tests ownership of the same note without a focus change.
 await m.evaluate(()=>{pointers.set(999,60);state();});await until(()=>serverState().notes['60']===2,'shared same note refcount');
 await m.evaluate(()=>release());await until(()=>serverState().notes['60']===1,'one participant release preserves other');
 await p.mouse.up();await until(()=>Object.keys(serverState().notes).length===0,'both released');
 await p.mouse.down();await until(()=>serverState().notes['60']===1,'note before record lock');
 fs.writeFileSync('disable','');await p.waitForFunction(()=>!enabled);await until(()=>Object.keys(serverState().notes).length===0,'record lock clears notes');
 await p.mouse.up();fs.writeFileSync('enable','');await p.waitForFunction(()=>enabled);
 console.log('PASS shared-note ownership / recording lock');
 await p.mouse.down();await until(()=>serverState().notes['60']===1,'note before watchdog');
 await p.evaluate(()=>clearInterval(heartbeat));await until(()=>Object.keys(serverState().notes).length===0,'watchdog releases without updates');await p.mouse.up();
 await p.evaluate(()=>{heartbeat=setInterval(state,150);});await p.mouse.down();await until(()=>serverState().notes['60']===1,'note before disconnect');await p.close();await until(()=>Object.keys(serverState().notes).length===0,'disconnect releases');
 console.log('PASS heartbeat watchdog / abrupt disconnect');
 const denied=await browser.newPage();await denied.goto(url.replace(/key=.*/,'key=bad'));await denied.locator('#join').click();await denied.waitForFunction(()=>document.querySelector('#status').textContent==='连接已断开');await denied.close();
 const response=await m.request.get(new URL('/settings.json',url).href);assert.equal(response.status(),404);
 assert.deepEqual(errors,[]); console.log('PASS invalid key rejection / no file access / no JS or CSP errors');
 await m.locator('#leave').click();await until(()=>serverState().clients===0,'all clients closed');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
