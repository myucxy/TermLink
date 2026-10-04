import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
export default defineConfig({
  plugins: [react()], base: './',
  build: { target: 'es2022', outDir: '../android/app/src/main/assets/ai-web', emptyOutDir: true },
});
