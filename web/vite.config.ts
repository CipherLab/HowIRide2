import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Dev: `npm run dev` proxies /api to a local Api (dotnet run --project src/Api).
// Build: output lands in the Api's wwwroot, which serves it.
export default defineConfig({
  plugins: [react()],
  server: { proxy: { '/api': process.env.API_URL ?? 'http://127.0.0.1:5199' } },
  build: { outDir: '../src/Api/wwwroot', emptyOutDir: true },
})
