// REQ-WEB-008: persist proportions, reconcile schema changes, keep the star column last.
export const columnWeight = width => width === '*' ? 200 : Number(width) > 0 && Number.isFinite(Number(width)) ? Number(width) : 120;
export function columnOrder(columns, saved = []) {
  const keys = columns.map(c => c.key), star = columns.find(c => c.width === '*')?.key;
  const ordered = [...new Set([...saved, ...keys])].filter(key => keys.includes(key) && key !== star);
  if (star !== undefined) ordered.push(star);
  return ordered;
}
export function allocateWidths(weights, available) {
  available = Math.max(0, available);
  if (!weights.length) return [];
  const floor = Math.min(48, available / weights.length), result = new Array(weights.length);
  let remaining = available, pending = weights.map((_, i) => i);
  while (pending.length) {
    const total = pending.reduce((sum, i) => sum + columnWeight(weights[i]), 0);
    const small = pending.filter(i => remaining * columnWeight(weights[i]) / total < floor - .001);
    if (!small.length) {
      pending.forEach(i => { result[i] = remaining * columnWeight(weights[i]) / total; });
      result[pending.at(-1)] += remaining - pending.reduce((sum, i) => sum + result[i], 0);
      break;
    }
    for (const i of small) { result[i] = floor; remaining -= floor; }
    pending = pending.filter(i => !small.includes(i));
  }
  return result;
}
export function resizeWidths(widths, index, delta) {
  const next = [...widths], last = widths.length - 1;
  if (index < 0 || index >= last) return next;
  const floor = Math.min(48, widths.reduce((sum, w) => sum + w, 0) / widths.length);
  const change = Math.max(floor - widths[index], Math.min(delta, widths[last] - floor));
  next[index] += change; next[last] -= change;
  return next;
}
