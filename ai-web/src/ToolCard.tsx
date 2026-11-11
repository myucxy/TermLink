import {useLayoutEffect,useMemo,useRef,useState} from 'react';
import type {Message} from './bridge';
import {t} from './i18n';
import {parseToolPresentation,safeToolPath,safeToolUrl,type ToolKind} from './toolPresentation';

type Props={pathStyle?:string;message:Message;open:boolean;onExpand:(open:boolean)=>void;onAction:(action:string,args?:Record<string,unknown>)=>Promise<unknown>};
const kindLabels:Record<ToolKind,string>={command:'运行命令',file:'文件操作',mcp:'MCP 工具',search:'搜索',agent:'子 Agent 任务',image:'图片',tool:'工具操作'};
function statusLabel(status?:string):string {
  switch(status){
    case 'running':case 'in_progress':return t('执行中');
    case 'completed':case 'success':case 'succeeded':return t('已完成');
    case 'failed':case 'error':return t('失败');
    case 'cancelled':case 'canceled':case 'interrupted':return t('已取消');
    case 'pending':case 'waiting':return t('等待中');
    default:return t('状态未知');
  }
}
function TextSection({label,value,className='',follow=false}:{label:string;value?:string;className?:string;follow?:boolean}) {
  const pre=useRef<HTMLPreElement>(null);const tail=useRef(true);
  useLayoutEffect(()=>{if(follow&&tail.current&&pre.current)pre.current.scrollTop=pre.current.scrollHeight;},[value,follow]);
  return value!==undefined&&value!==''?<section className={`tool-section ${className}`} aria-label={label}><h4>{label}</h4><pre ref={pre} tabIndex={0}
    onScroll={e=>{const el=e.currentTarget;tail.current=el.scrollHeight-el.scrollTop-el.clientHeight<24;}}>{value}</pre></section>:null;
}
function ToolDiff({value}:{value:string}) {
  return <pre className="tool-diff" tabIndex={0} aria-label={t('文件变更')}>{value.split('\n').map((line,i)=><span key={i} className={line.startsWith('+')?'added':line.startsWith('-')?'removed':line.startsWith('@@')?'hunk':undefined}>{line||' '}{'\n'}</span>)}</pre>;
}
export function ToolCard({message:m,open,onExpand,onAction,pathStyle}:Props) {
  const data=useMemo(()=>parseToolPresentation(m.text,m.kind),[m.text,m.kind]);
  const [rawOpen,setRawOpen]=useState(false);
  const [reading,setReading]=useState(false);
  const paged=m.fullLength>m.text.length;
  const title=m.title?t(m.title):data.name||t(kindLabels[data.kind]);
  const summary=m.summary||data.command||data.files[0]?.path||data.progress||'';
  const status=['running','failed','completed','cancelled'].includes(m.status)?m.status:'unknown';
  const full=async()=>{
    setReading(true);
    try{
      const text=paged?await onAction('message',{messageId:m.id}):m.text;
      if(typeof text==='string'&&text)await onAction('show-text',{text});
    }finally{setReading(false);}
  };
  return <details className="tool tool-card" data-tool-kind={data.kind} data-tool-status={status} open={open}
    onToggle={e=>{if(e.target===e.currentTarget&&e.currentTarget.open!==open)onExpand(e.currentTarget.open);}}>
    <summary><span className="tool-heading"><span className="tool-symbol" aria-hidden="true">{{running:'◉',failed:'!',completed:'✓',cancelled:'×',unknown:'?'}[status]}</span><strong title={title}>{title}</strong><span className="tool-state">{statusLabel(m.status)}</span></span>
      {summary&&<small>{summary}</small>}</summary>
    {open&&<div className="tool-body">
      {!data.fallback&&<>
        <div className="tool-meta"><span>{t(kindLabels[data.kind])}</span>{data.name&&data.name!==title&&<code>{data.name}</code>}</div>
        {data.progress&&<p className="tool-progress" role="status">{data.progress}</p>}
        <TextSection label={t('命令')} value={data.command} className="tool-command"/>
        {data.cwd&&<p className="tool-cwd"><span>{t('工作目录')}</span> <code>{data.cwd}</code></p>}
        <TextSection label={t('输入')} value={data.input} className="tool-input"/>
        <TextSection label={t('输出')} value={data.output} className="tool-output" follow/>
        {data.outputClipped&&<p className="hint">{t('仅显示最近的输出')}</p>}
        {(data.exitCode!==undefined||data.durationMs!==undefined)&&<dl className="tool-metrics">
          {data.exitCode!==undefined&&<><dt>{t('退出码')}</dt><dd className={data.exitCode!==0?'danger':undefined}>{data.exitCode}</dd></>}
          {data.durationMs!==undefined&&<><dt>{t('耗时')}</dt><dd>{data.durationMs.toLocaleString()} ms</dd></>}
        </dl>}
        {data.files.length>0&&<section className="tool-section tool-files" aria-label={t('涉及文件')}><h4>{t('涉及文件')}</h4>{data.files.map((file,i)=><div className="tool-file" key={file.path}>
          {safeToolPath(file.path,pathStyle)?<button className="tool-reference" onClick={()=>void onAction('open-file',{path:file.path})} aria-label={`${t(data.kind==='image'?'查看图片':'打开文件')} · ${file.path}`}>{file.path}</button>:<code>{file.path}</code>}
          {file.diff&&<details className="tool-file-diff" open={i===0}><summary>{t('文件变更')}</summary><ToolDiff value={file.diff}/></details>}
        </div>)}</section>}
        {data.children.length>0&&<section className="tool-section tool-children" aria-label={t('子 Agent 任务')}><h4>{t('子 Agent 任务')}</h4><ul>{data.children.map((child,i)=><li key={`${i}:${child.id}`}>
          <div className="tool-child-heading"><code title={child.id}>{child.id}</code><span>{statusLabel(child.status)}</span></div>{child.prompt&&<p>{child.prompt}</p>}
        </li>)}</ul></section>}
        {data.urls.length>0&&<section className="tool-section tool-urls" aria-label={t('来源链接')}><h4>{t('来源链接')}</h4><ul>{data.urls.map(url=><li key={url.url}>
          {safeToolUrl(url.url)?<button className="tool-reference" onClick={()=>void onAction('open-link',{url:url.url})}>{url.title||url.url}{url.title&&<small>{url.url}</small>}</button>:<><span>{url.title}</span><code>{url.url}</code><small>{t('此链接不能打开')}</small></>}
        </li>)}</ul></section>}
        {data.kind==='image'&&<p className="hint">{t('图片通过文件或链接打开')}</p>}
      </>}
      {data.fallback?<section className="tool-section tool-fallback" aria-label={t('原始内容')}><h4>{t('原始内容')}</h4><p className="hint">{t('此工具内容尚无专用视图')}</p><pre tabIndex={0}>{data.raw||t('暂无工具详情')}</pre></section>:
        <details className="tool-raw" open={rawOpen} onToggle={e=>{if(e.target===e.currentTarget)setRawOpen(e.currentTarget.open);}}><summary>{t('原始内容')}</summary>{rawOpen&&<pre tabIndex={0}>{data.raw}</pre>}</details>}
      {(paged||data.truncated||data.clipped)&&<p className="hint tool-truncation">{t('仅显示部分内容')}{data.truncated&&!paged&&` · ${t('Agent 或主机仅保留了部分内容')}`}</p>}
      {(paged||data.clipped||data.truncated)&&<button className="tool-full" disabled={reading} onClick={()=>void full()}>{reading?t('正在读取…'):t(paged?'查看完整内容':'查看已保留内容')}</button>}
    </div>}
  </details>;
}
