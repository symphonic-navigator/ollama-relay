import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// During development the SPA runs on Vite and talks to a relay on :8080.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: { outDir: "dist", emptyOutDir: true, sourcemap: false },
  server: {
    port: 5173,
    proxy: {
      "/mgmt": "http://localhost:8080",
    },
  },
});
