import {t, setLanguage} from './i18n';
import React, {memo, useCallback, useEffect, useRef, useState} from 'react';
import {createRoot} from 'react-dom/client';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import {Virtuoso, type VirtuosoHandle} from 'react-virtuoso';
import {request, staticHtml, type Message, type Pending, type Session, type Update, type UiState, type QuestionDraft} from './bridge';
import {timelineRows,questionSchema,type TimelineRow} from './timeline';
import './style.css';
import {CodeBlock} from './CodeBlock';

type Preview = {name: string; text?: string; mime: string; url?: string; fileId?: string};
type Artifact = {id: string; name: string; mime: string; size: number; version: string};
function FolderIcon(){return <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" aria-hidden="true"><path d="M3 7V5a1 1 0 0 1 1-1h5l3 3h8a1 1 0 0 1 1 1v11a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1Z"/></svg>;}
const labels: Record<string,string> = {waiting:'等待 Agent', thinking:'思考中', generating:'正在回复', tool:'执行工具', approval:'等待处理', compacting:'压缩上下文', starting:'正在启动', idle:'已完成', running:'正在生成'};
type Actions=(action:string,args?:Record<string,unknown>)=>Promise<any>;
type TimelineContext={session:Session;act:Actions;ui:UiState;save:(next:UiState)=>void};
// Stable component identities preserve approval input during streaming updates.
function TimelineHeader({context}:{context?:TimelineContext}) {return context&&context.session.historyBefore>0?<button className="history" onClick={()=>context.act('history')}>{t('加载更早消息')}</button>:context?.session.truncated?<p className="hint">{t('已到历史保留边界')}</p>:null;}
function TimelineFooter({context}:{context?:TimelineContext}) {if(!context)return null;const {session,act,ui,save}=context;return <div className="pending-list">{session.pending.map(p=><Interaction key={p.id+questionSchema(p)} value={p} draft={ui.questions?.[p.id]} onDraft={draft=>save({questions:{...ui.questions,[p.id]:draft}})} disabled={!session.canControl||session.busy||session.uncertain} onAnswer={(decision,answers)=>act('answer',{requestId:p.id,decision,answers})}/>)}</div>;}
const timelineComponents={Header:TimelineHeader,Footer:TimelineFooter};

function App() {
  const [session,setSession] = useState<Session>();
  const [messages,setMessages] = useState<Message[]>([]);
  const [rows,setRows]=useState<TimelineRow[]>([]); const previousRows=useRef<TimelineRow[]>([]);
  const [ui,setUi]=useState<UiState>({}); const uiRef=useRef<UiState>({});const dirty=useRef(false);
  const [initial,setInitial]=useState<{index:number|'LAST';align:'start'|'end';offset?:number}>({index:'LAST',align:'end'});
  const [restoreAnchor,setRestoreAnchor]=useState<UiState['anchor']>();
  const restoring=useRef(false);
  const pendingSchemas=useRef(new Map<string,string>());
  const [firstIndex,setFirstIndex]=useState(100000);
  const messageMap = useRef(new Map<string,Message>()); const order = useRef<string[]>([]);
  const [error,setError] = useState(''); const [panel,setPanel] = useState<'files'|'search'|'usage'|'diff'|null>(null);
  const [files,setFiles] = useState<Artifact[]>([]); const [query,setQuery] = useState('');
  const [fileQuery,setFileQuery] = useState('');
  const [fullDiff,setFullDiff]=useState<string>();const [diffBusy,setDiffBusy]=useState(false);
  const [hits,setHits] = useState<Message[]>([]); const [preview,setPreview] = useState<Preview>();
  const [nextHit,setNextHit]=useState(-1);const [searching,setSearching]=useState(false);const [searched,setSearched]=useState(false);const [searchTruncated,setSearchTruncated]=useState(false);const searchVersion=useRef(0);
  const [bottom,setBottom] = useState(true); const list = useRef<VirtuosoHandle>(null); const [now,setNow] = useState(Date.now());
  useEffect(()=>{
    window.termlinkBack=()=>{if(preview)setPreview(undefined);else setPanel(null);};
    void request('back-state',{canGoBack:!!preview||panel!==null}).catch(()=>{});
    return()=>{window.termlinkBack=undefined;};
  },[preview,panel]);
  const currentId = useRef('');
  const readingTimer=useRef<ReturnType<typeof setTimeout>|undefined>(undefined);
  useEffect(()=>()=>clearTimeout(readingTimer.current),[]);
  const saveUi=useCallback((next:UiState)=>{const value={...uiRef.current,...next};uiRef.current=value;setUi(value);
    // Question input is sent immediately, so navigating away cannot drop the debounce window.
    const owner=currentId.current;void request('ui-state',{state:value}).catch(()=>{if(owner===currentId.current){dirty.current=true;setError(t('页面状态尚未保存，请保留当前页面并重试'));}});
  },[]);
  useEffect(()=>{
    const flush=()=>{if(dirty.current&&currentId.current){dirty.current=false;void request('ui-state',{state:uiRef.current}).catch(()=>{dirty.current=true;});}};
    const timer=setInterval(flush,250);document.addEventListener('visibilitychange',flush);window.addEventListener('pagehide',flush);
    return()=>{flush();clearInterval(timer);document.removeEventListener('visibilitychange',flush);window.removeEventListener('pagehide',flush);};
  },[]);
  useEffect(() => {
    window.termlinkUpdate = (u: Update) => {
      if (!u.session || !Array.isArray(u.messages)) return;
      const newView=currentId.current !== u.session.id;
      if (u.reset || newView) { messageMap.current.clear(); order.current = []; previousRows.current=[];setFirstIndex(100000); }
      if (newView) { setPanel(null); setPreview(undefined); setHits([]); setError('');searchVersion.current++;setSearching(false);setNextHit(-1);setSearched(false);uiRef.current=u.uiState||{};setUi(uiRef.current);dirty.current=false; }
      pendingSchemas.current=new Map(u.session.pending.map(p=>[p.id,questionSchema(p)]));
      const questions=Object.fromEntries(Object.entries(uiRef.current.questions||{}).filter(([id,d])=>pendingSchemas.current.get(id)===d.schema));
      if(JSON.stringify(questions)!==JSON.stringify(uiRef.current.questions||{}))saveUi({questions});
      currentId.current = u.session.id;
      u.messages.forEach(m => messageMap.current.set(m.id,m));
      if (u.order) {
        order.current = u.order; const ids = new Set(u.order); for (const id of messageMap.current.keys()) if (!ids.has(id)) messageMap.current.delete(id);
      }
      else for (const m of u.messages) if (!order.current.includes(m.id)) order.current.push(m.id);
      const all=order.current.flatMap(id => { const m=messageMap.current.get(id); return m?[m]:[]; });
      const projected=timelineRows(all);const oldFirst=previousRows.current[0]?.id;
      const prepended=oldFirst?projected.findIndex(r=>r.messages.some(m=>m.id===oldFirst)):0;
      if(prepended>0)setFirstIndex(i=>i-prepended);
      previousRows.current=projected;setRows(projected);setMessages(all);
      if(newView){const anchor=uiRef.current.anchor;const at=uiRef.current.tail===false&&anchor?projected.findIndex(r=>r.messages.some(m=>m.id===anchor.id)):-1;
        restoring.current=uiRef.current.tail===false&&!!anchor&&at<0;setRestoreAnchor(restoring.current?anchor:undefined);
        setInitial(at>=0?{index:at,align:'start',offset:anchor?.offset||0}:{index:'LAST',align:'end'});}
      setLanguage(u.session.language); setSession(u.session);
      document.documentElement.dataset.theme = u.session.dark?'dark':'light';
      document.documentElement.style.fontSize = `${14 * Math.min(2,Math.max(.85,u.session.fontScale || 1))}px`;
    };
    void request('ready').catch(e => setError(e.message));
    return () => { window.termlinkUpdate = undefined; };
  },[]);
  useEffect(() => { if(session?.state !== 'running') return; const timer=setInterval(()=>setNow(Date.now()),1000); return ()=>clearInterval(timer); },[session?.state]);
  useEffect(() => () => { if(preview?.url) URL.revokeObjectURL(preview.url); },[preview?.url]);
  useEffect(()=>{setFullDiff(undefined);},[session?.diff,session?.diffLength,session?.id]);
  useEffect(()=>{if(!restoreAnchor)return;const at=rows.findIndex(r=>r.messages.some(m=>m.id===restoreAnchor.id));
    if(at>=0){list.current?.scrollToIndex({index:at,align:'start',offset:restoreAnchor.offset});restoring.current=false;setRestoreAnchor(undefined);}
  },[rows,restoreAnchor]);
  const act = useCallback(async(action:string,args:Record<string,unknown>={}) => {
    setError(''); const owner=currentId.current;
    try {
      if(action==='show-text'){setPreview({name:t('完整消息'),mime:'text/markdown',text:String(args.text)});return;}
      if(action==='open-link' && typeof args.url==='string' && !/^https?:/i.test(args.url)){action='open-file';args={path:args.url};}
      const value=await request(action,args);
      if(owner!==currentId.current) return;
      if(action==='open-file'){await inspect(value);return;}
      return value;
    } catch(e) { if(owner===currentId.current)setError((e as Error).message); }
  },[]);
  const inspect = async(file:Artifact) => {
    if (file.size > 8*1024*1024) { setError(t('预览上限为 8 MiB，请在电脑打开或使用下载')); return; }
    const owner=currentId.current; const chunks: Uint8Array[]=[]; let offset=0;
    try {
      do { const value = await request('artifact.read',{fileId:file.id,offset,version:file.version});
        if(owner!==currentId.current)return;
        const bytes=Uint8Array.from(atob(value.data),(c:string)=>c.charCodeAt(0));
        if(value.id!==file.id||value.version!==file.version||value.size!==file.size||value.size<0||value.size>8*1024*1024||
          value.offset!==offset||bytes.length>65536||value.nextOffset!==offset+bytes.length||value.nextOffset>value.size||value.nextOffset===offset&&offset<value.size) throw new Error(t('文件已变化或分块不连续，请重新读取'));
        chunks.push(bytes); offset=value.nextOffset;
        if(offset>=value.size) break;
      }while(offset<=8*1024*1024);
      const bytes=new Uint8Array(offset); let at=0; for(const chunk of chunks){bytes.set(chunk,at);at+=chunk.length;}
      if(file.mime.startsWith('image/') && file.mime!=='image/svg+xml') setPreview({name:file.name,mime:file.mime,fileId:file.id,url:URL.createObjectURL(new Blob([bytes],{type:file.mime}))});
      else if(file.mime.startsWith('text/') || file.mime==='application/json' || file.mime==='image/svg+xml') setPreview({name:file.name,mime:file.mime,fileId:file.id,text:new TextDecoder().decode(bytes)});
      else setError(t('此格式暂不支持页面内预览'));
    }catch(e){if(owner===currentId.current)setError((e as Error).message);}
  };
  const previewHtml=useCallback((text:string)=>setPreview({name:t('HTML 预览'),text,mime:'text/html'}),[]);
  const search=async(offset=0)=>{const version=++searchVersion.current;const owner=currentId.current;const term=query.trim();if(!term)return;
    setSearching(true);if(offset===0){setHits([]);setNextHit(-1);setSearched(false);}
    try{const value=await request('search',{query:term,offset});if(version!==searchVersion.current||owner!==currentId.current)return;
      setHits(old=>[...new Map([...(offset?old:[]),...value.items].map((m:Message)=>[m.id,m])).values()] as Message[]);
      setNextHit(Number.isInteger(value.nextOffset)&&value.nextOffset>offset?value.nextOffset:-1);setSearchTruncated(!!value.truncated);setSearched(true);
    }catch(e){if(version===searchVersion.current&&owner===currentId.current)setError((e as Error).message);}
    finally{if(version===searchVersion.current)setSearching(false);}
  };
  const changeQuery=(value:string)=>{searchVersion.current++;setQuery(value);setHits([]);setNextHit(-1);setSearched(false);setSearching(false);};
  const expand=(id:string,open:boolean)=>{const values=new Set(uiRef.current.expanded||[]);open?values.add(id):values.delete(id);saveUi({expanded:[...values].slice(-128)});};
  const recordReading=(element:EventTarget)=>{if(!(element instanceof HTMLElement)||!element.hasAttribute('data-virtuoso-scroller'))return;
    clearTimeout(readingTimer.current);const owner=currentId.current;
    readingTimer.current=setTimeout(()=>{
      if(!element.isConnected||owner!==currentId.current||restoring.current)return;
      const top=element.getBoundingClientRect().top;
      const row=[...element.querySelectorAll<HTMLElement>('[data-row-id]')].find(r=>r.getBoundingClientRect().bottom>top&&r.getBoundingClientRect().top<top+element.clientHeight);
      const tail=element.scrollHeight-element.scrollTop-element.clientHeight<24;
      if(!row&&!tail)return; // Virtual rows may not have mounted during the scroll event.
      uiRef.current={...uiRef.current,tail,anchor:row?{id:row.dataset.rowId!,offset:Math.min(0,Math.round(row.getBoundingClientRect().top-top))}:uiRef.current.anchor};dirty.current=true;
    },100);
  };
  const loadDiff=async()=>{setDiffBusy(true);const owner=currentId.current;let text='',offset=0,version='';
    try{do{const page=await request('diff.read',{offset,version});
      if(owner!==currentId.current)return;
      if(page.offset!==offset||page.nextOffset!==offset+page.text.length||page.size>262145||page.nextOffset>page.size||page.nextOffset===offset&&offset<page.size||version&&version!==page.version)throw new Error(t('变更记录已变化，请重新读取'));
      text+=page.text;offset=page.nextOffset;version=page.version;if(offset>=page.size)break;
    }while(offset<=262145);setFullDiff(text);
    }catch(e){if(owner===currentId.current)setError((e as Error).message);}finally{setDiffBusy(false);}
  };
  const openFiles = async() => {setPanel('files'); const value=await act('artifacts'); if(value){setFiles(value.files);if(value.truncated)setError(t('仅显示工作目录内的部分文件，可输入名称筛选'));}};
  if(!session) return <main className="empty">{t('正在连接会话…')}{error&&<p role="alert">{error}</p>}</main>;
  const elapsed=session.turnStartedAtMs>0?Math.max(0,Math.floor((now-session.turnStartedAtMs)/1000)):0;
  return <main>
    <nav className="tools" aria-label={t('会话工具')}>
      {session.search&&<button onClick={()=>setPanel(panel==='search'?null:'search')}>{t('⌕ 搜索')}</button>}
      {session.artifacts&&<button onClick={openFiles}>{t('▱ 文件')}</button>}
      <button onClick={()=>setPanel(panel==='diff'?null:'diff')}>{t('± 变更')}</button>
      {session.usage&&<button onClick={()=>setPanel(panel==='usage'?null:'usage')}>{t('用量')}</button>}
      <span className="grow"/>{session.state==='running'&&<button className="danger" disabled={!session.canControl||session.busy||session.uncertain} onClick={()=>act('interrupt')}>{t('停止')}</button>}
    </nav>
    {(session.state==='running'||session.pending.length>0)&&<div className="status" role="status"><i className={session.pending.length?'dot':'dot pulse'}/><span>{session.pending.length?`${t('等待处理')} · ${session.pending.length} ${t('项')}`:t(labels[session.phase])||t('正在生成')}</span>{session.state==='running'&&<time>{Math.floor(elapsed/60)}:{String(elapsed%60).padStart(2,'0')}</time>}</div>}
    {session.plan?.length>0&&<details className="plan"><summary>{t('计划')} · {session.plan.filter(s=>s.status==='completed').length}/{session.plan.length}</summary>{session.plan.map((s,i)=><p key={i}><span>{s.status==='completed'?'✓':s.status==='in_progress'?'◉':'○'}</span> {s.step}</p>)}</details>}
    {error&&<div role="alert" className="error" onClick={()=>setError('')}>{error} ×</div>}
    {restoreAnchor&&<div className="status"><span>{t('阅读位置在更早的历史中')}</span>{session.historyBefore>0?<button onClick={()=>act('history')}>{t('加载并恢复')}</button>:<span>{t('当前历史中已不可用')}</span>}<button onClick={()=>{restoring.current=false;setRestoreAnchor(undefined);saveUi({tail:true});}}>{t('回到最新')}</button></div>}
    {panel&&<section className="panel"><div className="panel-title"><strong>{{files:t('工作目录文件'),search:t('搜索会话'),usage:t('上下文与用量'),diff:t('文件变更')}[panel]}</strong><button onClick={()=>setPanel(null)}>{t('关闭')}</button></div>
      {panel==='search'&&<><form onSubmit={e=>{e.preventDefault();void search();}}><input value={query} maxLength={200} onChange={e=>changeQuery(e.target.value)} placeholder={t('输入要查找的内容')}/><button disabled={searching||!query.trim()}>{t('查找')}</button></form>{hits.map(hit=><button className="file" key={hit.id} onClick={async()=>{const m=rows.findIndex(r=>r.messages.some(m=>m.id===hit.id));if(m>=0){expand('tools:'+rows[m].id,true);expand(hit.id,true);setPanel(null);list.current?.scrollToIndex({index:m,align:'center'});}else{const text=await act('message',{messageId:hit.id});if(text)setPreview({name:t('消息内容'),mime:'text/markdown',text});}}}>{hit.text}</button>)}
        {searching&&<p role="status">{t('正在搜索…')}</p>}{searched&&hits.length===0&&<p className="hint">{t('没有匹配的消息')}</p>}
        {nextHit>=0&&<button disabled={searching} onClick={()=>void search(nextHit)}>{t('加载更多结果')}</button>}
        {searched&&searchTruncated&&<p className="hint">{t('仅搜索电脑当前保留的历史')}</p>}</>}
      {panel==='files'&&<><form onSubmit={async e=>{e.preventDefault();const v=await act('artifacts',{query:fileQuery});if(v)setFiles(v.files);}}><input value={fileQuery} maxLength={200} onChange={e=>setFileQuery(e.target.value)} placeholder={t('筛选文件名称')}/><button>{t('筛选')}</button></form>{files.map(file=><div className="file-row" key={file.id}><button className="file" onClick={()=>inspect(file)}><strong>{file.name}</strong><small>{file.id} · {Math.ceil(file.size/1024)} KB</small></button><button aria-label={`${t('打开所在目录')} · ${file.name}`} title={t('打开所在目录')} onClick={()=>act('open-directory',{fileId:file.id})}><FolderIcon/></button><button aria-label={`${t('保存')} ${file.name}`} onClick={()=>act('download',{fileId:file.id,version:file.version})}>{t('保存')}</button></div>)}</>}
      {panel==='usage'&&<Usage raw={session.usage}/>}
      {panel==='diff'&&<>{(session.diffLength||0)>session.diff.length&&fullDiff===undefined&&<button disabled={diffBusy||!session.diffRead} onClick={loadDiff}>{diffBusy?t('正在读取…'):t('读取完整变更')}</button>}
        {session.diffTruncated&&<p className="hint">{t('变更超过当前保留上限，完整内容请在电脑查看。')}</p>}<Diff text={(fullDiff??session.diff)||t('Agent 尚未报告文件变更')}/></>}
    </section>}
    <div className="timeline" onScrollCapture={e=>recordReading(e.target)}><Virtuoso key={session.id} ref={list} data={rows} computeItemKey={(_,r)=>r.id} firstItemIndex={firstIndex}
      atBottomStateChange={setBottom} followOutput={atBottom=>atBottom?'auto':false} initialTopMostItemIndex={initial}
      components={timelineComponents} context={{session,act,ui,save:saveUi}}
      itemContent={(_,row)=><div data-row-id={row.id}>{row.messages.length>1?<details className="tool-group" open={ui.expanded?.includes('tools:'+row.id)||false} onToggle={e=>{if(e.target===e.currentTarget)expand('tools:'+row.id,e.currentTarget.open);}}>
        <summary>{t('工具')} · {row.messages.length} {t('项')} <small>{row.messages.filter(m=>m.status==='running').length} {t('执行中')} · {row.messages.filter(m=>m.status==='failed').length} {t('失败')}</small></summary>
        {row.messages.map(m=><MessageCard key={m.id} message={m} agent={session.agent} onAction={act} onPreview={previewHtml} open={ui.expanded?.includes(m.id)||false} onExpand={open=>expand(m.id,open)}/>)}</details>:
        <MessageCard message={row.messages[0]} agent={session.agent} onAction={act} onPreview={previewHtml} open={ui.expanded?.includes(row.id)||false} onExpand={open=>expand(row.id,open)}/>}</div>}/>
      {!bottom&&<button className="latest" onClick={()=>list.current?.scrollToIndex({index:'LAST',align:'end',behavior:'auto'})}>{session.pending.length?t('查看待处理'):t('↓ 最新')}</button>}
      {messages.length===0&&session.pending.length===0&&<div className="empty-overlay">{t('描述要完成的任务')}</div>}
    </div>
    {preview&&<PreviewPane value={preview} onClose={()=>setPreview(undefined)} onDirectory={preview.fileId?()=>void act('open-directory',{fileId:preview.fileId}):undefined}/>}
  </main>;
}

const MessageCard=memo(function MessageCard({message:m,agent,onAction,onPreview,open,onExpand}:{message:Message;agent:string;onAction:(action:string,args?:Record<string,unknown>)=>Promise<any>;onPreview:(text:string)=>void;open:boolean;onExpand:(open:boolean)=>void}) {
  const content=<RichText text={m.text} onAction={onAction} onPreview={onPreview}/>;
  return <article className={`message ${m.role}`} data-message-id={m.id}>
    {m.kind==='reasoning'?<details className="reasoning" open={open} onToggle={e=>onExpand(e.currentTarget.open)}><summary><i className={m.status==='running'?'dot pulse':'dot'}/>{t('思考摘要')}</summary>{content}</details>:
      m.role==='tool'?<details className="tool" open={open} onToggle={e=>onExpand(e.currentTarget.open)}><summary>{{running:'◉',failed:'!',completed:'✓',cancelled:'×'}[m.status]||'?'} {m.title||t('工具操作')}<small>{m.summary}</small></summary><pre>{m.text}</pre></details>:
      <><div className="message-head"><span>{m.role==='user'?t('你'):agent}</span><button onClick={()=>onAction('copy',{text:m.text})}>{t('复制')}</button><button onClick={()=>onAction(m.role==='user'?'edit':'quote',{text:m.text})}>{m.role==='user'?t('追加到草稿'):t('引用')}</button></div>{content}</>}
    {m.fullLength>m.text.length&&<button onClick={async()=>{const text=await onAction('message',{messageId:m.id});if(text)await onAction('show-text',{text});}}>{t('查看完整内容')}</button>}
  </article>;
});
function RichText({text,onAction,onPreview}:{text:string;onAction:(action:string,args?:Record<string,unknown>)=>Promise<any>;onPreview:(text:string)=>void}) {
  return <Markdown remarkPlugins={[remarkGfm]} components={{
    a:({href,children})=><button className="link" onClick={()=>onAction('open-link',{url:href})}>{children}</button>,
    img:({alt,src})=><button className="link" onClick={()=>onAction('open-file',{path:src})}>▧ {alt||t('查看图片')}</button>,
    pre:({children})=>{ const element=React.Children.toArray(children)[0] as React.ReactElement<{children?:React.ReactNode;className?:string}>;
      const value=String(element?.props?.children||'').replace(/\n$/,''); const language=element?.props?.className?.replace('language-','')||'';
      return <CodeBlock value={value} language={language} onCopy={()=>onAction('copy',{text:value})} onPreview={()=>onPreview(value)}/>; },
  }}>{text}</Markdown>;
}
function Interaction({value:p,disabled,onAnswer,draft,onDraft}:{value:Pending;disabled:boolean;onAnswer:(d:string,a:Record<string,string[]>)=>void;draft?:QuestionDraft;onDraft:(d:QuestionDraft)=>void}) {
  const saved=draft?.schema===questionSchema(p)?draft:undefined;
  const answers=saved?.answers||{};const extra=saved?.extra||{};
  const setAnswers=(answers:Record<string,string[]>)=>onDraft({schema:questionSchema(p),answers,extra});
  const setExtra=(extra:Record<string,string>)=>onDraft({schema:questionSchema(p),answers,extra});
  return <section className="approval"><strong>{p.kind==='question'?t('需要你的回答'):p.title||t('需要批准')}</strong>
    {p.summary&&<p>{p.summary}</p>}<details><summary>{t('请求详情')}</summary><pre>{p.detail}</pre></details>
    {p.questions.map(q=><fieldset key={q.id} disabled={disabled}><legend>{q.text}</legend>{q.options.map(option=><label key={option}><input type={q.multiSelect?'checkbox':'radio'} name={q.id} checked={(answers[q.id]||[]).includes(option)} onChange={()=>setAnswers({...answers,[q.id]:q.multiSelect?(answers[q.id]||[]).includes(option)?answers[q.id].filter(x=>x!==option):[...(answers[q.id]||[]),option]:[option]})}/>{option}</label>)}<input placeholder={t('补充回答')} maxLength={4000} value={extra[q.id]||''} onChange={e=>setExtra({...extra,[q.id]:e.target.value})}/></fieldset>)}
    {!p.canApprove&&<p>{t('请在电脑端查看完整请求')}</p>}
    <div className="actions"><button disabled={disabled} onClick={()=>onAnswer('deny',{})}>{t('拒绝')}</button><button className="primary" disabled={disabled||!p.canApprove||p.questions.some(q=>!(answers[q.id]?.length||extra[q.id]?.trim()))} onClick={()=>onAnswer('allow',Object.fromEntries(p.questions.map(q=>[q.id,[...(answers[q.id]||[]),...(extra[q.id]?.trim()?[extra[q.id].trim()]:[])]])))}>{p.kind==='question'?t('提交回答'):t('允许本次')}</button></div>
  </section>;
}
function Usage({raw}:{raw:string}){
  let value:any;try{value=JSON.parse(raw);}catch{return <p>{t('Agent 暂未报告用量')}</p>;}
  const total=value.total||value.tokens||{}; const used=value.last?.totalTokens; const limit=value.modelContextWindow;
  const stats=[[t('输入'),total.inputTokens??total.input_tokens],[t('输出'),total.outputTokens??total.output_tokens],[t('缓存命中'),total.cachedInputTokens??total.cache_read_input_tokens],[t('累计'),total.totalTokens]];
  return <div className="usage">{Number.isFinite(used)&&limit>0&&<><label>{t('最近一轮')} {used.toLocaleString()} / {limit.toLocaleString()} tokens</label><progress max={limit} value={used}/><small>{t('仅显示 Agent 报告的上下文窗口用量')}</small></>}
    <dl>{stats.filter(([,v])=>typeof v==='number').map(([k,v])=><React.Fragment key={k}><dt>{k}</dt><dd>{Number(v).toLocaleString()} tokens</dd></React.Fragment>)}</dl>
    {typeof value.costUsd==='number'&&<p>{t('Agent 报告费用')} ${value.costUsd.toFixed(4)}</p>}<details><summary>{t('原始用量')}</summary><pre>{JSON.stringify(value,null,2)}</pre></details></div>;
}
function Diff({text}:{text:string}){return <>{text.split(/(?=^diff --git )/m).filter(Boolean).map((file,i)=>{const lines=file.split('\n');const header=lines[0].startsWith('diff --git ')?lines.shift()!.replace(/^diff --git .*? b\//,''):t('变更摘要');
  return <details key={i} open={i===0}><summary>{header} <span className="added">+{lines.filter(l=>l.startsWith('+')&&!l.startsWith('+++')).length}</span> <span className="removed">−{lines.filter(l=>l.startsWith('-')&&!l.startsWith('---')).length}</span></summary><pre className="diff">{lines.map((line,j)=><div key={j} className={line.startsWith('+')?'added':line.startsWith('-')?'removed':line.startsWith('@@')?'hunk':''}>{line||' '}</div>)}</pre></details>;
})}</>;}
function PreviewPane({value,onClose,onDirectory}:{value:Preview;onClose:()=>void;onDirectory?:()=>void}) {
  const [source,setSource]=useState(false);const [url,setUrl]=useState('');
  useEffect(()=>{if(value.mime!=='text/html'||!value.text)return;const u=URL.createObjectURL(new Blob([staticHtml(value.text)],{type:'text/html'}));setUrl(u);return()=>URL.revokeObjectURL(u);},[value]);
  return <section className="preview" role="dialog" aria-label={value.name}><div className="panel-title"><strong>{value.name}</strong>{onDirectory&&<button aria-label={t('打开所在目录')} title={t('打开所在目录')} onClick={onDirectory}><FolderIcon/></button>}{value.text&&<button onClick={()=>setSource(!source)}>{source?t('预览'):t('源码')}</button>}<button onClick={onClose}>{t('关闭')}</button></div>
    {value.url?<img className="image-preview" src={value.url} alt={value.name}/>:value.mime==='text/html'&&!source?<iframe sandbox="" referrerPolicy="no-referrer" src={url} title={t('隔离的静态 HTML 预览')}/>:<pre>{value.text}</pre>}
  </section>;
}
createRoot(document.getElementById('root')!).render(<App/>);
