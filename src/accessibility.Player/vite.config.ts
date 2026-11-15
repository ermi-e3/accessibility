import { defineConfig } from "vite";
import { fileURLToPath } from "node:url";

export default defineConfig({
  build: {
    target: "es2020",
    sourcemap: false,
    emptyOutDir: true,
    outDir: fileURLToPath(new URL("../accessibility.Api/wwwroot/sdk/v1", import.meta.url)),
    lib: {
      entry: fileURLToPath(new URL("src/index.ts", import.meta.url)),
      name: "AccessLearn",
      formats: ["iife"],
      fileName: () => "accesslearn-player.min.js"
    }
  }
});
