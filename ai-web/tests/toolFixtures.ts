import type {Message,Session} from '../src/bridge';
export const toolSession:Session={id:'tools-session',agent:'Codex',state:'idle',phase:'idle',turnStartedAtMs:0,lastActivityAtMs:0,
  canControl:true,busy:false,uncertain:false,historyBefore:0,truncated:false,pending:[],plan:[],usage:'',diff:'',error:'',artifacts:true,search:true,dark:false,fontScale:1,language:'en'};
export function toolMessage(id:string,payload:unknown,kind='tool',status='completed'):Message {
  const text=typeof payload==='string'?payload:JSON.stringify(payload);
  return {id,role:'tool',kind,text,title:'',summary:'',status,fullLength:text.length};
}
export const legacyTools=[
  {agent:'Codex',kind:'commandExecution',expected:'command',payload:{type:'commandExecution',command:'printf codex',cwd:'/repo',aggregatedOutput:'codex output',exitCode:0,durationMs:25}},
  {agent:'Claude',kind:'tool_use',expected:'command',payload:JSON.stringify({type:'tool_use',id:'c1',name:'Bash',input:{command:'printf claude',cwd:'/repo'}})+'\n'+JSON.stringify({type:'tool_result',tool_use_id:'c1',content:[{type:'text',text:'claude output'}]})},
  {agent:'ZCode',kind:'tool',expected:'command',payload:{toolCallId:'z1',toolName:'Bash',input:{command:'printf zcode'},output:'zcode output',status:'completed'}},
  {agent:'OpenCode',kind:'acp',expected:'command',payload:{kind:'execute',title:'Run fixture',rawInput:{command:'printf opencode'},content:[{type:'content',content:{type:'text',text:'opencode output'}}],status:'completed'}},
  {agent:'Grok Code',kind:'acp',expected:'command',payload:{kind:'execute',rawInput:{command:'printf grok'},rawOutput:'grok output',status:'completed'}},
] as const;
