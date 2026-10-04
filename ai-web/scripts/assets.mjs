import {createHash} from 'node:crypto';
import {readFile,writeFile,readdir} from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const out=path.resolve(root,'../android/app/src/main/assets/ai-web');
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
async function files(dir){const result=[];for(const entry of await readdir(dir,{withFileTypes:true})){const name=path.join(dir,entry.name);result.push(...entry.isDirectory()?await files(name):[name]);}return result.sort();}
const sourceFiles=[...(await files(path.join(root,'src'))),...(await files(path.join(root,'scripts'))),...['package.json','package-lock.json','tsconfig.json','vite.config.ts','index.html'].map(f=>path.join(root,f))].sort();
const inputs={};for(const file of sourceFiles)inputs[path.relative(root,file).replaceAll('\\','/')]=hash(await readFile(file));
if(process.argv.includes('--verify')){
  const manifest=JSON.parse(await readFile(path.join(out,'manifest.json'),'utf8'));
  if(JSON.stringify(inputs)!==JSON.stringify(manifest.sources))throw new Error('Stale AI Web assets; run npm ci && npm run build in ai-web.');
  for(const [name,expected]of Object.entries(manifest.files))if(hash(await readFile(path.join(out,name)))!==expected)throw new Error(`AI Web asset changed: ${name}`);
  console.log('AI Web sources/assets verified');
}else{
  const lock=JSON.parse(await readFile(path.join(root,'package-lock.json'),'utf8'));const notices=[];
  for(const [name,entry]of Object.entries(lock.packages)){
    if(!name||entry.dev)continue;
    const dir=path.join(root,name);const pkg=JSON.parse(await readFile(path.join(dir,'package.json'),'utf8'));
    if(!['MIT','ISC','Apache-2.0','BSD-2-Clause','BSD-3-Clause','0BSD','(MIT OR CC0-1.0)'].includes(pkg.license))throw new Error(`License review required: ${pkg.name} ${pkg.license}`);
    const licenseFiles=(await readdir(dir)).filter(n=>/^(license|licence|copying|notice)(\.|$)/i.test(n));
    if(!licenseFiles.length)throw new Error(`Missing license: ${pkg.name}`);
    notices.push(`${pkg.name}@${pkg.version} — ${pkg.license}\n${(await Promise.all(licenseFiles.map(f=>readFile(path.join(dir,f),'utf8')))).join('\n')}`);
  }
  await writeFile(path.join(out,'third-party-notices.txt'),notices.join('\n\n-------------------------\n\n'));
  const assets={};for(const file of await files(out))if(path.basename(file)!=='manifest.json')assets[path.relative(out,file).replaceAll('\\','/')]=hash(await readFile(file));
  await writeFile(path.join(out,'manifest.json'),JSON.stringify({bridgeVersion:1,sources:inputs,files:assets},null,2)+'\n');
  console.log(`Embedded AI Web manifest and ${notices.length} runtime license notices generated`);
}
