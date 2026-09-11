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
      // Fora do Docker o Django atende em 127.0.0.1; dentro da rede do compose
      // ele atende pelo nome do servico. VITE_API_ALVO cobre os dois casos sem
      // o front precisar saber onde esta rodando.
      "/api": {
        target: process.env.VITE_API_ALVO || "http://127.0.0.1:8000",
        changeOrigin: true,
      },
    },
  },
});
