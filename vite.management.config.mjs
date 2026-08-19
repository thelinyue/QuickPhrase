import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// 正式 WebView2 只加载管理界面，避免把浏览器原型和演示壁纸带进桌面发布包。
export default defineConfig({
  publicDir: false,
  build: {
    outDir: "dist/management",
    emptyOutDir: true,
    rollupOptions: {
      input: { management: "management.html" },
      output: {
        manualChunks: {
          react: ["react", "react-dom"],
        },
      },
    },
  },
  optimizeDeps: { include: ["react", "react-dom/client"] },
  plugins: [react()],
});
