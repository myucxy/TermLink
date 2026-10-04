import {test,expect,type Page} from '@playwright/test';

const session={id:'s1',agent:'Codex',state:'running',phase:'thinking',turnStartedAtMs:Date.now()-4000,lastActivityAtMs:Date.now(),
  canControl:true,busy:false,uncertain:false,historyBefore:0,truncated:false,pending:[],plan:[{step:'验证页面',status:'in_progress'}],
  usage:JSON.stringify({last:{totalTokens:12000},total:{inputTokens:8000,outputTokens:4000,totalTokens:12000},modelContextWindow:128000}),
  diff:'diff --git a/demo.html b/demo.html\n--- a/demo.html\n+++ b/demo.html\n@@ -1 +1 @@\n-old\n+new',error:'',artifacts:true,search:true,dark:false,fontScale:1};
const msg=(id:string,text:string,role='assistant',kind='text')=>({id,text,role,kind,title:'',summary:'',status:'completed',fullLength:text.length});
async function open(page:Page,messages:any[],value:any=session){
  await page.addInitScript(({messages,session})=>{
    if(window.top!==window)return; // Match the native bridge's main-frame allowlist.
    (window as any).calls=[];
    (window as any).TermLink={postMessage:(raw:string)=>{
      const r=JSON.parse(raw);(window as any).calls.push(r);
      if(r.action==='ready')setTimeout(()=>(window as any).termlinkUpdate({session,messages,reset:true,uiState:JSON.parse(sessionStorage.getItem('fixture-ui')||'{}')}),0);
      if(r.action==='ui-state')sessionStorage.setItem('fixture-ui',JSON.stringify(r.state));
      const hits=messages.filter((m:any)=>m.text.includes(r.query||''));
      const files=(session as any).fixtureArtifacts||[];
      const file=files.find((f:any)=>f.id===r.fileId);
      const result=r.action==='search'?{items:hits.slice(r.offset||0,(r.offset||0)+50),nextOffset:hits.length>(r.offset||0)+50?(r.offset||0)+50:-1}:r.action==='artifacts'?{files,truncated:false}:r.action==='artifact.read'&&file?{...file,offset:0,nextOffset:file.size,data:btoa(file.text)}:true;
      setTimeout(()=>(window as any).termlinkReply({id:r.id,value:result}),r.query==='delayed'?700:0);
    }};
  },{messages,session:value});
  await page.goto('/');await expect(page.getByRole('navigation',{name:value.language==='en'?'Conversation tools':'会话工具'})).toBeVisible();
}
test('10,000 messages stay virtualized; updates preserve the reading position',async({page})=>{
  await open(page,Array.from({length:10000},(_,i)=>msg(`m${i}`,`Message ${i}\n\n${'body '.repeat(15)}`)));
  await expect(page.locator('[data-message-id="m9999"]')).toBeVisible();
  expect(await page.locator('article').count()).toBeLessThan(45);
  const scroller=page.locator('[data-virtuoso-scroller]');await scroller.evaluate(async el=>{(document.activeElement as HTMLElement)?.blur();await new Promise(requestAnimationFrame);el.scrollTop=el.scrollHeight/2;});
  await expect(page.getByRole('button',{name:'↓ 最新'})).toBeVisible();
  const position=await scroller.evaluate(el=>el.scrollTop);
  await page.evaluate(({session,message})=>(window as any).termlinkUpdate({session,messages:[message],reset:false}),{session,message:msg('m9999','new tail')});
  await page.waitForTimeout(150);expect(Math.abs(await scroller.evaluate(el=>el.scrollTop)-position)).toBeLessThan(10);
  expect(await page.locator('article').count()).toBeLessThan(45);
});
test('approval answers survive metadata/delta refresh; denied requests cannot allow',async({page})=>{
  const value={...session,pending:[{id:'p1',kind:'question',title:'选择范围',summary:'',detail:'fixture',canApprove:true,questions:[{id:'q1',text:'处理范围',options:['仅测试','全部'],multiSelect:false}]}]};
  await open(page,[msg('m1','思考公开摘要','assistant','reasoning')],value);
  await page.getByPlaceholder('补充回答').fill('不要改变 CLI');
  await page.getByLabel('仅测试',{exact:true}).check();
  await page.evaluate(session=>(window as any).termlinkUpdate({session,messages:[],reset:false}),{...value,phase:'generating'});
  await expect(page.getByPlaceholder('补充回答')).toHaveValue('不要改变 CLI');
  await expect(page.getByLabel('仅测试',{exact:true})).toBeChecked();
  await page.getByRole('button',{name:'提交回答'}).click();
  expect(await page.evaluate(()=>(window as any).calls.find((c:any)=>c.action==='answer').answers.q1)).toEqual(['仅测试','不要改变 CLI']);
  await page.evaluate(session=>(window as any).termlinkUpdate({session,messages:[],reset:false}),{...value,pending:[{...value.pending[0],canApprove:false}]});
  await expect(page.getByRole('button',{name:'提交回答'})).toBeDisabled();
});
test('static HTML has no scripts, navigation, remote resources or native bridge',async({page})=>{
  const requests:string[]=[];page.on('request',r=>{if(r.url().includes('invalid.example'))requests.push(r.url());});
  const html='<h1>Static preview</h1><script>parent.hacked=1</script><img src="https://invalid.example/leak"><a href="javascript:alert(1)">unsafe</a><iframe src="https://invalid.example"></iframe><form><input></form>';
  await open(page,[msg('m1','```html\n'+html+'\n```')]);
  await page.getByRole('button',{name:'预览',exact:true}).click();
  const frame=page.frameLocator('iframe');await expect(frame.locator('h1')).toHaveText('Static preview');
  await expect(frame.locator('script, iframe, form')).toHaveCount(0);
  expect(await frame.locator('body').evaluate(()=>typeof (window as any).TermLink)).toBe('undefined');
  expect(await page.evaluate(()=>(window as any).hacked)).toBeUndefined();expect(requests).toEqual([]);
  await expect(page.locator('iframe')).toHaveAttribute('sandbox','');
  await expect(page.getByRole('button',{name:'打开所在目录',exact:true})).toHaveCount(0);
});

