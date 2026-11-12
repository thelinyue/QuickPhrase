import { useEffect, useRef } from 'react';

// Adapted from LeiBox ai-generating (AiImageGenerationEngine.js).
// https://leibox.pantoria.cn/docs/animations/ai-generating
const dots = Array.from({ length: 400 }, (_, i) => ({ x: (i % 20 - 10) * 18, y: (Math.floor(i / 20) - 10) * 18 }));

/** 仅绘制等待反馈；是否仍在读取由目录请求决定，隐藏模块不保留后台动画循环。 */
export function CloudLoading({ label, active }: { label: string; active: boolean }) {
  const grid = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const elements = Array.from(grid.current!.children) as HTMLElement[];
    const media = window.matchMedia?.('(prefers-reduced-motion: reduce)');
    let frame = 0;
    function paint(timestamp: number) {
      const time = timestamp * 0.0012;
      const foci = [
        { x: Math.sin(time * .7) * 90, y: Math.cos(time * 1.1) * 70, weight: 1 },
        { x: Math.sin(time * -1.2 + 2) * 70, y: Math.cos(time * .8 + 1.5) * 95, weight: (Math.sin(time * .35) + 1) / 2 },
      ];
      dots.forEach((dot, i) => {
        const intensity = Math.min(1, foci.reduce((sum, focus) => sum + Math.pow(Math.max(0, 1 - Math.hypot(focus.x - dot.x, focus.y - dot.y) / 110), 1.4) * focus.weight, 0));
        elements[i].style.transform = `scale(${.333 + (.65 - .333) * intensity})`;
        elements[i].style.opacity = String(.25 + .65 * intensity);
      });
    }
    function animate(time: number) { paint(time); frame = requestAnimationFrame(animate); }
    function update() {
      cancelAnimationFrame(frame); frame = 0;
      if (active && !document.hidden && !media?.matches) frame = requestAnimationFrame(animate);
      else paint(0);
    }
    paint(0); update();
    document.addEventListener('visibilitychange', update); media?.addEventListener('change', update);
    return () => { cancelAnimationFrame(frame); document.removeEventListener('visibilitychange', update); media?.removeEventListener('change', update); };
  }, [active]);
  return <div className="company-empty cloud-loading" role="status"><div className="cloud-loading-grid" ref={grid} aria-hidden="true">{dots.map((_, i) => <i key={i} />)}</div><p>{label}</p></div>;
}
