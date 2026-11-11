import {useCallback,useEffect,useRef,useState} from 'react';
import {t} from './i18n';
import type {Session} from './bridge';
type Choice={action:'review'|'revert'|'fork';turnId:string};
type Page={supported:boolean;turns:{id:string;status:string}[];truncated:boolean;review:boolean;revert:boolean;forkAtTurn:boolean};
export function HistoryActions({session,onAction}:{session:Session;onAction:(action:string,args?:Record<string,unknown>)=>Promise<any>}) {
  const [page,setPage]=useState<Page>();const [busy,setBusy]=useState(false);const [failed,setFailed]=useState(false);const [choice,setChoice]=useState<Choice>();
  const generation=useRef(0);
  const load=useCallback(async()=>{const id=++generation.current;setBusy(true);setFailed(false);setChoice(undefined);
    try{const value=await onAction('history.actions');if(id!==generation.current)return;
      if(!value||!Array.isArray(value.turns))throw new Error();
      setPage({...value,turns:value.turns.filter((v:any)=>typeof v.id==='string'&&v.id.length<=256&&v.status==='completed').slice(0,30)});
    }catch{if(id===generation.current){setFailed(true);setPage(undefined);}}finally{if(id===generation.current)setBusy(false);}
  },[onAction]);
  useEffect(()=>{void load();return()=>{generation.current++;};},[load]);
  const disabled=busy||!session.canControl||session.busy||session.uncertain||session.state!=='idle'||session.pending.length>0;
  const confirm=async()=>{if(!choice||disabled)return;const id=generation.current;setBusy(true);setChoice(undefined);
    const value=await onAction(choice.action,{turnId:choice.turnId,confirmed:true});if(id!==generation.current)return;setBusy(false);
    if(value)await load();else setFailed(true);
  };
  return <div className="agent-features"><p className="hint">{t('历史回退只改变对话记录，不恢复工作区文件。分叉仍使用原工作目录。')}</p>
    <button disabled={busy} onClick={()=>void load()}>{t('刷新')}</button>
    {busy&&<p role="status">{t('正在读取…')}</p>}{failed&&<p role="alert">{t('操作未确认，请查询电脑回执或刷新后重试。')}</p>}
    {page?.supported===false&&<p>{t('当前 Agent 未提供这些原生操作。')}</p>}
    {page?.supported&&<><button disabled={disabled||!page.review} onClick={()=>setChoice({action:'review',turnId:''})}>{t('审查未提交的变更')}</button>
      <ul>{page.turns.map(turn=><li key={turn.id}><code>{turn.id}</code><div className="actions">
        <button disabled={disabled||!page.forkAtTurn} onClick={()=>setChoice({action:'fork',turnId:turn.id})}>{t('从此轮分叉')}</button>
        <button className="danger" disabled={disabled||!page.revert} onClick={()=>setChoice({action:'revert',turnId:turn.id})}>{t('回退到此轮之前')}</button>
      </div></li>)}</ul>{page.truncated&&<p>{t('仅显示部分内容')}</p>}</>}
    {choice&&<section className="approval" role="dialog" aria-label={t('确认会话操作')}><strong>{t(choice.action==='review'?'确认启动代码审查？':choice.action==='fork'?'确认从此轮创建分支？':'确认删除此轮及之后的对话历史？')}</strong>
      <p>{t(choice.action==='review'?'审查会启动新的 Agent 任务。':'工作区文件不会被恢复或隔离。')}</p>{choice.turnId&&<code>{choice.turnId}</code>}
      <div className="actions"><button onClick={()=>setChoice(undefined)}>{t('取消')}</button><button disabled={disabled} onClick={()=>void confirm()}>{t('确认')}</button></div></section>}
  </div>;
}
