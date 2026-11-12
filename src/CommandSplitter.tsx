import { useEffect, useRef, type RefObject } from 'react';

// Adapted from LeiBox elastic-panel-resize (ElasticPanelResizeEngine.js).
// Independent DOM/SVG reconstruction; the original author source was not supplied.
// Design reference: Alvish https://x.com/alvish/status/2111084319716647114/video/1
// https://leibox.pantoria.cn/docs/animations/elastic-panel-resize
const clamp = (value: number, min: number, max: number) => Math.max(min, Math.min(max, value));

/** 分栏比例只留在当前窗口。实际列宽始终受限，弹性只作用于把手，避免挤压正文或改变业务状态。 */
export function CommandSplitter({ workspace, active }: { workspace: RefObject<HTMLDivElement | null>; active: boolean }) {
  const handle = useRef<HTMLDivElement>(null), path = useRef<SVGPathElement>(null), ratio = useRef(.34);
  useEffect(() => {
    const container = workspace.current!, separator = handle.current!, ink = path.current!;
    const media = window.matchMedia?.('(prefers-reduced-motion: reduce)');
    let width = 0, frame = 0, last = 0, bend = 0, bendVelocity = 0, offset = 0, offsetVelocity = 0;
    let drag: { id: number; start: number; origin: number; x: number; time: number; speed: number; excess: number } | null = null;
    const enabled = () => active && !document.hidden && window.innerWidth >= 900;
    const legal = (value: number) => clamp(value, 240, width - 320);
    function draw() { ink.setAttribute('d', `M${12 + offset} 10 Q${12 + offset + bend * 2} 40 ${12 + offset} 70`); }
    function updateWidth() {
      if (!width) return;
      const value = legal(ratio.current * width), percent = (pixels: number) => Math.round(pixels / width * 1000) / 10;
      container.style.setProperty('--command-list-width', `${value}px`);
      separator.setAttribute('aria-valuemin', String(percent(240)));
      separator.setAttribute('aria-valuemax', String(percent(width - 320)));
      separator.setAttribute('aria-valuenow', String(percent(value)));
      separator.setAttribute('aria-valuetext', `命令列表宽度 ${percent(value)}%`);
    }
    function choose(value: number) { ratio.current = legal(value) / width; updateWidth(); }
    function stopMotion() {
      cancelAnimationFrame(frame); frame = 0; bend = bendVelocity = offset = offsetVelocity = 0; draw();
    }
    function animate(now: number) {
      frame = 0;
      const dt = Math.min((now - last) / 1000 || 1 / 60, 1 / 30); last = now;
      const moving = !!drag && now - drag.time < 70, direction = moving ? clamp(drag!.speed / 600, -1, 1) : 0;
      const bendTarget = direction * 3, offsetTarget = direction * 2 + (drag ? Math.sign(drag.excess) * 5 * (1 - Math.exp(-Math.abs(drag.excess) / 35)) : 0);
      // 沿用来源的弹簧积分；静止后停止申请帧，边界回弹不带动正文越界。
      for (let i = 0; i < 4; i++) {
        const step = dt / 4;
        bendVelocity += ((bendTarget - bend) * 680 - bendVelocity * 34) * step; bend += bendVelocity * step;
        offsetVelocity += ((offsetTarget - offset) * 680 - offsetVelocity * 34) * step; offset += offsetVelocity * step;
      }
      draw();
      if (moving || Math.abs(bendTarget - bend) > .01 || Math.abs(bendVelocity) > .03 || Math.abs(offsetTarget - offset) > .01 || Math.abs(offsetVelocity) > .03) frame = requestAnimationFrame(animate);
      else { bend = bendTarget; offset = offsetTarget; bendVelocity = offsetVelocity = 0; draw(); }
    }
    function wake() {
      if (!enabled() || media?.matches) { stopMotion(); return; }
      if (!frame) { last = performance.now(); frame = requestAnimationFrame(animate); }
    }
    function finish(cancel = false) {
      if (!drag) return;
      const previous = drag; drag = null;
      if (cancel) { ratio.current = previous.start; updateWidth(); }
      delete container.dataset.resizing;
      if (separator.hasPointerCapture(previous.id)) separator.releasePointerCapture(previous.id);
      wake();
    }
    function measure() {
      separator.tabIndex = enabled() ? 0 : -1;
      const next = container.clientWidth;
      if (!enabled() || next !== width) finish();
      if (next > 0) { width = next; updateWidth(); }
      if (!enabled()) stopMotion();
    }
    function down(event: PointerEvent) {
      if (event.button !== 0 || drag || !enabled() || !width) return;
      event.preventDefault(); separator.focus({ preventScroll: true });
      drag = { id: event.pointerId, start: ratio.current, origin: event.clientX - legal(ratio.current * width), x: event.clientX, time: performance.now(), speed: 0, excess: 0 };
      separator.setPointerCapture(event.pointerId); container.dataset.resizing = 'true';
    }
    function move(event: PointerEvent) {
      if (!drag || event.pointerId !== drag.id) return;
      const now = performance.now(), raw = event.clientX - drag.origin;
      drag.speed = clamp((event.clientX - drag.x) / Math.max((now - drag.time) / 1000, .008), -2200, 2200);
      drag.x = event.clientX; drag.time = now; drag.excess = raw - legal(raw);
      choose(raw); wake();
    }
    function up(event: PointerEvent) { if (event.pointerId === drag?.id) finish(event.type === 'pointercancel'); }
    const end = () => finish();
    const reset = () => { if (enabled() && width) { finish(); ratio.current = .34; updateWidth(); } };
    function key(event: KeyboardEvent) {
      if (!enabled() || !width || event.isComposing || event.defaultPrevented) return;
      const step = event.shiftKey ? 48 : 16, value = legal(ratio.current * width);
      const values: Record<string, number> = { ArrowLeft: value - step, ArrowRight: value + step, Home: 240, End: width - 320, Escape: width * .34 };
      if (!(event.key in values)) return;
      event.preventDefault(); event.stopPropagation();
      if (event.key === 'Escape' && drag) finish(true);
      else { finish(); if (event.key === 'Escape') reset(); else choose(values[event.key]); }
    }
    separator.addEventListener('pointerdown', down); separator.addEventListener('pointermove', move);
    separator.addEventListener('pointerup', up); separator.addEventListener('pointercancel', up); separator.addEventListener('lostpointercapture', end);
    separator.addEventListener('blur', end); separator.addEventListener('dblclick', reset); separator.addEventListener('keydown', key);
    window.addEventListener('blur', end); window.addEventListener('resize', measure); document.addEventListener('visibilitychange', measure); media?.addEventListener('change', wake);
    const observer = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(measure); observer?.observe(container);
    measure();
    return () => {
      finish(); stopMotion(); observer?.disconnect();
      separator.removeEventListener('pointerdown', down); separator.removeEventListener('pointermove', move);
      separator.removeEventListener('pointerup', up); separator.removeEventListener('pointercancel', up); separator.removeEventListener('lostpointercapture', end);
      separator.removeEventListener('blur', end); separator.removeEventListener('dblclick', reset); separator.removeEventListener('keydown', key);
      window.removeEventListener('blur', end); window.removeEventListener('resize', measure); document.removeEventListener('visibilitychange', measure); media?.removeEventListener('change', wake);
    };
  }, [workspace, active]);
  return <div className="command-splitter" ref={handle} role="separator" tabIndex={active ? 0 : -1} aria-label="调整命令列表宽度" aria-orientation="vertical" aria-controls="cloud-command-list"><svg viewBox="0 0 24 80" aria-hidden="true"><path ref={path} d="M12 10 Q12 40 12 70" /></svg></div>;
}
