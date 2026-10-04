import {useEffect,useState} from 'react';

export function CodeBlock({value,language,onCopy,onPreview}:{value:string;language:string;onCopy:()=>void;onPreview:()=>void}) {
  const [markup,setMarkup]=useState('');
  useEffect(()=>{let active=true;setMarkup('');
    // Defer highlighting while tokens arrive and bound parsing of very large blocks.
    const timer=setTimeout(()=>{if(language&&value.length<=24000)void import('./highlight').then(({highlight})=>{
      if(active)setMarkup(highlight(value,language));
    }).catch(()=>{});},250);
    return()=>{active=false;clearTimeout(timer);};
  },[value,language]);
  return <div className="code"><div className="code-head"><span>{language||'代码'}</span><button onClick={onCopy}>复制</button>{language==='html'&&<button onClick={onPreview}>预览</button>}</div>
    <pre>{markup?<code dangerouslySetInnerHTML={{__html:markup}}/>:<code>{value}</code>}</pre></div>;
}
