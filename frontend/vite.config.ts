import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', 'VITE_')
  const apiTarget = env.VITE_DEV_API_TARGET ?? 'http://localhost:5063'

  return {
    plugins: [tailwindcss(), react()],
    server: {
      port: 3177,
      proxy: {
        '/api': apiTarget,
        '/hub': { target: apiTarget, ws: true },
      },
    },
  }
})
