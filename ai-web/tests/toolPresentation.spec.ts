import {test,expect} from '@playwright/test';
import {parseToolPresentation,safeToolPath,safeToolUrl} from '../src/toolPresentation';
import {legacyTools} from './toolFixtures';

for(const fixture of legacyTools)test(`${fixture.agent}: retained provider payload becomes a command`,()=>{
  const source=typeof fixture.payload==='string'?fixture.payload:JSON.stringify(fixture.payload);
  const value=parseToolPresentation(source,fixture.kind);
  expect(value.kind).toBe('command');expect(value.command).toMatch(/^printf /);
  expect(value.output).toMatch(/ output$/);expect(value.fallback).toBe(false);expect(value.raw).toBe(source);
});
test('Claude mismatched result IDs and partially written JSON stay visibly raw',()=>{
  for(const value of ['{"type":"tool_use","id":"a","name":"Bash"}\n{"type":"tool_result","tool_use_id":"b","content":"wrong result"}',
    '{"schema":"termlink.tool.v1","kind":"command","output":"partial',
    '{"schema":"termlink.tool.v2","kind":"command","command":"future"}',
    'plain tool output', 'null', '{"schema":"termlink.tool.v1","kind":"__proto__"}']) {
    const data=parseToolPresentation(value);expect(data.fallback).toBe(true);expect(data.raw).toBe(value);
  }
});
test('canonical data is validated without coercion and supports zero values',()=>{
  const data=parseToolPresentation(JSON.stringify({schema:'termlink.tool.v1',kind:'command',command:'true',exitCode:0,durationMs:0,
    input:{untrusted:'not a string'},files:[null,{path:123},{path:'/repo/a.ts',diff:'-a\n+b'}],children:[{id:'child',status:'running'}],urls:[{url:'https://example.invalid'}]}));
  expect(data.exitCode).toBe(0);expect(data.durationMs).toBe(0);expect(data.input).toBeUndefined();
  expect(data.files).toEqual([{path:'/repo/a.ts',diff:'-a\n+b'}]);expect(data.children[0].id).toBe('child');
  const invalid=parseToolPresentation(JSON.stringify({schema:'termlink.tool.v1',kind:'tool',exitCode:1.5,durationMs:-1}));
  expect(invalid.exitCode).toBeUndefined();expect(invalid.durationMs).toBeUndefined();expect(invalid.fallback).toBe(true);
});
test('file changes, MCP text, child states and ACP diffs preserve structured details',()=>{
  const files=parseToolPresentation(JSON.stringify({type:'fileChange',changes:[{path:'src/a.ts',diff:'-old\n+new'}]}));
  expect(files.kind).toBe('file');expect(files.files[0].diff).toBe('-old\n+new');
  const mcp=parseToolPresentation(JSON.stringify({type:'mcpToolCall',server:'local',tool:'find',arguments:{q:'fixture'},result:{content:[{type:'text',text:'found'}]}}));
  expect(mcp.kind).toBe('mcp');expect(mcp.name).toBe('local/find');expect(mcp.output).toBe('found');
  const agents=parseToolPresentation(JSON.stringify({type:'collabAgentToolCall',receiverThreadIds:['a','b'],prompt:'Check tests',agentsStates:{a:{status:'completed',message:'Done'},b:{status:'running'}}}));
  expect(agents.children).toEqual([{id:'a',status:'completed',prompt:'Done'},{id:'b',status:'running',prompt:'Check tests'}]);
  const acp=parseToolPresentation(JSON.stringify({kind:'edit',locations:[{path:'src/a.ts',line:1}],content:[{type:'diff',path:'src/a.ts',oldText:'old',newText:'new'}]}),'acp');
  expect(acp.kind).toBe('file');expect(acp.files).toEqual([{path:'src/a.ts',diff:'-old\n+new'}]);
});
test('large data is bounded, latest output is kept and long references are never shortened into actions',()=>{
  const data=parseToolPresentation(JSON.stringify({schema:'termlink.tool.v1',kind:'command',output:'a'.repeat(50_000)+'\nLAST LINE',
    files:[{path:'x'.repeat(5000)},...Array.from({length:80},(_,i)=>({path:`src/${i}.ts`,diff:'-old\n+new'}))],raw:'b'.repeat(30_000)}));
  expect(data.output?.length).toBeLessThanOrEqual(16_000);expect(data.output).toMatch(/LAST LINE$/);expect(data.outputClipped).toBe(true);
  expect(data.files.length).toBeLessThanOrEqual(24);expect(data.files.every(f=>f.path.startsWith('src/'))).toBe(true);
  expect(data.raw.length).toBeLessThanOrEqual(12_000);expect(data.clipped).toBe(true);
  const huge=parseToolPresentation('x'.repeat(1_048_577));expect(huge.fallback).toBe(true);expect(huge.raw.length).toBe(12_000);
});
test('only inert HTTP links and local path references can be offered to the native bridge',()=>{
  for(const url of ['https://example.invalid/path?q=1','http://example.invalid:8080'])expect(safeToolUrl(url)).toBe(true);
  for(const url of ['javascript:alert(1)','data:text/html,x','file:///etc/passwd','//example.invalid','https://user:pass@example.invalid','https://example.invalid/\nattack','https://example.invalid\\attack'])expect(safeToolUrl(url)).toBe(false);
  for(const path of ['/Users/me/a.ts','src/文件名.txt','C:\\repo\\a.ts','D:/repo/a.ts'])expect(safeToolPath(path)).toBe(true);
  for(const path of ['javascript:alert(1)','data:bad','https://example.invalid/a','file:///etc/passwd','/repo/a\u0000b','//network/share','\\\\server\\share',' /repo/a'])expect(safeToolPath(path)).toBe(false);
});


test('host path policy controls references without depending on phone or agent',()=>{
  expect(safeToolPath('report:part.txt','posix')).toBe(true);
  expect(safeToolPath('report:part.txt','windows')).toBe(false);
  expect(safeToolPath('/workspace/result.txt','unsupported')).toBe(false);
  expect(safeToolPath('javascript:alert(1)','posix')).toBe(false);
});