test('workspace file and preview open the exact parent through the native bridge',async({page})=>{
  const file={id:'output/site/index.html',name:'index.html',mime:'text/html',text:'<h1>Generated file</h1>',size:23,version:'23:123'};
  file.size=file.text.length;
  await open(page,[msg('m1','Created output/site/index.html')],{...session,fixtureArtifacts:[file]});
  await page.getByRole('button',{name:'▱ 文件'}).click();
  await page.getByRole('button',{name:'打开所在目录 · index.html'}).click();
  expect(await page.evaluate(()=>(window as any).calls.filter((c:any)=>c.action==='open-directory').map((c:any)=>c.fileId))).toEqual([file.id]);
  await page.locator('.file-row .file').click();
  await expect(page.frameLocator('iframe').locator('h1')).toHaveText('Generated file');
  await page.getByRole('button',{name:'打开所在目录',exact:true}).click();
  expect(await page.evaluate(()=>(window as any).calls.filter((c:any)=>c.action==='open-directory').map((c:any)=>c.fileId))).toEqual([file.id,file.id]);
});

test('English locale translates conversation actions and file directory control',async({page})=>{
  await open(page,[msg('m1','Hello')],{...session,language:'en',fixtureArtifacts:[{id:'note.txt',name:'note.txt',size:0,mime:'text/plain',version:'0:1'}]});
  await page.getByRole('button',{name:'▱ Files'}).click();
  await expect(page.getByRole('button',{name:'Open containing folder · note.txt'})).toBeVisible();
  await expect(page.getByRole('button',{name:'Save note.txt'})).toBeVisible();
});
test('model retry is visible and Stop remains available',async({page})=>{
  await open(page,[],{...session,phase:'retrying',error:'AGENT_RATE_LIMITED',language:'en',plan:[]});
  await expect(page.getByRole('status')).toContainText('Waiting to retry model service');
  await page.getByRole('button',{name:'Stop',exact:true}).click();
  expect(await page.evaluate(()=>(window as any).calls.some((c:any)=>c.action==='interrupt'))).toBe(true);
});
test('compact tools remain reachable on narrow screens with large text and a fixed Stop',async({page})=>{
  await open(page,[msg('m1','Hello')],{...session,language:'en',plan:[]});
  const toolbar=page.getByRole('navigation',{name:'Conversation tools'});
  await expect(page.locator('[data-message-id="m1"]')).toBeVisible();
  expect((await toolbar.boundingBox())!.height).toBeLessThanOrEqual(38);
  await page.screenshot({path:'test-results/compact-toolbar.png'});
  await page.setViewportSize({width:320,height:740});
  await page.evaluate(session=>(window as any).termlinkUpdate({session,messages:[],reset:false}),{...session,language:'en',fontScale:2,plan:[]});
  const stop=toolbar.getByRole('button',{name:'Stop',exact:true});
  const stopBounds=(await stop.boundingBox())!;
  const tools=toolbar.locator('.tool-links');
  expect(await tools.evaluate(el=>el.scrollWidth>el.clientWidth)).toBe(true);
  expect(await toolbar.evaluate(el=>el.scrollWidth<=el.clientWidth)).toBe(true);
  for(const [button,panel] of [['⌕ Search','Search conversation'],['▱ Files','Workspace files'],['± Changes','File changes'],['Usage','Context and usage']]) {
    await toolbar.getByRole('button',{name:button,exact:true}).click();
    await expect(page.locator('.panel-title strong')).toHaveText(panel);
    const bounds=(await stop.boundingBox())!;
    expect(bounds.x).toBeCloseTo(stopBounds.x,0);expect(bounds.x+bounds.width).toBeLessThanOrEqual(320);
    await expect(stop).toBeInViewport();
  }
  await stop.click();
  expect(await page.evaluate(()=>(window as any).calls.filter((r:any)=>r.action==='interrupt').length)).toBe(1);
  await page.screenshot({path:'test-results/compact-toolbar-large-font.png'});
});
test('user bubbles fit short prompts and keep actions accessible outside long content',async({page})=>{
  const idle={...session,state:'idle',plan:[],dark:true,language:'en'};
  await open(page,[msg('u1','hi','user'),msg('a1','Hi! 有什么我可以帮你的吗？')],idle);
  const user=page.locator('[data-message-id="u1"]');
  const bubble=user.locator('.user-bubble');
  const bounds=(await bubble.boundingBox())!;
  expect(bounds.width).toBeLessThan(90);
  expect(bounds.x+bounds.width).toBeCloseTo(381,0);
  await user.getByRole('button',{name:'Copy',exact:true}).click();
  await user.getByRole('button',{name:'Append to draft',exact:true}).click();
  expect(await page.evaluate(()=>(window as any).calls.filter((r:any)=>r.action==='copy'||r.action==='edit').map((r:any)=>[r.action,r.text]))).toEqual([['copy','hi'],['edit','hi']]);
  for(const button of await user.getByRole('button').all()) {const box=(await button.boundingBox())!;expect(box.width).toBeGreaterThanOrEqual(44);expect(box.height).toBeGreaterThanOrEqual(44);expect(box.y).toBeGreaterThanOrEqual(bounds.y+bounds.height);}
  await page.screenshot({path:'test-results/user-bubble-dark.png'});
  await page.setViewportSize({width:320,height:740});
  const long='长内容保持换行 '.repeat(16)+'\n\n```text\n'+('long_path_without_spaces/'.repeat(30))+'\n```';
  for(const dark of [true,false]) {
    await page.evaluate(({session,message})=>(window as any).termlinkUpdate({session,messages:[message],reset:false}),{session:{...idle,dark,fontScale:2},message:msg('u1',long,'user')});
    const scroller=page.locator('[data-virtuoso-scroller]');await scroller.evaluate(el=>{el.scrollTop=0;});
    await expect(bubble).toContainText('长内容保持换行');
    expect(await bubble.evaluate(el=>el.scrollWidth<=el.clientWidth+1)).toBe(true);
    const box=(await bubble.boundingBox())!;expect(box.x).toBeGreaterThanOrEqual(12);expect(box.x+box.width).toBeLessThanOrEqual(308);
  }
  await page.screenshot({path:'test-results/user-bubble-large-font.png'});
});
test('compact transcript exposes plan, thought, usage and per-file diff',async({page})=>{
  await open(page,[msg('m1','检查公开事件并执行测试','assistant','reasoning'),msg('m2','## 已完成\n\n| 功能 | 状态 |\n|---|---|\n| 预览 | 通过 |')]);
  await expect(page.getByText('思考中',{exact:true})).toBeVisible();
  await page.getByText('思考摘要',{exact:true}).click();await expect(page.getByText('检查公开事件并执行测试')).toBeVisible();
  await page.getByRole('button',{name:'用量',exact:true}).click();await expect(page.locator('progress')).toHaveAttribute('value','12000');
  await page.getByRole('button',{name:'± 变更'}).click();await expect(page.getByText('demo.html', {exact:false}).first()).toBeVisible();
  await page.screenshot({path:'test-results/compact-chat.png'});
});

