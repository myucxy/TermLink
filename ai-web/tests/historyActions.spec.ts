import {test,expect} from '@playwright/test';
const session={id:'history',agent:'Codex',state:'idle',phase:'idle',turnStartedAtMs:0,lastActivityAtMs:0,canControl:true,busy:false,uncertain:false,historyBefore:0,truncated:false,pending:[],plan:[],usage:'',diff:'',error:'',artifacts:false,search:false,dark:false,fontScale:1,language:'en',historyActions:true};
test('history actions require explicit confirmation and preserve selected native turn',async({page})=>{
 await page.addInitScript(value=>{const w=window as any;w.calls=[];w.TermLink={postMessage:(raw:string)=>{const r=JSON.parse(raw);w.calls.push(r);if(r.action==='ready')setTimeout(()=>w.termlinkUpdate({session:value,messages:[],reset:true}),0);const reply=r.action==='history.actions'?{supported:true,turns:[{id:'turn-1',status:'completed'}],review:true,revert:true,forkAtTurn:true,truncated:false}:true;setTimeout(()=>w.termlinkReply({id:r.id,value:reply}),0);}};},session);
 await page.goto('/');await page.getByRole('button',{name:'History actions',exact:true}).click();
 await page.getByRole('button',{name:'Revert before this turn'}).click();
 await expect(page.getByRole('dialog',{name:'Confirm conversation action'})).toBeVisible();
 expect(await page.evaluate(()=>(window as any).calls.filter((x:any)=>x.action==='revert'))).toEqual([]);
 await page.getByRole('button',{name:'Confirm',exact:true}).click();
 const call=await page.evaluate(()=>(window as any).calls.find((x:any)=>x.action==='revert'));
 expect(call).toMatchObject({turnId:'turn-1',confirmed:true});
 await expect(page.getByText('Workspace files will not be restored or isolated.')).toHaveCount(0);
});
test('non-supporting Agent and read-only sessions never offer writable history controls',async({page})=>{
 await page.addInitScript(value=>{const w=window as any;w.TermLink={postMessage:(raw:string)=>{const r=JSON.parse(raw);if(r.action==='ready')setTimeout(()=>w.termlinkUpdate({session:value,messages:[],reset:true}),0);setTimeout(()=>w.termlinkReply({id:r.id,value:true}),0);}};},{...session,agent:'OpenCode',historyActions:false,canControl:false});
 await page.goto('/');await expect(page.getByRole('button',{name:'History actions',exact:true})).toHaveCount(0);
});
