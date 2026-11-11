import {useCallback,useEffect,useRef,useState} from 'react';
import {t} from './i18n';

type Feature={id:string;label:string;status:string;detail:string};
type Named={name:string;description:string;status:string};
type Capabilities={agent:string;protocol:string;features:Feature[];skills:Named[];tools:Named[];commands:Named[];truncated:boolean};
type Action=(action:string,args?:Record<string,unknown>)=>Promise<unknown>;
const record=(value:unknown):Record<string,unknown>=>value!==null&&typeof value==='object'&&!Array.isArray(value)?value as Record<string,unknown>:{};
const text=(value:unknown,max=2000)=>typeof value==='string'?value.slice(0,max):'';
function readCapabilities(input:unknown):Capabilities|undefined {
  const value=record(input);
  if(!Array.isArray(value.features))return undefined;
  let truncated=value.truncated===true;
  const take=(value:unknown,max=2000)=>{if(typeof value==='string'&&value.length>max)truncated=true;return text(value,max);};
  const rows=(value:unknown)=>{if(!Array.isArray(value))return [];if(value.length>100)truncated=true;return value.slice(0,100).map(record);};
  const features=rows(value.features).map((row,i)=>({id:take(row.id,200)||String(i),label:take(row.label,300)||take(row.id,200)||t('功能'),status:take(row.status,80),detail:take(row.detail)}));
  const named=(value:unknown)=>rows(value).filter(row=>typeof row.name==='string').map(row=>({name:take(row.name,300),description:take(row.description),status:take(row.status,80)}));
  const skills=named(value.skills),tools=named(value.tools),commands=named(value.commands);
  return {agent:take(value.agent,200),protocol:take(value.protocol,80),features,skills,tools,commands,truncated};
}
function statusLabel(value:string) {
  switch(value.toLowerCase().replaceAll('-','_')){
    case 'available':case 'ready':case 'supported':case 'enabled':return t('可用');
    case 'reported':case 'observed':return t('Agent 已报告');
    case 'not_reported':return t('尚未报告');
    case 'partial':case 'partially_supported':return t('部分支持');
    case 'unsupported':case 'not_supported':return t('尚不支持');
    case 'disabled':case 'not_configured':return t('未启用');
    case 'unavailable':case 'failed':case 'error':return t('暂不可用');
    case 'requires_auth':case 'needs_auth':case 'authentication_required':case 'unauthenticated':return t('需要登录');
    case 'connected':return t('已连接');
    case 'disconnected':return t('未连接');
    case 'pending':case 'loading':case 'running':return t('正在读取…');
    default:return t('尚未确认');
  }
}
function featureDetail(value:string):string {
  const patterns:[RegExp,string][]=[
    [/^代理报告 (\d+) 项；仅展示公开元数据，不证明执行权限或清单完整性。$/, '代理报告 {0} 项；仅展示公开元数据，不证明执行权限或清单完整性。'],
    [/^初始化报告 (\d+) 个服务：已连接 (\d+)，待授权 (\d+)，失败 (\d+)。工具执行仍受原生权限控制。$/, '初始化报告 {0} 个服务：已连接 {1}，待授权 {2}，失败 {3}。工具执行仍受原生权限控制。'],
    [/^已读取一页，共 (\d+) 个服务，其中 (\d+) 个待授权。不会发起登录或刷新配置；工具执行仍受原生权限控制。$/, '已读取一页，共 {0} 个服务，其中 {1} 个待授权。不会发起登录或刷新配置；工具执行仍受原生权限控制。'],
  ];
  for(const [pattern,template] of patterns){const match=value.match(pattern);if(match)return t(template).replace(/\{(\d+)\}/g,(_,i)=>match[Number(i)+1]);}
  if(/^(?:(?:主要窗口|次要窗口)已用 [\d.]+%(?:；|。))+仅包含用量百分比，不包含账户或凭据信息。$/.test(value))
    return [...value.matchAll(/(主要窗口|次要窗口)已用 ([\d.]+)%/g)].map(([,label,percent])=>`${t(label)}: ${percent}% ${t('已用')}`).join(' · ')+' · '+t('仅包含用量百分比，不包含账户或凭据信息。');
  return t(value);
}
function NamedList({title,items,empty,statuses=false}:{title:string;items:Named[];empty:string;statuses?:boolean}) {
  return <section className="feature-section" aria-label={title}><h3>{title}</h3>{items.length?<ul>{items.map((item,i)=><li key={`${i}:${item.name}`}>
    <div className="feature-heading"><strong>{item.name}</strong>{statuses&&<span>{statusLabel(item.status)}</span>}</div>{item.description&&<p>{item.description}</p>}
  </li>)}</ul>:<p className="hint">{empty}</p>}</section>;
}
export function AgentFeatures({onAction}:{onAction:Action}) {
  const [value,setValue]=useState<Capabilities>();const [busy,setBusy]=useState(true);const [failed,setFailed]=useState(false);
  const revision=useRef(0);
  const load=useCallback(async()=>{
    const current=++revision.current;setBusy(true);setFailed(false);setValue(undefined);
    try{
      const next=readCapabilities(await onAction('agent.features'));
      if(current!==revision.current)return;
      setValue(next);setFailed(!next);
    }catch{if(current===revision.current)setFailed(true);}
    finally{if(current===revision.current)setBusy(false);}
  },[onAction]);
  useEffect(()=>{void load();return()=>{revision.current++;};},[load]);
  return <div className="agent-features" aria-busy={busy}>
    <div className="feature-toolbar"><p className="hint">{t('仅查看当前 Agent 报告的能力')}</p><button disabled={busy} onClick={()=>void load()}>{t('刷新')}</button></div>
    {busy&&<p role="status">{t('正在读取…')}</p>}
    {failed&&<p role="alert">{t('暂时无法读取 Agent 能力，请重试')}</p>}
    {value&&<>
      {value.agent&&<p className="feature-agent">{value.agent}{value.protocol&&<small> · {value.protocol}</small>}</p>}
      <section className="feature-section" aria-label={t('功能')}><h3>{t('功能')}</h3>{value.features.length?<ul>{value.features.map((feature,i)=><li key={`${i}:${feature.id}`}>
        <div className="feature-heading"><strong>{t(feature.label)}</strong><span>{statusLabel(feature.status)}</span></div>{feature.detail&&<p>{featureDetail(feature.detail)}</p>}
      </li>)}</ul>:<p className="hint">{t('Agent 尚未报告功能')}</p>}</section>
      <NamedList title={t('技能')} items={value.skills} empty={t('Agent 尚未报告技能')}/>
      <NamedList title={t('工具')} items={value.tools} empty={t('Agent 尚未报告工具')} statuses/>
      <NamedList title={t('命令')} items={value.commands} empty={t('Agent 尚未报告命令')}/>
      {value.truncated&&<p className="hint">{t('仅显示部分内容')}</p>}
    </>}
  </div>;
}
