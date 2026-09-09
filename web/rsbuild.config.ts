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
  },
});
