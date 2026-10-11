# 闪语应用图标

采用 2026-10-11 确认的 C「活泼表达」：青绿鹦鹉侧身回望，鸟喙微张，奶油白圆角底板，外缘透明。

- `source.png`：1024×1024 选稿原图，后续尺寸转换以此为源。
- `icon.png`：256×256，供 Tauri 窗口及托盘使用。
- `icon.ico`：16、24、32、48、64、256 像素，供 Windows 可执行文件和安装器使用。
- `../../public/shanyu.png`：64×64 网页图标。

使用项目自带 Tauri CLI 生成，再将所需 Windows 资源复制到现有入口：

```powershell
node_modules/.bin/tauri.cmd icon src-tauri/icons/source.png --output .build/icon-C
Copy-Item -LiteralPath .build/icon-C/128x128@2x.png -Destination src-tauri/icons/icon.png
Copy-Item -LiteralPath .build/icon-C/icon.ico -Destination src-tauri/icons/icon.ico
Copy-Item -LiteralPath .build/icon-C/64x64.png -Destination public/shanyu.png
```
