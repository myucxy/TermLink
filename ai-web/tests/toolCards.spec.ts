import {test,expect,type Page} from '@playwright/test';
import type {Message,Session,UiState} from '../src/bridge';
import {legacyTools,toolMessage,toolSession} from './toolFixtures';

async function openTools(page:Page,messages:Message[],options:{session?:Partial<Session>;ui?:UiState;full?:Record<string,string>;delay?:number}={}) {
  const session={...toolSession,...options.session};
  const ui=options.ui||{expanded:[...messages.map(m=>m.id),'tools:'+messages[0]?.id]};
  await page.addInitScript(({messages,session,ui,full,delay})=>{
    if(window.top!==window)return;
    const win=window as any;win.calls=[];
    win.TermLink={postMessage:(raw:string)=>{
      const r=JSON.parse(raw);win.calls.push(r);
      if(r.action==='ready')setTimeout(()=>win.termlinkUpdate({session,messages,reset:true,uiState:ui}),0);
      let value:unknown=true;
      if(r.action==='message')value=full[r.messageId];
      if(r.action==='open-file')value={id:r.path,name:r.path,mime:'text/plain',size:9,version:'fixture'};
      if(r.action==='artifact.read')value={id:r.fileId,name:r.fileId,mime:'text/plain',size:9,version:'fixture',offset:0,nextOffset:9,data:btoa('file body')};
      setTimeout(()=>win.termlinkReply({id:r.id,value}),r.action==='message'?delay:0);
    }};
  },{messages,session,ui,full:options.full||{},delay:options.delay||0});
  await page.goto('/');await expect(page.getByRole('navigation',{name:session.language==='zh'?'会话工具':'Conversation tools'})).toBeVisible();
  return session;
}
const card=(page:Page,id:string)=>page.locator(`[data-message-id="${id}"] .tool-card`);

for(const fixture of legacyTools)test(`${fixture.agent} legacy commands use the shared mobile card`,async({page})=>{
  await openTools(page,[toolMessage('tool',fixture.payload,fixture.kind)],{session:{agent:fixture.agent}});
  const tool=card(page,'tool');await expect(tool).toHaveAttribute('data-tool-kind','command');
  await expect(tool.locator('.tool-command')).toContainText('printf ');
  await expect(tool.locator('.tool-output')).toContainText(' output');
  await expect(tool.locator('.tool-state')).toHaveText('Completed');
  await tool.locator('.tool-raw>summary').click();await expect(tool.locator('.tool-raw pre')).toContainText(fixture.kind==='tool_use'?'tool_result':fixture.kind==='acp'?'execute':fixture.kind==='tool'?'toolName':'commandExecution');
  await expect(tool).toHaveAttribute('open','');
});

for(const agent of ['Codex','Claude','ZCode','OpenCode','Grok Code'])test(`${agent} accepts the same normalized command schema`,async({page})=>{
  await openTools(page,[toolMessage('normalized',{schema:'termlink.tool.v1',kind:'command',name:'shell',command:'echo shared',output:'shared output',exitCode:0,durationMs:0})],{session:{agent}});
  const tool=card(page,'normalized');await expect(tool.locator('.tool-command')).toContainText('echo shared');
  await expect(tool.locator('.tool-output')).toContainText('shared output');await expect(tool.locator('.tool-metrics')).toContainText('Exit code0');
  await expect(tool.locator('.tool-metrics')).toContainText('Duration0 ms');
});

