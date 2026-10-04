// Requires the isolated Android acceptance Activity, its loopback Windows fixture,
// a sent "web-ui" message, and adb forward of that app's WebView to port 19229.
import {chromium} from 'playwright';
import assert from 'node:assert/strict';
const browser=await chromium.connectOverCDP('http://127.0.0.1:19229',{noDefaults:true});
try{
  const page=browser.contexts().flatMap(c=>c.pages()).find(p=>p.url()==='https://appassets.androidplatform.net/assets/ai-web/index.html');
  assert(page,'Trusted conversation WebView missing');
  for(const close of await page.getByRole('button',{name:'关闭',exact:true}).all()) if(await close.isVisible()) await close.click();
  await page.getByRole('heading',{name:'Web 会话联合测试'}).waitFor({timeout:12000});
  assert.equal(await page.locator('[data-message-id="web-reply"]').count(),1,'Stream duplicated the final reply');
  if(!await page.getByText('检查工作目录边界，生成静态 HTML 并验证预览。',{exact:true}).isVisible()) await page.getByText('思考摘要',{exact:true}).click();
  await page.getByText('检查工作目录边界，生成静态 HTML 并验证预览。',{exact:true}).waitFor();
  await page.getByRole('button',{name:'▱ 文件'}).click();
  await page.getByRole('button',{name:/report.html.*KB/}).click();
  const frame=page.frameLocator('iframe');await frame.getByRole('heading',{name:'TermLink Preview'}).waitFor();
  assert.equal(await frame.locator('script').count(),0,'Preview retained executable script');
  assert.equal(await frame.locator('body').evaluate(()=>typeof window.TermLink),'undefined','Preview obtained native bridge');
  assert.equal(await page.evaluate(()=>window.hacked),undefined,'Preview script escaped sandbox');
  await page.screenshot({path:'test-results/emulator-static-preview.png'});
  await page.getByRole('dialog').getByRole('button',{name:'关闭',exact:true}).click();
  await page.getByRole('button',{name:/pixel.png.*KB/}).click();
  await page.getByRole('dialog').getByRole('img').waitFor();
  assert(await page.getByRole('dialog').getByRole('img').evaluate(img=>img.complete&&img.naturalWidth===1),'Native file read/image decode failed');
  await page.getByRole('dialog').getByRole('button',{name:'关闭',exact:true}).click();
  await page.locator('.panel-title').getByRole('button',{name:'关闭',exact:true}).click();
  await page.getByRole('button',{name:'用量',exact:true}).click();
  assert.equal(await page.locator('progress').getAttribute('value'),'2000');
  await page.getByRole('button',{name:'± 变更'}).click();
  await page.getByText('report.html',{exact:false}).first().waitFor();
  await page.locator('.panel-title').getByRole('button',{name:'关闭',exact:true}).click();
  await page.getByRole('button',{name:'⌕ 搜索'}).click();
  await page.getByPlaceholder('输入要查找的内容').fill('步骤');await page.getByRole('button',{name:'查找',exact:true}).click();
  await page.locator('.panel .file').first().waitFor();
  await page.screenshot({path:'test-results/emulator-search.png'});
  console.log('PASS emulator + Windows fixture: native send, one stream message, public thought, host file/PNG/HTML read, sandbox bridge isolation, usage, Diff, full-text search');
}finally{await browser.close();}
