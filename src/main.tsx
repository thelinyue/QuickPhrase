import React from 'react';
import { createRoot } from 'react-dom/client';
import { MinimizedFloat } from './MinimizedFloat';
import './styles.css';
import { startDiagnosticObserver } from './diagnosticsClient';

void startDiagnosticObserver().catch(() => {});

// 小条使用独立入口，避免静态导入 App 将话术、账号与编辑器一起加载到这个 WebView。
const root = createRoot(document.getElementById('root')!);
if (location.hash.split('?')[0] === '#minimized-float') root.render(<React.StrictMode><MinimizedFloat /></React.StrictMode>);
else if (location.hash.split('?')[0] === '#common-menu') void import('./CommonMenu').then(({ CommonMenuWindow }) => root.render(<React.StrictMode><CommonMenuWindow /></React.StrictMode>));
else void import('./App').then(({ App }) => root.render(<React.StrictMode><App /></React.StrictMode>));
