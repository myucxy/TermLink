import {test,expect,type Page} from '@playwright/test';
import {toolSession} from './toolFixtures';
const capabilities={agent:'Claude',protocol:'claude',features:[
  {id:'mcp',label:'MCP tools',status:'available',detail:'Available through the connected host'},
  {id:'review',label:'Code review',status:'partial',detail:'Some capabilities need desktop access'},
  {id:'auth',label:'Remote service',status:'requires_auth',detail:'Sign in on the host'},
  {id:'future',label:'Future feature',status:'future-status',detail:''},
  {id:'unknown-label',label:'__proto__',status:'unknown',detail:'constructor'},
  {id:'skills',label:'技能清单',status:'not-reported',detail:'当前协议未报告此清单；不代表代理不能使用已配置的技能或工具。'},
  {id:'catalog',label:'工具清单',status:'available',detail:'代理报告 3 项；仅展示公开元数据，不证明执行权限或清单完整性。'},
],skills:[{name:'project-review',description:'Review the current project'}],tools:[{name:'workspace/read',status:'connected'}],
commands:[{name:'/help',description:'Available commands'}],truncated:true};
async function open(page:Page,flag=true,options:{failure?:boolean;delay?:number}={}) {
  const session={...toolSession,agent:'Claude',agentFeatures:flag};
  await page.addInitScript(({session,capabilities,options})=>{
    const win=window as any;win.calls=[];win.featureCount=0;
    win.TermLink={postMessage:(raw:string)=>{
      const r=JSON.parse(raw);win.calls.push(r);
      if(r.action==='ready')setTimeout(()=>win.termlinkUpdate({session,messages:[],reset:true}),0);
      if(r.action==='agent.features'){
        const count=++win.featureCount;
        const value=count===1?capabilities:{...capabilities,agent:'OpenCode',protocol:'acp',features:[{id:'fresh',label:'New session feature',status:'available',detail:''}]};
        setTimeout(()=>win.termlinkReply({id:r.id,value,error:count===1&&options.failure?'CAPABILITIES_UNAVAILABLE':undefined}),count===1?options.delay||0:0);
      }else setTimeout(()=>win.termlinkReply({id:r.id,value:true}),0);
    }};
  },{session,capabilities,options});
  await page.goto('/');await expect(page.getByRole('navigation',{name:'Conversation tools'})).toBeVisible();
  return session;
}
test('capabilities are opt-in, read only and rendered with friendly statuses',async({page})=>{
  const session=await open(page,false);
  await expect(page.getByRole('button',{name:'Agent capabilities'})).toHaveCount(0);
  expect(await page.evaluate(()=>(window as any).calls.some((r:any)=>r.action==='agent.features'))).toBe(false);
  await page.evaluate(session=>(window as any).termlinkUpdate({session,messages:[],reset:false}),{...session,agentFeatures:true});
  await page.getByRole('button',{name:'Agent capabilities'}).click();
  const panel=page.locator('.agent-features');
  await expect(panel).toContainText('MCP toolsAvailable');await expect(panel).toContainText('Code reviewPartially supported');
  await expect(panel).toContainText('Remote serviceSign-in needed');await expect(panel).toContainText('Future featureNot confirmed');
  await expect(panel).toContainText('__proto__Not confirmed');await expect(panel).toContainText('constructor');
  await expect(panel).toContainText('Skill catalogNot reported');await expect(panel).toContainText('The Agent reports 3 entries.');
  await expect(panel).toContainText('This protocol has not reported the catalog.');
  await expect(panel).toContainText('project-review');await expect(panel).toContainText('workspace/readConnected');await expect(panel).toContainText('/help');
  await expect(panel).toContainText('Showing partial content');await expect(panel.locator('pre,input,a,img,iframe')).toHaveCount(0);
  await expect(panel.getByRole('button')).toHaveCount(1);
  expect(await page.evaluate(()=>(window as any).calls.filter((r:any)=>r.action.startsWith('agent.')).map((r:any)=>r.action))).toEqual(['agent.features']);
  await page.evaluate(session=>(window as any).termlinkUpdate({session,messages:[],reset:false}),session);
  await expect(panel).toHaveCount(0);await expect(page.getByRole('button',{name:'Agent capabilities'})).toHaveCount(0);
});
test('capability failure offers retry and does not leave the panel busy',async({page})=>{
  await open(page,true,{failure:true});await page.getByRole('button',{name:'Agent capabilities'}).click();
  const panel=page.locator('.agent-features');await expect(panel.getByRole('alert')).toHaveText('Unable to read Agent capabilities. Please retry.');
  const refresh=panel.getByRole('button',{name:'Refresh'});await expect(refresh).toBeEnabled();await refresh.click();
  await expect(panel).toContainText('New session featureAvailable');await expect(panel.getByRole('alert')).toHaveCount(0);
});
for(const failure of [false,true])test(`a previous session capability ${failure?'failure':'response'} cannot populate the new session`,async({page})=>{
  const session=await open(page,true,{delay:450,failure});await page.getByRole('button',{name:'Agent capabilities'}).click();
  await expect.poll(()=>page.evaluate(()=>(window as any).featureCount)).toBe(1);
  await page.evaluate(session=>(window as any).termlinkUpdate({session,messages:[],reset:true}),{...session,id:'different',agent:'OpenCode'});
  await expect(page.locator('.panel')).toHaveCount(0);
  await page.getByRole('button',{name:'Agent capabilities'}).click();await expect(page.locator('.agent-features')).toContainText('New session featureAvailable');
  await page.waitForTimeout(500);await expect(page.locator('.agent-features')).not.toContainText('MCP tools');
  await expect(page.locator('.feature-agent')).toContainText('OpenCode');await expect(page.getByRole('alert')).toHaveCount(0);
});
