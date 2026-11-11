import {defineConfig} from '@playwright/test';
import path from 'node:path';

// Run against a separately built Vite directory, leaving bundled phone assets
// and any other worker's fixture server untouched.
const root=process.env.AI_WEB_ASSETS_DIR;
if(!root||!path.isAbsolute(root))throw new Error('Set AI_WEB_ASSETS_DIR to the absolute temporary Vite output directory.');
const port=process.env.AI_WEB_TEST_PORT||'4187';
export default defineConfig({
  testDir:'.',testMatch:'**/*.spec.ts',timeout:30000,workers:1,outputDir:path.join(root,'playwright-results'),
  reporter:'list',
  use:{baseURL:`http://127.0.0.1:${port}`,viewport:{width:393,height:740},launchOptions:{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE||'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'}},
  webServer:{command:'node tests/serve.mjs',cwd:path.resolve(import.meta.dirname,'..'),url:`http://127.0.0.1:${port}`,reuseExistingServer:false,
    env:{AI_WEB_ASSETS_DIR:root,AI_WEB_TEST_PORT:port}},
});
