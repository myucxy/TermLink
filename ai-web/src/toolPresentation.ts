// The host owns provider normalization. This reader also accepts retained, older
// transcripts; it never interprets tool output as HTML or invokes provider tools.
export type ToolKind = 'command'|'file'|'mcp'|'search'|'agent'|'image'|'tool';
export type ToolFile = {path:string;diff?:string};
export type ToolChild = {id:string;status?:string;prompt?:string};
export type ToolUrl = {title?:string;url:string};
export type ToolPresentation = {
  kind:ToolKind; name?:string; command?:string; cwd?:string; input?:string; output?:string;
  exitCode?:number; durationMs?:number; progress?:string;
  files:ToolFile[]; children:ToolChild[]; urls:ToolUrl[];
  raw:string; fallback:boolean; truncated:boolean; clipped:boolean; outputClipped:boolean;
};
type ObjectValue = Record<string,unknown>;
const object = (value:unknown):ObjectValue => value!==null&&typeof value==='object'&&!Array.isArray(value)?value as ObjectValue:{};
const string = (value:unknown) => typeof value==='string'?value:undefined;
const first = (...values:unknown[]) => values.find(value=>value!==null&&value!==undefined);
const text = (value:unknown):string|undefined => {
  if(value===undefined||value===null)return undefined;
  if(typeof value==='string')return value;
  try{return JSON.stringify(value,null,2);}catch{return undefined;}
};
const number = (value:unknown) => typeof value==='number'&&Number.isFinite(value)?value:undefined;
const kinds = new Set<ToolKind>(['command','file','mcp','search','agent','image','tool']);

