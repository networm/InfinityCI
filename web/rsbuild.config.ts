import { defineConfig } from "@rsbuild/core";
import { pluginReact } from "@rsbuild/plugin-react";

export default defineConfig({
  plugins: [pluginReact()],
  resolve: {
    alias: {
      "@": "./src",
    },
  },
  server: {
    port: 3000,
    proxy: {
      "/api": "http://127.0.0.1:5000",
      "/hubs": {
        target: "http://127.0.0.1:5000",
        ws: true,
      },
    },
  },
  html: {
    title: "Infinity CI",
    tags: [
      {
        tag: "script",
        head: true,
        append: false,
        children:
          "(() => { try { const t = localStorage.getItem('infinityci.theme'); if (t === 'dark' || (t !== 'light' && matchMedia('(prefers-color-scheme: dark)').matches)) document.documentElement.classList.add('dark'); } catch {} })();",
      },
    ],
  },
});
