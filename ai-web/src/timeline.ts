import type {Message,Pending} from './bridge';
export type TimelineRow={id:string;messages:Message[]};
export const questionSchema=(p:Pending)=>JSON.stringify(p.questions.map(q=>[q.id,q.text,q.options,q.multiSelect]));
// Bound expanded groups so even a long tool-only transcript remains virtualized.
export function timelineRows(messages:Message[]):TimelineRow[]{
  const rows:TimelineRow[]=[];
  for(const m of messages){const last=rows.at(-1);
    if(m.role==='tool'&&last?.messages[0].role==='tool'&&last.messages.length<20)last.messages.push(m);
    else rows.push({id:m.id,messages:[m]});
  }
  return rows;
}
