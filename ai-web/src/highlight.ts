import hljs from 'highlight.js/lib/core';
import javascript from 'highlight.js/lib/languages/javascript';
import typescript from 'highlight.js/lib/languages/typescript';
import json from 'highlight.js/lib/languages/json';
import xml from 'highlight.js/lib/languages/xml';
import css from 'highlight.js/lib/languages/css';
import python from 'highlight.js/lib/languages/python';
import bash from 'highlight.js/lib/languages/bash';
import csharp from 'highlight.js/lib/languages/csharp';
for(const [name,language] of Object.entries({javascript,typescript,json,xml,css,python,bash,csharp}))hljs.registerLanguage(name,language);
export function highlight(value:string,language:string):string {return hljs.getLanguage(language)?hljs.highlight(value,{language,ignoreIllegals:true}).value:'';}