test('normalized file, MCP, search, agent and image details use controlled actions',async({page})=>{
  const fixtures:Record<string,unknown>={
    file:{schema:'termlink.tool.v1',kind:'file',files:[{path:'src/应用.ts',diff:'@@ -1 +1 @@\n-old\n+new'}]},
    mcp:{schema:'termlink.tool.v1',kind:'mcp',name:'local/read',input:'{"file":"notes.txt"}',output:'MCP result',progress:'Fetched 1 of 2'},
    search:{schema:'termlink.tool.v1',kind:'search',input:'fixture query',urls:[{title:'Fixture source',url:'https://fixture.invalid/results'}]},
    agent:{schema:'termlink.tool.v1',kind:'agent',children:[{id:'worker-a',status:'completed',prompt:'Tests passed'},{id:'worker-b',status:'running',prompt:'Checking UI'}]},
    image:{schema:'termlink.tool.v1',kind:'image',files:[{path:'out/preview.png'}]},
    tool:{schema:'termlink.tool.v1',kind:'tool',name:'custom-plugin',input:'opaque args',output:'opaque result'},
  };
  // The shared group stays expanded so each controlled action can be exercised.
  const messages=Object.entries(fixtures).map(([id,payload])=>toolMessage(id,payload));
  await openTools(page,messages);
  const scroller=page.locator('[data-virtuoso-scroller]');await scroller.evaluate(el=>el.scrollTop=0);
  const file=card(page,'file');await expect(file.locator('.tool-diff')).toContainText('+new');
  await file.locator('.tool-file-diff>summary').click();await expect(file).toHaveAttribute('open','');
  await file.getByRole('button',{name:'Open file · src/应用.ts',exact:true}).click();
  await expect(page.getByRole('dialog')).toContainText('file body');await page.getByRole('button',{name:'Close',exact:true}).click();
  await expect(card(page,'mcp').locator('.tool-output')).toContainText('MCP result');
  await card(page,'search').getByRole('button',{name:/Fixture source/}).click();
  await expect(card(page,'agent').locator('.tool-children')).toContainText('worker-aCompletedTests passed');
  await expect(card(page,'agent').locator('.tool-children')).toContainText('worker-brunningChecking UI');
  await expect(card(page,'image').getByRole('button',{name:'View image · out/preview.png'})).toBeAttached();
  await expect(card(page,'tool').locator('.tool-output')).toContainText('opaque result');
  expect(await page.evaluate(()=>(window as any).calls.filter((r:any)=>['open-file','open-link'].includes(r.action)).map((r:any)=>[r.action,r.path||r.url])))
    .toEqual([['open-file','src/应用.ts'],['open-link','https://fixture.invalid/results']]);
});

test('incremental output replaces the same card and failure/cancellation remain explicit',async({page})=>{
  const start={schema:'termlink.tool.v1',kind:'command',command:'run tests',output:'first line',progress:'1 / 3'};
  const session=await openTools(page,[toolMessage('stream',start,'commandExecution','running')],{session:{state:'running',phase:'tool'}});
  const tool=card(page,'stream');await expect(tool.locator('.tool-output')).toContainText('first line');
  await tool.locator('.tool-raw>summary').click();
  await page.evaluate(({session,messages})=>(window as any).termlinkUpdate({session,messages,reset:false}),{
    session,messages:[toolMessage('stream',{...start,output:'first line\nsecond line\nFAIL fixture',progress:'3 / 3',exitCode:2,durationMs:1200},'commandExecution','failed')],
  });
  await expect(page.locator('.tool-card')).toHaveCount(1);await expect(tool).toHaveAttribute('open','');
  await expect(tool.locator('.tool-output')).toContainText('first line\nsecond line\nFAIL fixture');
  await expect(tool.locator('.tool-state')).toHaveText('failed');await expect(tool.locator('.tool-metrics .danger')).toHaveText('2');
  await expect(tool.locator('.tool-raw')).toHaveAttribute('open','');await expect(tool.locator('.tool-raw pre')).toContainText('FAIL fixture');
  await page.evaluate(({session,messages})=>(window as any).termlinkUpdate({session,messages,reset:false}),{session,messages:[toolMessage('stream',{...start,output:'Interrupted'},'commandExecution','cancelled')]});
  await expect(tool.locator('.tool-state')).toHaveText('Cancelled');
  expect(await page.evaluate(()=>(window as any).calls.some((r:any)=>['answer','interrupt','exec','open-link'].includes(r.action)))).toBe(false);
});

