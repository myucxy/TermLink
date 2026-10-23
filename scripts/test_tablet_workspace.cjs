// Uses the project's installed Playwright; no dependencies or browser download are added.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
const {chromium} = require('../ai-web/node_modules/@playwright/test');

async function main() {
  const output = path.resolve(process.env.TERMLINK_DESIGN_TEST_OUTPUT || '../.workspace-validation/tablet-density-design-20261023');
  await fs.mkdir(output, {recursive: true});
  const browser = await chromium.launch({headless: true, executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});
  const page = await browser.newPage({viewport: {width: 1600, height: 1200}});
  const errors = [], requests = [], screens = [], densityChecks = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {if (/^https?:/.test(request.url())) requests.push(request.url());});
  const url = pathToFileURL(path.resolve(__dirname, '../docs/design/tablet-workspace.html')).href;
  const app = () => page.frames().find(frame => frame.parentFrame());
  const settle = () => page.waitForTimeout(100);
  async function cleanLayout(label) {
    await settle();
    const result = await app().evaluate(() => {
      const board = document.querySelector('#board'), scroll = document.querySelector('#board-scroll');
      const bounds = scroll.getBoundingClientRect();
      const bad = [...document.querySelectorAll('.session-card')].filter(card => {
        const r = card.getBoundingClientRect();
        return r.left < bounds.left - 1 || r.right > bounds.right + 1 || r.width < 240;
      }).map(card => card.dataset.id);
      return {width: innerWidth, height: innerHeight, bad, overflow: document.documentElement.scrollWidth > innerWidth + 1, columns: getComputedStyle(board).gridTemplateColumns, status: document.querySelector('#layout-status').textContent};
    });
    assert.deepEqual(result.bad, [], label + ': cards stay within the workspace');
    assert.equal(result.overflow, false, label + ': no horizontal document overflow');
    screens.push({label, ...result});
    return result;
  }
  try {
    await page.goto(url);
    await page.frameLocator('#app').locator('.session-card').first().waitFor();
    const f = page.frameLocator('#app');
    assert.equal(await f.locator('.session-card').count(), 2, 'Two usable native windows by default');
    assert.equal(await f.locator('.native-cli .terminal-input').count(), 1, 'CLI input stays inside the terminal');
    assert.equal(await f.locator('.native-cli .composer').count(), 0, 'CLI must not become a chat composer');
    assert.equal(await f.locator('.native-cli .dock-row').count(), 2, 'Original two-row shortcut dock');
    assert.equal(await f.locator('.native-ai .ai-tools [data-panel]').count(), 4, 'AI Search / Files / Changes / Usage remain available');
    assert.equal(await f.locator('.native-ai .permission-select').count(), 1);
    assert.equal(await f.locator('.native-ai .model-select').count(), 1);
    assert.equal(await f.locator('.native-ai .effort-select').count(), 1);
    for (const ratio of ['16:10', '4:3', '3:2', '16:9']) {
      await page.locator(`[data-ratio="${ratio}"]`).click();
      await cleanLayout(ratio + ' landscape');
      await page.locator('#rotate').click();
      await cleanLayout(ratio + ' portrait');
      await page.locator('#rotate').click();
    }
    await page.locator('[data-ratio="custom"]').click();
    for (const [width, height] of [[360, 800], [600, 960], [820, 1180], [1280, 600], [1366, 1024], [1800, 1000], [2560, 1600]]) {
      await page.locator('#width').fill(String(width));
      await page.locator('#height').fill(String(height));
      await page.locator('#custom-form button').click();
      await cleanLayout(`${width}x${height}`);
    }
    await page.locator('[data-ratio="16:10"]').click();
    await settle();
    for (const device of ['16:10', '4:3']) {
      await page.locator(`[data-ratio="${device}"]`).click();
      for (const columnCount of [2, 3, 4, 5]) {
        await f.locator('#column-preference').selectOption(String(columnCount));
        await f.locator('#fill-examples').click();
        for (const aspect of ['adaptive', '4:3', '3:2', '16:10', '1:1', '3:4']) {
          await f.locator('#ratio-preference').selectOption(aspect);
          await settle();
          const check = await app().evaluate(() => {
            const board = document.querySelector('#board'), scroll = document.querySelector('#board-scroll');
            const actualColumns = Number(board.dataset.columns);
            const rect = scroll.getBoundingClientRect();
            const data = [...board.querySelectorAll('.session-card')].map(el => {
              const r = el.getBoundingClientRect(), native = el.querySelector('.native-scale-content'), matrix = new DOMMatrix(getComputedStyle(native).transform);
              return {id: el.dataset.id, ratio: r.width/r.height, inside: r.left >= rect.left-1 && r.right <= rect.right+1, scale: matrix.a, uniform: matrix.a === matrix.d, width: r.width, nativeWidth: native.clientWidth, controls: el.querySelector('[data-action=expand]').getBoundingClientRect().width};
            });
            return {actualColumns, data, status: document.querySelector('#layout-status').textContent};
          });
          assert.equal(check.actualColumns, columnCount, 'Personal column count is honored');
          for (const card of check.data) {
            assert.ok(card.inside && card.uniform, 'Native windows remain inside the workspace and scale uniformly');
            assert.ok(card.controls >= 27, 'Outer expand buttons are not shrunk with content');
            assert.ok(card.nativeWidth >= 559, 'Native content keeps its reference layout width');
            if (aspect !== 'adaptive') {
              const [w,h] = aspect.split(':').map(Number);
              assert.ok(Math.abs(card.ratio-w/h)<0.02, 'Chosen window aspect ratio stays consistent');
            } else assert.ok(card.ratio >= .74 && card.ratio <= 1.61, 'Adaptive proportions stay in a useful range');
          }
          densityChecks.push({device, columnCount, aspect, ...check});
        }
        if (device === '16:10' && columnCount >= 3) {
          await f.locator('#ratio-preference').selectOption('adaptive');
          await f.locator('#toast').waitFor({state: 'hidden'});
          await page.evaluate(() => window.scrollTo(0,0));
          await page.screenshot({path:path.join(output, `${columnCount}-columns.png`),fullPage:true});
        }
      }
    }
    for (const device of ['16:10', '4:3']) {
      await page.locator(`[data-ratio="${device}"]`).click();
      await page.locator('#rotate').click();
      await f.locator('#column-preference').selectOption('5');
      for (const aspect of ['adaptive', '4:3', '3:2', '16:10', '1:1', '3:4']) {
        await f.locator('#ratio-preference').selectOption(aspect);
        await settle();
        const check = await app().evaluate(() => {
          const viewport = document.querySelector('#board-scroll').getBoundingClientRect();
          return [...document.querySelectorAll('.session-card')].map(el => {
            const r = el.getBoundingClientRect(), transform = new DOMMatrix(getComputedStyle(el.querySelector('.native-scale-content')).transform);
            return {ratio:r.width/r.height, inside:r.left>=viewport.left-1 && r.right<=viewport.right+1, uniform:transform.a===transform.d};
          });
        });
        assert.ok(check.every(c=>c.inside&&c.uniform), 'Dense portrait layout stays inside the window and scales uniformly');
        if (aspect!=='adaptive') {
          const [w,h]=aspect.split(':').map(Number);
          assert.ok(check.every(c=>Math.abs(c.ratio-w/h)<.02), 'Portrait density preserves the chosen ratio');
        }
        densityChecks.push({device,orientation:'portrait',columnCount:5,aspect,data:check});
      }
      await page.locator('#rotate').click();
    }
    // Personal settings survive reload and a temporary narrow-window fallback.
    await f.locator('#column-preference').selectOption('5');
    await f.locator('#ratio-preference').selectOption('4:3');
    await page.reload();
    await f.locator('.session-card').first().waitFor();
    assert.equal(await f.locator('#column-preference').inputValue(), '5');
    assert.equal(await f.locator('#ratio-preference').inputValue(), '4:3');
    await page.locator('[data-ratio="custom"]').click();
    await page.locator('#width').fill('393');
    await page.locator('#height').fill('852');
    await page.locator('#custom-form button').click();
    await settle();
    assert.equal(await f.locator('#board').getAttribute('data-columns'), '1');
    assert.equal(await f.locator('#column-preference').inputValue(), '5', 'Narrow fallback retains personal preference');
    await page.locator('[data-ratio="16:10"]').click();
    await settle();
    assert.equal(await f.locator('#board').getAttribute('data-columns'), '5');
    const denseWindow=f.locator('[data-id=cli-shell]');
    const thumbnailScale=Number(await denseWindow.getAttribute('data-content-scale'));
    await denseWindow.locator('[data-action=expand]').click();
    await settle();
    assert.ok(Number(await denseWindow.getAttribute('data-content-scale'))>thumbnailScale, 'Expanded window restores readable native content');
    await denseWindow.locator('[data-action=expand]').click();
    await settle();
    assert.ok(Number(await denseWindow.getAttribute('data-content-scale'))<.6, 'Restore returns to overview scale');
    await f.locator('#reset').click();
    await f.locator('[data-filter=ai]').click();
    assert.equal(await f.locator('.session-card').count(), 1);
    await f.locator('[data-filter=all]').click();
    await f.locator('#search').fill('PowerShell');
    assert.equal(await f.locator('.session-card').count(), 1);
    await f.locator('#search').fill('');
    const cli = f.locator('[data-id=cli-shell]');
    await cli.locator('textarea').fill('pwd');
    await cli.locator('textarea').press('Enter');
    await cli.locator('.card-body p').filter({hasText: /^Path/}).waitFor();
    await cli.locator('textarea').fill('clear');
    await cli.locator('textarea').press('Enter');
    await f.locator('[data-filter=ai]').click();
    await f.locator('[data-filter=all]').click();
    assert.equal(await cli.locator('.output').textContent(), '', 'Clear survives filtering during the current session');
    const ai = f.locator('[data-id=ai-claude]');
    // Native cloud menus control input explicitly; selecting a window must not grant control.
    await cli.locator('[data-native=menu]').click();
    await cli.locator('[data-native=control]').click();
    assert.equal(await cli.locator('textarea').isDisabled(), true);
    assert.equal(await ai.locator('textarea').isDisabled(), false, 'Other window keeps its control state');
    await cli.locator('.terminal').click({position: {x: 20, y: 20}});
    assert.equal(await cli.locator('textarea').isDisabled(), true, 'Focus never steals control');
    await cli.locator('[data-native=menu]').click();
    await cli.locator('[data-native=control]').click();
    assert.equal(await cli.locator('textarea').isDisabled(), false);
    await cli.locator('[data-native=menu]').click();
    await cli.locator('[data-native=frozen]').click();
    assert.equal(await cli.locator('textarea').isDisabled(), true, 'Frozen history stays read-only');
    await cli.locator('.frozen-banner button').click();
    assert.equal(await cli.locator('textarea').isDisabled(), false);
    await cli.locator('textarea').fill('pw');
    await cli.locator('[data-terminal-key=Backspace]').click();
    assert.equal(await cli.locator('textarea').inputValue(), 'p', 'Native terminal dock edits the terminal input');
    await cli.locator('textarea').fill('');
    await ai.locator('[data-panel=search]').click();
    await ai.locator('[data-conversation-search]').fill('CLI');
    assert.match(await ai.locator('.panel-result').textContent(), /找到 [1-9]/);
    await ai.locator('[data-panel=files]').click();
    await ai.locator('[data-native=file-preview]').click();
    assert.match(await ai.locator('.ai-panel pre').textContent(), /TermLink/);
    await ai.locator('[data-panel=usage]').click();
    assert.equal(await ai.locator('progress').getAttribute('value'), '18');
    await ai.locator('[data-native=close-panel]').click();
    await ai.locator('.message.agent [data-native=quote]').first().click();
    assert.match(await ai.locator('textarea').inputValue(), /CLI/);
    await ai.locator('textarea').fill('');
    await ai.locator('[data-native=attach]').click();
    assert.equal(await ai.locator('.attachment-chip').isVisible(), true);
    await ai.locator('[data-native=remove-file]').click();
    await ai.locator('.permission-select').selectOption('默认授权');
    assert.equal(await ai.locator('.permission-select').inputValue(), '默认授权');
    await f.locator('[data-id=ai-claude] textarea').fill('hi');
    await ai.locator('textarea').press('Enter');
    assert.equal(await ai.locator('textarea').inputValue(), 'hi\n', 'Native AI composer keeps Enter as a newline');
    await ai.locator('.send-button').click();
    await f.locator('[data-id=ai-claude] .message').filter({hasText: 'Hi！'}).waitFor();
    await ai.locator('textarea').fill('演示停止生成');
    await ai.locator('.send-button').click();
    await ai.locator('.send-button[aria-label="停止生成"]').click();
    assert.equal(await ai.locator('.send-button').getAttribute('aria-label'), '发送消息');
    assert.equal(await ai.locator('.message').filter({hasText: '已停止生成（演示）'}).count(), 1);
    // User text must be rendered as text, not inserted as executable markup.
    await cli.locator('textarea').fill('<img src=x onerror="alert(1)">');
    await cli.locator('textarea').press('Enter');
    await settle();
    assert.equal(await cli.locator('.card-body img').count(), 0);
    await cli.locator('[data-action=expand]').click();
    assert.equal(await f.locator('.zoomed').count(), 1);
    await cli.locator('[data-native=menu]').click();
    await cli.locator('[data-native=control]').click();
    assert.equal(await f.locator('.zoomed').count(), 1, 'Native controls preserve the expanded window');
    await cli.locator('[data-native=menu]').click();
    await cli.locator('[data-native=control]').click();
    await cli.locator('[data-native=menu]').click();
    await cli.locator('[data-native=menu]').press('Escape');
    assert.equal(await cli.locator('.native-menu').isVisible(), false, 'Escape closes the native menu first');
    assert.equal(await f.locator('.zoomed').count(), 1);
    await cli.locator('[data-action=expand]').press('Escape');
    assert.equal(await f.locator('.zoomed').count(), 0);
    await cli.locator('textarea').fill('');
    await cli.locator('textarea').focus();
    await f.locator('#keyboard-toggle').click();
    await settle();
    const keyboard = await app().evaluate(() => {
      const input = document.querySelector('[data-id=cli-shell] textarea').getBoundingClientRect();
      return {input: input.bottom, keyboard: document.querySelector('#keyboard').getBoundingClientRect().top};
    });
    assert.ok(keyboard.input <= keyboard.keyboard + 1, 'Input remains above the simulated keyboard');
    await f.locator('[data-key=h]').click();
    await f.locator('[data-key=i]').click();
    assert.equal(await cli.locator('textarea').inputValue(), 'hi');
    await f.locator('#hide-keyboard').click();
    await cli.locator('[data-action=close]').click();
    assert.equal(await f.locator('[data-id=cli-shell]').count(), 0);
    await page.reload();
    await f.locator('[data-restore=cli-shell]').waitFor();
    await f.locator('[data-restore=cli-shell]').click();
    assert.equal(await cli.locator('textarea').inputValue(), '', 'Input content is not persisted');
    await f.locator('#new-session').click();
    await f.locator('[data-new-type=ai]').click();
    await f.locator('#new-agent').selectOption('OpenCode');
    await f.locator('#new-name').fill('平板交互验证');
    await f.locator('#new-form button[type=submit]').click();
    assert.equal(await f.locator('.session-name').filter({hasText: '平板交互验证'}).count(), 1);
    await f.locator('#reset').click();
    await f.locator('#sessions-button').click();
    await f.locator('[data-pick=cli-opencode]').click();
    assert.equal(await f.locator('[data-id=cli-opencode] .native-cli').count(), 1, 'Open an existing CLI window without replacing its terminal');
    await f.locator('#reset').click();
    await f.locator('[data-id=cli-shell] .drag-handle').focus();
    await f.locator('[data-id=cli-shell] .drag-handle').press('Alt+ArrowRight');
    assert.equal(await f.locator('.session-card').first().getAttribute('data-id'), 'ai-claude', 'Accessible reordering');
    await f.locator('#reset').click();
    await f.locator('[data-layout=free]').click();
    await settle();
    assert.ok(await f.locator('#board.free').count(), 'Free layout on a wide tablet');
    const handle = f.locator('[data-id=cli-shell] .drag-handle');
    const original = await cli.boundingBox();
    const box = await handle.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + 104, box.y + 69, {steps: 12});
    await page.mouse.up();
    const moved = await cli.boundingBox();
    assert.ok(moved.x > original.x + 30 && moved.y > original.y + 20, 'Free card moves with pointer drag');
    await cli.locator('.resize-handle').scrollIntoViewIfNeeded();
    const corner = await cli.locator('.resize-handle').boundingBox();
    await page.mouse.move(corner.x + 10, corner.y + 10);
    await page.mouse.down();
    await page.mouse.move(corner.x + 75, corner.y + 65, {steps: 10});
    await page.mouse.up();
    const resized = await cli.boundingBox();
    assert.ok(resized.width > moved.width + 35 && resized.height > moved.height + 30, 'Free card resize');
    await page.evaluate(() => window.scrollTo(0, 0));
    await f.locator('#toast').waitFor({state: 'hidden'});
    await page.screenshot({path: path.join(output, 'free-cards.png'), fullPage: true});
    await page.locator('#rotate').click();
    await cleanLayout('free mode portrait fallback');
    assert.equal(await f.locator('#board.free').count(), 0);
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({path: path.join(output, 'portrait.png'), fullPage: true});
    await page.locator('#rotate').click();
    await f.locator('#reset').click();
    await f.locator('#theme').click();
    await cleanLayout('light theme');
    assert.equal(await cli.locator('.terminal').evaluate(el => getComputedStyle(el).backgroundColor), 'rgb(8, 12, 16)', 'The native terminal keeps its original dark surface');
    await page.evaluate(() => window.scrollTo(0, 0));
    await f.locator('#toast').waitFor({state: 'hidden'});
    await page.screenshot({path: path.join(output, 'light.png'), fullPage: true});
    await f.locator('#theme').click();
    await page.screenshot({path: path.join(output, 'landscape.png'), fullPage: true});
    await page.locator('[data-ratio="custom"]').click();
    await page.locator('#width').fill('1280');
    await page.locator('#height').fill('600');
    await page.locator('#custom-form button').click();
    await f.locator('[data-id=ai-claude] textarea').focus();
    await f.locator('#keyboard-toggle').click();
    await app().waitForFunction(() => document.querySelector('[data-id=ai-claude] textarea').getBoundingClientRect().bottom <= document.querySelector('#keyboard').getBoundingClientRect().top + 1);
    await f.locator('#hide-keyboard').click();
    await page.locator('[data-ratio=auto]').click();
    await page.setViewportSize({width: 768, height: 1024});
    await cleanLayout('native browser 768x1024');
    await page.setViewportSize({width: 393, height: 852});
    await cleanLayout('native browser 393x852');
    // Verify a real touch gesture reaches the iframe's pointer-capture handler.
    const touchPage = await browser.newPage({viewport: {width: 1500, height: 1100}, hasTouch: true});
    touchPage.on('pageerror', error => errors.push(error.message));
    await touchPage.goto(url);
    const touchApp = touchPage.frameLocator('#app');
    await touchApp.locator('[data-layout=free]').click();
    const touchCard = touchApp.locator('[data-id=cli-shell]');
    const start = await touchCard.boundingBox(), grip = await touchCard.locator('.drag-handle').boundingBox();
    const protocol = await touchPage.context().newCDPSession(touchPage);
    const point = {x: grip.x + grip.width / 2, y: grip.y + grip.height / 2};
    await protocol.send('Input.dispatchTouchEvent', {type: 'touchStart', touchPoints: [point]});
    for (let i = 1; i <= 10; i++) await protocol.send('Input.dispatchTouchEvent', {type: 'touchMove', touchPoints: [{x: point.x + i * 8, y: point.y + i * 4}]});
    await protocol.send('Input.dispatchTouchEvent', {type: 'touchEnd', touchPoints: []});
    const end = await touchCard.boundingBox();
    assert.ok(end.x > start.x + 40 && end.y > start.y + 20, 'Touch drag works without scrolling the whole page');
    await touchPage.close();
    assert.deepEqual(errors, [], 'No JavaScript exceptions');
    assert.deepEqual(requests, [], 'No network requests');
    await fs.writeFile(path.join(output, 'RESULT.json'), JSON.stringify({passed: true, screens, densityChecks, nativeSurfaces: ['terminal inline input', 'two-row shortcut dock', 'cloud control menu', 'read-only and frozen input', 'AI original message structure', 'AI search/files/changes/usage', 'AI permission/model/effort composer', 'quote and references', 'stop generation', 'terminal keeps black background'], interactions: ['filter', 'search', 'CLI input', 'clear', 'AI reply', 'HTML escaping', 'expand', 'soft keyboard including short windows', 'close and restore', 'layout persistence', 'personal column and ratio persistence', 'uniform content scale', 'existing session picker', 'new session', 'keyboard reorder', 'pointer move', 'touch move', 'resize', 'light theme'], errors, requests}, null, 2));
    console.log(`PASS: ${screens.length} viewport checks, ${densityChecks.length} density/aspect combinations, all interactions, zero external requests. Evidence: ${output}`);
  } finally {
    await browser.close();
  }
}
main().catch(error => {console.error(error); process.exitCode = 1;});
