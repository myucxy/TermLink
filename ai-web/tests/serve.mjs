import http from 'node:http';
import path from 'node:path';
import {readFile} from 'node:fs/promises';
const root=path.resolve('../android/app/src/main/assets/ai-web');
http.createServer(async(req,res)=>{
  try{const file=path.resolve(root,'.'+decodeURIComponent(new URL(req.url,'http://localhost').pathname).replace(/\/$/,'/index.html'));
    if(!file.startsWith(root+path.sep))throw new Error('outside');
    res.setHeader('Content-Type',({'.js':'application/javascript','.css':'text/css','.html':'text/html'}[path.extname(file)])||'text/plain');res.end(await readFile(file));
  }catch{res.writeHead(404);res.end();}
}).listen(4173,'127.0.0.1');
