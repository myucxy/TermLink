import {defineConfig} from '@playwright/test';
export default defineConfig({
  testDir:'./tests', timeout:30000, workers:1,
  use:{baseURL:'http://127.0.0.1:4173',viewport:{width:393,height:740},launchOptions:{executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE||'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'}},
  webServer:{command:'node tests/serve.mjs',url:'http://127.0.0.1:4173',reuseExistingServer:false},
});
