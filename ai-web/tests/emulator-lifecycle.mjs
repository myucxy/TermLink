// Isolated acceptance package only. Existing production login and Windows client are untouched.
import {chromium} from 'playwright';
import {execFileSync} from 'node:child_process';
import assert from 'node:assert/strict';
const adb='D:/Dev/Env/Android/Sdk/platform-tools/adb.exe';
const pkg='dev.termlink.harness.aivalidation';
const component=pkg+'/dev.termlink.product.debug.AiAcceptanceActivity';
const run=(...args)=>execFileSync(adb,['-s','emulator-5556',...args],{encoding:'utf8'});
const pause=ms=>new Promise(r=>setTimeout(r,ms));
async function ui(){for(let i=0;i<5;i++){const result=run('shell','uiautomator','dump','/sdcard/ai-window.xml');if(result.includes('dumped'))return run('shell','cat','/sdcard/ai-window.xml');await pause(500);}throw new Error('UI hierarchy unavailable');}
async function tap(label){const xml=await ui();const node=[...xml.matchAll(/<node\b[^>]*>/g)].map(m=>m[0]).find(n=>n.includes(`text="${label}"`)||n.includes(`content-desc="${label}"`));assert(node,`Missing ${label}`);const points=node.match(/bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"/).slice(1).map(Number);run('shell','input','tap',String(Math.round((points[0]+points[2])/2)),String(Math.round((points[1]+points[3])/2)));}
const pid=run('shell','pidof',pkg).trim();run('forward','tcp:19229',`localabstract:webview_devtools_remote_${pid}`);
const browser=await chromium.connectOverCDP('http://127.0.0.1:19229',{noDefaults:true});
try {
  const page=browser.contexts().flatMap(c=>c.pages()).find(p=>p.url().includes('/ai-web/'));assert(page);
  await page.locator('[data-message-id="web-reply"]').getByRole('button',{name:'引用',exact:true}).click();
  assert((await ui()).includes('&gt; ## Web 会话联合测试'),'Quote did not reach native draft');
} finally {await browser.close();}
run('shell','input','keyevent','3');await pause(700);run('shell','am','force-stop',pkg);await pause(700);
let remaining='';try{remaining=run('shell','pidof',pkg).trim();}catch{/* pidof exits 1 when absent */}
assert(!remaining,'Test app process remained alive');
run('shell','am','start','-n',component);await pause(1500);
let recovered=false;for(let i=0;i<5;i++){
  const xml=await ui();if(xml.includes('&gt; ## Web 会话联合测试')){recovered=true;break;}
  if(!xml.includes('content-desc="返回 AI 列表"')&&xml.includes('text="web-ui"'))await tap('web-ui');
  await pause(700);
}
assert(recovered,'Encrypted draft did not survive process recreation');
console.log('PASS emulator: Web quote to native composer, process recreation, encrypted unsent draft recovery');