test('native saved question drafts and reading anchor survive a destroyed WebView',async({page})=>{
  const value={...session,pending:[{id:'p1',kind:'question',title:'范围',summary:'',detail:'fixture',canApprove:true,questions:[{id:'q1',text:'范围',options:['测试','全部'],multiSelect:false}]}]};
  await open(page,Array.from({length:300},(_,i)=>msg(`m${i}`,`Message ${i}\n${'text '.repeat(25)}`)),value);
  await page.getByPlaceholder('补充回答').fill('仅验证');await page.getByLabel('测试',{exact:true}).check();
  await expect.poll(()=>page.evaluate(()=>JSON.parse(sessionStorage.getItem('fixture-ui')||'{}').questions?.p1?.answers?.q1?.[0])).toBe('测试');
  const scroller=page.locator('[data-virtuoso-scroller]');await scroller.hover();await page.mouse.wheel(0,-5000);
  await expect.poll(()=>page.evaluate(()=>{const s=JSON.parse(sessionStorage.getItem('fixture-ui')||'{}');return s.tail===false&&!!s.anchor?.id;})).toBe(true).catch(async e=>{console.log(await scroller.evaluate(el=>({scroll:el.scrollTop,height:el.scrollHeight,rows:[...el.querySelectorAll('[data-row-id]')].map(r=>({id:(r as HTMLElement).dataset.rowId,top:r.getBoundingClientRect().top,bottom:r.getBoundingClientRect().bottom})),ui:sessionStorage.getItem('fixture-ui')})));throw e;});
  const saved=await page.evaluate(()=>JSON.parse(sessionStorage.getItem('fixture-ui')!));
  await page.reload();await expect(page.locator(`[data-row-id="${saved.anchor.id}"]`)).toBeVisible();
  await page.getByRole('button',{name:'查看待处理',exact:true}).click();
  await expect(page.getByPlaceholder('补充回答')).toHaveValue('仅验证');await expect(page.getByLabel('测试',{exact:true})).toBeChecked();
  expect(await page.evaluate(()=>(window as any).calls.some((r:any)=>r.action==='answer'))).toBe(false);
});

