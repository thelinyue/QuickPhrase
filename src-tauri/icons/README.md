# 闪语应用图标

采用 2026-10-11 确认的 C「活泼表达」：青绿鹦鹉侧身回望，鸟喙微张。2026-10-15 移除奶油白圆角底板及其阴影，使用透明背景，并减少留白以放大各处显示。

- `source.png`：1254×1254 透明底源图，后续尺寸转换以此为源。
- `icon.png`：256×256，供 Tauri 窗口及托盘使用。
- `icon.ico`：16、24、32、48、64、256 像素，供 Windows 可执行文件和安装器使用。
- `../../public/shanyu.png`：256×256 网页图标，供标题栏、关于页及悬浮窗共用，避免放大后模糊。

本轮使用内置 imagegen 编辑透明素材，提示词要求只放大既有鹦鹉、减少透明留白，保留造型、配色和纹理，不增加背景、阴影或文字。标题栏显示24px、关于页72px、悬浮窗32px；托盘及快捷方式使用同源ICO／PNG，在Windows固定图标尺寸中增加图案占比。

使用项目自带 Tauri CLI 生成，再将所需 Windows 资源复制到现有入口：

```powershell
node_modules/.bin/tauri.cmd icon src-tauri/icons/source.png --output .build/icon-enlarged
Copy-Item -LiteralPath .build/icon-enlarged/128x128@2x.png -Destination src-tauri/icons/icon.png
Copy-Item -LiteralPath .build/icon-enlarged/icon.ico -Destination src-tauri/icons/icon.ico
Copy-Item -LiteralPath .build/icon-enlarged/128x128@2x.png -Destination public/shanyu.png
```
