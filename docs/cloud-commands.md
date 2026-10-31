# 闪云中心与云命令

主菜单的“闪云中心”保留内部 `company-files` 路由和窗口标识。顶部切换“云文件／云命令”，默认云文件；文件传输不会因切换而停止。窗口默认1000×650 DIP，最小760×480 DIP；云命令在900 DIP及以上左右分栏，较窄时从列表进入详情，返回保留筛选和列表位置。沿用应用图标、字体和七套主题。

## 使用与授权

- 首版只保存命令文本，不托管脚本、不连接服务器、不执行命令，也不进入话术发送或本机 MCP 工具。
- 新旧普通账号的云命令权限默认关闭，与云文件、共享话术和个人同步无关。未授权账号看不到云命令入口，直接请求也会被拒绝。
- 使用者可搜索、按单层分类筛选、查看和复制；维护者还可新建、编辑、移入回收站及恢复。服务管理员固定拥有全部权限，并可确认永久删除已回收命令。
- 管理员在右上角“权限管理”对话框内分别配置云文件和云命令，搜索账号后按行保存。维护包含使用；取消使用同时取消维护。配置版本冲突保留草稿，必须明确采用最新配置，不能自动覆盖。
- 名称最多100字，分类最多50字，空分类显示“未分类”；说明最多5000字，正文最多64KB。名称和分类不能含控制字符，正文不能包含空字符。正文保留引号、管道、反斜杠、空白和多行文本，不替换参数或添加回车。
- 列表按最近更新时间排序，支持每页10／20／50条。完整说明、维护人、更新时间和正文显示在详情中；复制按钮固定在正文区域。

## 复制和断网边界

复制由用户明确点击触发。Rust 在线重新核验会话，服务端核对所见修订号；已更新或回收时要求刷新，不静默复制新正文。最后核对当前账号和授权版本后，只写入原始纯文本剪贴板，不向其他窗口粘贴或发送。授权不代表脚本经过安全审查，已经复制的文本无法收回。

云命令仅在线可用，不写入本机目录或离线缓存。断网停止显示旧目录和正文，禁止复制、保存及管理；未保存的编辑草稿只保留在当前窗口内存。重连后重新核权。退出、切号、确认撤权或失去维护权限清除相关状态。关闭有修改的表单或窗口会询问是否放弃；写入冲突不自动重试。

现有云文件离线目录和已缓存关联文件仍按原规则可用，不受上述命令限制影响。当前窗口内存不等于安全存储，应用关闭后不会恢复命令草稿。

## 协议与接口

`capabilities.cloudCommands` 缺失时默认关闭。账号的 `cloudCommandPermissions` 为 `{ canUse, canManage, version }`，缺失时均为false／0。管理员更新账号只提交该项的 `{ canUse, canManage, expectedVersion }`，服务端拒绝维护但不允许使用的组合。旧闪云继续提供原有云文件，新客户端仅向管理员提示升级。

命令详情包含 `id`、`name`、`category`、`description`、`body`、`revision`、`updatedAt`、`maintainedBy`、`maintainerName`、`deletedAt`。列表不包含正文。所有命令响应使用 `Cache-Control: no-store`。

| 接口 | 用途 |
| --- | --- |
| `GET /api/cloud/commands?q=&category=&recycled=false&offset=0&limit=10` | 分页列表；省略category为全部，空字符串为未分类 |
| `POST /api/cloud/commands` | 创建；正文含name、category、description、body，expectedRevision为null |
| `GET /api/cloud/commands/{id}` | 详情；回收记录仅维护者可读 |
| `GET /api/cloud/commands/{id}?expectedRevision=3` | 复制前核对；不匹配或已回收返回409 |
| `PUT /api/cloud/commands/{id}` | 更新；提交原expectedRevision，冲突返回409 |
| `POST /api/cloud/commands/{id}/recycle` | 回收；提交expectedRevision |
| `POST /api/cloud/commands/{id}/restore` | 恢复；提交expectedRevision |
| `DELETE /api/cloud/commands/{id}` | 管理员永久删除已回收记录；提交expectedRevision、confirmName |

列表返回 `{ commands, total, categories }`。普通修订号在更新、回收和恢复时递增，不因其他命令变更而冲突。写操作在同一SQLite事务内重新核验会话、权限和修订；通知只在提交后发送，不携带正文。`cloud-commands-changed` SSE只面向当时有使用权限的账号，账号授权变化沿用原通知。

闪语0.2.13与闪云0.2.5配套。服务端在已有 `teams.sqlite3` 增加命令表和账号授权列，旧账号默认不开放；命令正文、回收状态、修订号和授权自动纳入现有共享备份。更新服务端前仍应停服并完整备份实际挂载的数据。正式部署按先闪云、后客户端进行；正式发布不会自动升级任何现有部署。