test('search paginates and rejects stale query results; grouped tool detail stays accessible',async({page})=>{
  const tools=Array.from({length:75},(_,i)=>({...msg(`t${i}`,`tool needle ${i}`,'tool','tool'),title:`Tool ${i}`,status:i===4?'failed':'completed'}));
  await open(page,[msg('begin','start'),...tools,msg('end','done')]);
  await expect(page.locator('.tool-group')).toHaveCount(4);
  await expect(page.locator('.tool-group').first().locator('summary').first()).toContainText('1 失败');
  await page.getByRole('button',{name:'⌕ 搜索'}).click();await page.getByPlaceholder('输入要查找的内容').fill('needle');await page.getByRole('button',{name:'查找',exact:true}).click();
  await expect(page.locator('.panel .file')).toHaveCount(50);await page.getByRole('button',{name:'加载更多结果'}).click();await expect(page.locator('.panel .file')).toHaveCount(75);
  await page.getByPlaceholder('输入要查找的内容').fill('delayed');await page.getByRole('button',{name:'查找',exact:true}).click();
  await page.getByPlaceholder('输入要查找的内容').fill('needle 4');await page.getByRole('button',{name:'查找',exact:true}).click();
  await expect(page.locator('.panel .file')).toHaveCount(11);await page.waitForTimeout(750);await expect(page.locator('.panel .file')).toHaveCount(11);
  await page.locator('.panel .file').filter({hasText:'tool needle 4'}).first().click();
  await expect(page.locator('[data-message-id="t4"] pre')).toBeVisible();
});

test('file filters do not change search results; native Back closes preview before the panel',async({page})=>{
  await open(page,[msg('m1','needle'),msg('m2','```html\n<h1>Example</h1>\n```')]);
  await page.getByRole('button',{name:'⌕ 搜索'}).click();
  await page.getByPlaceholder('输入要查找的内容').fill('needle');
  await page.getByRole('button',{name:'查找',exact:true}).click();
  await expect(page.locator('.panel .file')).toHaveCount(1);
  await page.getByRole('button',{name:'▱ 文件'}).click();
  await page.getByPlaceholder('筛选文件名称').fill('html');
  await page.getByRole('button',{name:'⌕ 搜索'}).click();
  await expect(page.getByPlaceholder('输入要查找的内容')).toHaveValue('needle');
  await expect(page.locator('.panel .file')).toHaveCount(1);
  await page.getByRole('button',{name:'预览',exact:true}).click();
  await expect(page.locator('iframe')).toBeVisible();
  await page.evaluate(()=>(window as any).termlinkBack());
  await expect(page.locator('iframe')).toHaveCount(0);
  await expect(page.locator('.panel')).toBeVisible();
  await page.evaluate(()=>(window as any).termlinkBack());
  await expect(page.locator('.panel')).toHaveCount(0);
});
