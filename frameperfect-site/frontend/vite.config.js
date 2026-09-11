import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // Em desenvolvimento o Vite repassa /api para o Django, entao o front usa
    // caminho relativo e nao precisa saber o endereco do backend. Em producao
    // quem faz isso e o Nginx, do mesmo jeito.
    proxy: {
      "/api": { target: "http://127.0.0.1:8000", changeOrigin: true },
    },
  },
});