export function safeToolUrl(value:string):boolean {
  if(value.length>4096||!/^https?:\/\//i.test(value)||/[\u0000-\u0020\u007f\\]/.test(value))return false;
  try{const url=new URL(value);return !!url.hostname&&!url.username&&!url.password;}catch{return false;}
}
export function safeToolPath(value:string,pathStyle?:string):boolean {
  if(pathStyle&&!['windows','posix'].includes(pathStyle))return false;
  if(pathStyle==='posix')return value.length>0&&value.length<=4096&&!/[\u0000-\u001f\u007f]/.test(value)&&!/^((?:https?|javascript|data|vbscript|file|blob):|\\\\)/i.test(value);
  if(pathStyle==='windows'&&/^(?:\/\/|\\\\)/.test(value))return value.length<=4096&&!/[\u0000-\u001f\u007f]/.test(value);
  return value.length>0&&value.length<=4096&&value.trim()===value&&!/[\u0000-\u001f\u007f]/.test(value)&&
    !/^(?:\/\/|\\\\)/.test(value)&&(!/^[a-z][a-z\d+.-]*:/i.test(value)||/^[a-z]:[\\/]/i.test(value));
}
function legacyKind(value:unknown,name:string):ToolKind {
  const kind=string(value)?.toLowerCase();
  if(kind&&kinds.has(kind as ToolKind)&&kind!=='tool')return kind as ToolKind;
  switch(kind){
    case 'commandexecution':case 'execute':return 'command';
    case 'filechange':case 'read':case 'edit':case 'delete':case 'move':return 'file';
    case 'mcptoolcall':return 'mcp';
    case 'websearch':case 'fetch':return 'search';
    case 'collabagenttoolcall':case 'subagent':return 'agent';
    case 'imagegeneration':case 'imagegenerationtoolcall':case 'imageview':return 'image';
  }
  const key=name.toLowerCase();
  if(key.startsWith('mcp__')||key.startsWith('mcp.'))return 'mcp';
  if(['bash','shell','shell_command','exec_command','execute_command','run_command','terminal'].includes(key))return 'command';
  if(['read','read_file','edit','write','write_file','multiedit','apply_patch'].includes(key))return 'file';
  if(['websearch','web_search','webfetch','web_fetch','glob','grep','search'].includes(key))return 'search';
  if(['agent','task','spawn_agent','send_input','wait_agent','close_agent'].includes(key))return 'agent';
  if(['view_image','image_generation','image_search'].includes(key))return 'image';
  return 'tool';
}
function command(value:unknown):string|undefined {
  if(typeof value==='string')return value;
  if(Array.isArray(value)&&value.every(v=>typeof v==='string'))return value.join(' ');
  return undefined;
}
function contentText(value:unknown):string|undefined {
  // Content blocks may contain images, resources or unknown data. Only extract
  // text when every block is text; the original remains available in Raw data.
  if(Array.isArray(value)&&value.every(v=>object(v).type==='text'&&typeof object(v).text==='string'))
    return value.map(v=>object(v).text).join('\n');
  const item=object(value);
  if(Array.isArray(item.content))return contentText(item.content);
  return text(value);
}
function unwrap(value:unknown):ObjectValue {
  let result=object(value);
  for(let i=0;i<3;i++){
    const child=first(result.item,result.toolCall,result.type==='tool.updated'?result.payload:undefined);
    if(child===undefined||!Object.keys(object(child)).length)break;
    result=object(child);
  }
  return result;
}

export function parseToolPresentation(source:string,messageKind=''):ToolPresentation {
  const data:ToolPresentation={kind:'tool',files:[],children:[],urls:[],raw:'',fallback:false,truncated:false,clipped:false,outputClipped:false};
  // Shared limits also apply to legacy payloads. A huge or malformed transcript
  // remains readable without mounting megabytes of text in a virtualized row.
  let remaining=48_000;
  const take=(value:unknown,max=12_000,tail=false):string|undefined=>{
    const raw=string(value);if(raw===undefined)return undefined;
    const size=Math.min(max,remaining);remaining-=Math.min(raw.length,size);
    if(raw.length<=size)return raw;
    data.clipped=true;if(tail)data.outputClipped=true;
    return tail?raw.slice(raw.length-size):raw.slice(0,size);
  };
  const raw=(value:string)=>{if(value.length>12_000)data.clipped=true;return value.slice(0,12_000);};
  const rows=(value:unknown):unknown[]=>{
    if(!Array.isArray(value))return [];
    if(value.length>24)data.clipped=true;
    return value.slice(0,24);
  };
  const addFile=(path:unknown,diff?:unknown)=>{
    const fullPath=string(path);if(!fullPath)return;
    // Never turn a shortened path into an actionable reference to another file.
    if(fullPath.length>4096){data.clipped=true;return;}
    const old=data.files.find(f=>f.path===fullPath);
    if(old){if(old.diff===undefined)old.diff=take(diff,8000);return;}
    if(data.files.length>=24){data.clipped=true;return;}
    data.files.push({path:fullPath,diff:take(diff,8000)});
  };
  const addUrl=(url:unknown,title?:unknown)=>{
    const fullUrl=string(url);if(!fullUrl)return;
    if(fullUrl.length>4096){data.clipped=true;return;}
    if(data.urls.some(v=>v.url===fullUrl))return;
    if(data.urls.length>=24){data.clipped=true;return;}
    data.urls.push({url:fullUrl,title:take(title,300)});
  };
  const addChild=(id:unknown,status?:unknown,prompt?:unknown)=>{
    if(typeof id!=='string'||!id||data.children.some(v=>v.id===id))return;
    if(data.children.length>=24){data.clipped=true;return;}
    data.children.push({id:take(id,512)!,status:take(status,80),prompt:take(prompt,2000)});
  };
  const fallback=()=>{data.fallback=true;data.raw=raw(source);return data;};
  if(source.length>1_048_576)return fallback();
  try{
    let values:unknown[];
    try{const parsed:unknown=JSON.parse(source);values=Array.isArray(parsed)?parsed:[parsed];}
    catch{
      // Claude's retained tool_use and tool_result were stored on separate lines.
      const lines=source.split('\n').filter(line=>line.trim());
      if(lines.length>64)return fallback();
      values=lines.map(line=>JSON.parse(line));
    }
    if(!values.length||!Object.keys(object(values[0])).length)return fallback();
    const value=unwrap(values[0]);
    if(value.schema!==undefined){
      if(values.length!==1||value.schema!=='termlink.tool.v1'||typeof value.kind!=='string'||!kinds.has(value.kind as ToolKind))return fallback();
      data.kind=value.kind as ToolKind;
      data.name=take(value.name,300);data.command=take(value.command,4000);data.cwd=take(value.cwd,4096);
      data.progress=take(value.progress,1000);
      // Output gets the first share of the display budget so incremental results
      // cannot disappear behind a large input. Tail clipping keeps recent output.
      data.output=take(value.output,16_000,true);data.input=take(value.input,8000);
      data.exitCode=number(value.exitCode);data.durationMs=number(value.durationMs);
      for(const file of rows(value.files)){const f=object(file);addFile(f.path,f.diff);}
      for(const child of rows(value.children)){const c=object(child);addChild(c.id,c.status,c.prompt);}
      for(const url of rows(value.urls)){const u=object(url);addUrl(u.url,u.title);}
      data.raw=raw(string(value.raw)||source);data.truncated=value.truncated===true;
    }else{
      // Never guess at unrelated multi-record data. Pair only one Claude call and
      // its result; mismatched IDs remain a visible raw fallback.
      const use=values.find(v=>object(v).type==='tool_use');
      const results=values.filter(v=>object(v).type==='tool_result');
      if(values.length>1&&(!use||values.some(v=>v!==use&&!results.includes(v))||results.some(v=>object(v).tool_use_id!==object(use).id)))return fallback();
      const root=use?object(use):value;
      const name=string(first(root.tool_name,root.toolName,root.name,root.tool))||'';
      data.kind=legacyKind(first(root.type==='tool_use'?undefined:root.type,root.kind,messageKind),name);
      const state=object(root.state);
      const input=first(root.rawInput,root.input,root.arguments,root.parameters,state.input);
      let args=object(input);
      if(typeof input==='string'){try{args=object(JSON.parse(input));}catch{/* display the original input */}}
      const result=results.length?results.map(v=>contentText(object(v).content)).filter(v=>v!==undefined).join('\n'):
        contentText(first(root.aggregatedOutput,root.rawOutput,root.output,root.result,state.output,root.type==='tool_result'?root.content:undefined));
      const failure=first(root.error,state.error);
      data.name=take(root.server&&name?`${String(root.server)}/${name}`:name,300);
      data.command=take(command(first(root.command,root.cmd,args.command,args.cmd)),4000);
      data.cwd=take(first(root.cwd,args.cwd,args.workdir,args.working_directory),4096);
      data.output=take([result,contentText(failure)].filter(v=>v!==undefined&&v!=='').join('\n')||undefined,16_000,true);
      const query=data.kind==='search'?first(root.query,root.pattern,object(root.action).query,object(root.action).queries):undefined;
      data.input=take(contentText(first(input,query)),8000);
      data.progress=take(text(first(root.progress,root.progressMessage)),1000);
      data.exitCode=number(first(root.exitCode,root.exit_code,state.exitCode));
      data.durationMs=number(first(root.durationMs,root.duration_ms,state.durationMs));
      for(const file of rows(first(root.files,root.changes))){const f=object(file);addFile(first(f.path,f.file_path),first(f.diff,f.patch));}
      for(const location of rows(root.locations))addFile(object(location).path);
      if(data.kind==='file'||data.kind==='image')addFile(first(root.path,root.file_path,args.file_path,args.path,args.filePath),first(root.diff,args.diff));
      if(data.files.length&&typeof args.old_string==='string'&&typeof args.new_string==='string'&&!data.files[0].diff)
        data.files[0].diff=take(replacementDiff(args.old_string,args.new_string),8000);
      // ACP wraps text/diff/resource blocks under content. No resource bytes or
      // URLs are loaded by this projection; links go through the existing bridge.
      const contents:string[]=[];
      for(const part of rows(root.content)){
        const block=object(part);const c=block.type==='content'?object(block.content):block;
        if(c.type==='text'&&typeof c.text==='string')contents.push(c.text);
        else if(c.type==='diff'){addFile(c.path,replacementDiff(string(c.oldText)||'',string(c.newText)||''));}
        else if(c.type==='resource_link'){
          if(typeof c.uri==='string'&&safeToolUrl(c.uri))addUrl(c.uri,c.name);
          else if(typeof c.uri==='string'&&safeToolPath(c.uri))addFile(c.uri);
        }
      }
      if(data.output===undefined&&contents.length)data.output=take(contents.join('\n'),16_000,true);
      for(const child of rows(first(root.children,root.agents))){const c=object(child);addChild(first(c.id,c.agentId,c.threadId),c.status,first(c.prompt,c.message));}
      for(const id of rows(root.receiverThreadIds)){const child=object(object(root.agentsStates)[String(id)]);addChild(id,child.status,first(child.message,root.prompt));}
      addChild(first(root.newThreadId,root.receiverThreadId),root.status,root.prompt);
      for(const url of rows(first(root.urls,root.sources,root.results))){const u=object(url);addUrl(typeof url==='string'?url:u.url,u.title);}
      addUrl(first(root.url,args.url),root.title);
      data.raw=raw(source);data.truncated=root.truncated===true;
    }
    if(data.exitCode!==undefined&&!Number.isInteger(data.exitCode))data.exitCode=undefined;
    if(data.durationMs!==undefined&&data.durationMs<0)data.durationMs=undefined;
    if(!data.command&&!data.input&&!data.output&&!data.progress&&!data.files.length&&!data.children.length&&!data.urls.length&&data.exitCode===undefined&&data.durationMs===undefined)
      data.fallback=true;
    return data;
  }catch{return fallback();}
}
function replacementDiff(before:string,after:string):string {
  // These are the provider's replacement snippets, not an inferred full-file diff.
  const lines=(value:string,prefix:string)=>value.split('\n').map(line=>prefix+line).join('\n');
  return lines(before,'-')+'\n'+lines(after,'+');
}