test('paged, locally clipped and upstream truncated content use honest hints and full-message requests',async({page})=>{
  const preview={schema:'termlink.tool.v1',kind:'command',command:'cat logs',output:'preview',truncated:true};
  const full=JSON.stringify({...preview,output:'FULL MESSAGE\n'+('line\n'.repeat(9000)),truncated:false});
  const message={...toolMessage('paged',preview),fullLength:full.length};
  await openTools(page,[message],{full:{paged:full},delay:350});
  const tool=card(page,'paged');await expect(tool.locator('.tool-truncation')).toHaveText('Showing partial content');
  const button=tool.locator('.tool-full');await expect(button).toHaveText('View full content');await button.click();await expect(button).toBeDisabled();
  await expect(page.getByRole('dialog')).toContainText('FULL MESSAGE');
  expect(await page.evaluate(()=>(window as any).calls.filter((r:any)=>r.action==='message').map((r:any)=>r.messageId))).toEqual(['paged']);
  await page.getByRole('button',{name:'Close',exact:true}).click();
  await page.evaluate(({session,messages})=>(window as any).termlinkUpdate({session,messages,reset:false}),{session:toolSession,messages:[toolMessage('paged',{...preview,output:'x'.repeat(25_000)+'LATEST LINE'})]});
  await expect(tool.locator('.tool-output')).toContainText('LATEST LINE');await expect(tool).toContainText('Showing the latest output only');
  await expect(tool.locator('.tool-truncation')).toContainText('The Agent or host retained only part of the content');
  await expect(tool.getByRole('button',{name:'View full content'})).toHaveCount(0);
  await tool.getByRole('button',{name:'View retained content'}).click();await expect(page.getByRole('dialog')).toContainText('LATEST LINE');
  expect(await page.evaluate(()=>(window as any).calls.filter((r:any)=>r.action==='message').length)).toBe(1);
});

test('unknown and malicious tool content stays visible as text with no automatic requests or HTML',async({page})=>{
  const external:string[]=[];page.on('request',r=>{if(!r.url().startsWith('http://127.0.0.1:'))external.push(r.url());});
  const poison='<script>window.hacked=1</script><img src="https://unsafe.invalid/leak" onerror="window.hacked=2"><iframe src="https://unsafe.invalid"></iframe>';
  await openTools(page,[toolMessage('unknown','unrecognized\n'+poison,'future-tool','future-status'),toolMessage('payload',{
    schema:'termlink.tool.v1',kind:'image',input:poison,output:poison,raw:poison,
    files:[{path:'javascript:alert(1)'},{path:'data:text/html,bad'}],
    urls:[{title:'Bad JS',url:'javascript:alert(1)'},{url:'https://user:password@unsafe.invalid/private'},{title:'Safe but inert',url:'https://unsafe.invalid/view'}],
  })]);
  await expect(card(page,'unknown').locator('.tool-fallback')).toContainText(poison);
  await expect(card(page,'unknown').locator('.tool-state')).toHaveText('Unknown status');
  await expect(page.locator('.tool-card script,.tool-card img,.tool-card iframe,.tool-card a')).toHaveCount(0);
  await expect(card(page,'payload').locator('.tool-files button')).toHaveCount(0);
  await expect(card(page,'payload').locator('.tool-urls button')).toHaveCount(1);
  expect(external).toEqual([]);expect(await page.evaluate(()=>(window as any).hacked)).toBeUndefined();
  expect(await page.evaluate(()=>(window as any).calls.some((r:any)=>['open-link','open-file','artifact.read'].includes(r.action)))).toBe(false);
});

test('narrow screens and large text contain long paths, diffs and child tasks in both themes',async({page})=>{
  await page.setViewportSize({width:320,height:740});
  const longPath='src/'+('very-long-folder/'.repeat(16))+'文件.ts';
  const message=toolMessage('narrow',{schema:'termlink.tool.v1',kind:'file',name:'custom-'+('long'.repeat(30)),
    files:[{path:longPath,diff:'-old\n+'+'new'.repeat(300)}],children:[{id:'agent-'+('long'.repeat(80)),status:'running',prompt:'分析任务'}],output:'token'.repeat(300)});
  let session=await openTools(page,[message],{session:{fontScale:2,language:'zh',dark:true}});
  const tool=card(page,'narrow');await expect(tool).toHaveAttribute('data-tool-kind','file');
  await expect(tool).toContainText('涉及文件');await expect(tool.locator('.tool-state')).toHaveText('已完成');
  const verify=async()=>{
    expect(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth)).toBe(true);
    expect(await tool.evaluate(el=>el.scrollWidth<=el.clientWidth+1)).toBe(true);
    const bounds=(await tool.boundingBox())!;expect(bounds.x+bounds.width).toBeLessThanOrEqual(320);
    expect((await tool.getByRole('button',{name:'打开文件 · '+longPath,exact:true}).boundingBox())!.height).toBeGreaterThanOrEqual(44);
  };
  await verify();await page.locator('[data-virtuoso-scroller]').evaluate(el=>el.scrollTop=0);await page.screenshot({path:test.info().outputPath('tools-narrow-dark.png')});
  session={...session,dark:false};await page.evaluate(session=>(window as any).termlinkUpdate({session,messages:[],reset:false}),session);
  await verify();await page.screenshot({path:test.info().outputPath('tools-narrow-light.png')});
});

