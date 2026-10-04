export interface Message { id: string; role: string; kind: string; text: string; title: string; summary: string; status: string; fullLength: number }
export interface Question { id: string; text: string; options: string[]; multiSelect: boolean }
export interface Pending { id: string; kind: string; title: string; summary: string; detail: string; canApprove: boolean; questions: Question[] }
export interface Session {
  id: string; agent: string; state: string; phase: string; turnStartedAtMs: number; lastActivityAtMs: number;
  canControl: boolean; busy: boolean; uncertain: boolean; historyBefore: number; truncated: boolean;
  pending: Pending[]; plan: {step: string; status: string}[]; usage: string; diff: string; error: string;
  artifacts: boolean; search: boolean; dark: boolean; fontScale: number;
  diffLength?: number; diffTruncated?: boolean; diffRead?: boolean;
}
export type QuestionDraft = {schema:string;answers:Record<string,string[]>;extra:Record<string,string>};
export type UiState = {tail?:boolean;anchor?:{id:string;offset:number};expanded?:string[];questions?:Record<string,QuestionDraft>};
export interface Update { session: Session; messages: Message[]; order?: string[]; reset: boolean; uiState?:UiState }
declare global { interface Window {
  TermLink?: { postMessage: (value: string) => void };
  termlinkUpdate?: (update: Update) => void;
  termlinkReply?: (reply: {id: string; value?: unknown; error?: string}) => void;
} }
let nextId = 0;
const pending = new Map<string, {resolve: (value: any) => void; reject: (error: Error) => void; timer: ReturnType<typeof setTimeout>}>();
window.termlinkReply = reply => {
  const call = pending.get(reply.id); if (!call) return;
  clearTimeout(call.timer); pending.delete(reply.id);
  if (reply.error) call.reject(new Error(reply.error)); else call.resolve(reply.value);
};
export function request<T = any>(action: string, args: Record<string, unknown> = {}): Promise<T> {
  return new Promise((resolve, reject) => {
    if (!window.TermLink) { reject(new Error('手机连接尚未就绪')); return; }
    if (pending.size >= 16) { reject(new Error('请等待当前操作完成')); return; }
    const id = String(++nextId);
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('请求结果尚未确认，请检查连接')); }, 45_000);
    pending.set(id, {resolve, reject, timer});
    window.TermLink.postMessage(JSON.stringify({id, action, ...args}));
  });
}

// Agent HTML never runs in the trusted UI origin. No scripts, forms, external resources,
// navigation or native bridge; a second CSP prevents a document's own policy relaxing this.
export function staticHtml(source: string): string {
  const doc = new DOMParser().parseFromString(source, 'text/html');
  doc.querySelectorAll('script,iframe,frame,object,embed,base,meta,link,form').forEach(n => n.remove());
  for (const element of doc.querySelectorAll('*')) for (const attr of [...element.attributes]) {
    if (/^on/i.test(attr.name) || ['href','action','formaction','srcdoc','target'].includes(attr.name) ||
        ['src','srcset','poster'].includes(attr.name) && !/^data:image\/(png|jpeg|gif|webp);base64,/i.test(attr.value)) element.removeAttribute(attr.name);
  }
  return '<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src \'none\'; style-src \'unsafe-inline\'; img-src data:; form-action \'none\'; base-uri \'none\'; navigate-to \'none\'"></head><body>' + doc.head.innerHTML + doc.body.innerHTML + '</body></html>';
}