test('tool updates keep a pending answer draft and do not change a saved expanded state',async({page})=>{
  const pending={id:'p1',kind:'question',title:'Question',summary:'',detail:'fixture',canApprove:true,questions:[{id:'q1',text:'Scope',options:['Tests'],multiSelect:false}]};
  const start=toolMessage('work',{schema:'termlink.tool.v1',kind:'mcp',output:'first'},'tool','running');
  const session=await openTools(page,[start],{session:{pending:[pending],state:'running',phase:'tool'}});
  await page.getByPlaceholder('Additional answer').fill('keep the draft');await page.getByLabel('Tests',{exact:true}).check();
  await page.evaluate(({session,messages})=>(window as any).termlinkUpdate({session,messages,reset:false}),{session,messages:[toolMessage('work',{schema:'termlink.tool.v1',kind:'mcp',output:'first\nsecond'},'tool','running')]});
  await expect(page.getByPlaceholder('Additional answer')).toHaveValue('keep the draft');await expect(page.getByLabel('Tests',{exact:true})).toBeChecked();
  await expect(card(page,'work')).toHaveAttribute('open','');
  expect(await page.evaluate(()=>(window as any).calls.some((r:any)=>r.action==='answer'))).toBe(false);
});

test('tool-only history stays virtualized and streaming below the reader preserves the anchor',async({page})=>{
  const messages=Array.from({length:1000},(_,i)=>toolMessage(`t${i}`,{schema:'termlink.tool.v1',kind:'command',command:`echo ${i}`,output:`result ${i}`}));
  const session=await openTools(page,messages,{ui:{expanded:['tools:t400','t400']}});
  const scroller=page.locator('[data-virtuoso-scroller]');
  expect(await page.locator('.tool-group').count()).toBeLessThan(20);
  expect(await page.locator('article').count()).toBeLessThan(400);
  await scroller.evaluate(el=>el.scrollTop=el.scrollHeight*.4);
  await expect(page.getByRole('button',{name:'↓ Latest'})).toBeVisible();
  const position=await scroller.evaluate(el=>el.scrollTop);
  await page.evaluate(({session,message})=>(window as any).termlinkUpdate({session,messages:[message],reset:false}),{
    session,message:toolMessage('t999',{schema:'termlink.tool.v1',kind:'command',command:'echo 999',output:'latest streaming output'},'tool','running'),
  });
  await page.waitForTimeout(150);
  expect(Math.abs(await scroller.evaluate(el=>el.scrollTop)-position)).toBeLessThan(10);
  expect(await page.locator('.tool-group').count()).toBeLessThan(20);
  expect(await page.locator('article').count()).toBeLessThan(400);
  expect(await page.locator('.tool-body').count()).toBeLessThanOrEqual(1);
});


test('streaming output follows its tail until the reader scrolls up',async({page})=>{
  const lines=Array.from({length:200},(_,i)=>`line ${i}`).join('\n');
  const payload={schema:'termlink.tool.v1',kind:'command',command:'logs',output:lines};
  const session=await openTools(page,[toolMessage('log',payload,'tool','running')]);
  const output=card(page,'log').locator('.tool-output pre');
  const distance=()=>output.evaluate(el=>el.scrollHeight-el.scrollTop-el.clientHeight);
  await expect.poll(distance).toBeLessThan(2);
  await page.evaluate(({session,message})=>(window as any).termlinkUpdate({session,messages:[message],reset:false}),
    {session,message:toolMessage('log',{...payload,output:lines+'\nfollowed line'},'tool','running')});
  await expect(output).toContainText('followed line');await expect.poll(distance).toBeLessThan(2);
  await output.evaluate(el=>{el.scrollTop=40;el.dispatchEvent(new Event('scroll',{bubbles:true}));});
  await page.evaluate(({session,message})=>(window as any).termlinkUpdate({session,messages:[message],reset:false}),
    {session,message:toolMessage('log',{...payload,output:lines+'\nfollowed line\nunfollowed line'},'tool','running')});
  await expect(output).toContainText('unfollowed line');expect(await output.evaluate(el=>el.scrollTop)).toBeCloseTo(40,0);
});
