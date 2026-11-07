export const widgetKind = kind => String(kind ?? '').toLowerCase().replace(/^source-picker$|^source$/, 'sourcepicker');
export const booleanValue = value => ['true','1','on','开启','开'].includes(String(value).toLowerCase());
export function normalizeOptions(rows) {
  const seen=new Set();
  return rows.flatMap(row => {
    const value=typeof row === 'string' ? row : String(row?.value ?? '');
    if (!value || seen.has(value)) return [];
    seen.add(value); return [{value,text:typeof row === 'string' ? row : String(row.text || value)}];
  });
}
export function chooseOption(options, previous, initial) {
  return [previous,initial,options[0]?.value].find(value => options.some(o => o.value === value)) ?? '';
}
export function panelLines(cells, width, mode = 'flex') {
  width=Math.max(1,width); const lines=[]; let current=[], used=0;
  cells.forEach((cell,i) => {
    const next=used+(current.length ? 12 : 0)+cell.min;
    if (current.length && next>width) { lines.push(current); current=[]; used=0; }
    used+=(current.length ? 12 : 0)+cell.min; current.push(i);
  });
  if (current.length) lines.push(current);
  return lines.map(indices => {
    const content=width-12*(indices.length-1), sum=indices.reduce((n,i) => n+cells[i].min,0);
    const widths=indices.map(i => cells[i].min);
    if (indices.length === 1) widths[0]=width;
    else if (content>sum) {
      if (mode.toLowerCase() === 'even') {
        // Distribute only the surplus: rounding a proportional total can shrink a cell below its minimum.
        const extra=content-sum;
        widths.forEach((w,j) => { widths[j]=w+extra*w/sum; });
      } else {
        const flex=indices.findIndex(i => cells[i].flex);
        widths[flex<0 ? widths.length-1 : flex]+=content-sum;
      }
    }
    return indices.map((index,i) => ({index,width:widths[i]}));
  });
}
export function validatePanel(node) {
  if (!Array.isArray(node.rows)) throw new Error('面板 rows 必须是行数组');
  const ids=new Set();
  for (const row of node.rows) {
    if (!['flex','even'].includes((row.mode ?? 'flex').toLowerCase()) || !Array.isArray(row.widgets)) throw new Error('面板行模式或 widgets 无效');
    for (const spec of row.widgets) {
      const kind=widgetKind(spec.kind);
      if (!['text','button','textbox','switch','choices','sourcepicker'].includes(kind)) throw new Error(`未知面板控件：${spec.kind}`);
      if (!['text','button'].includes(kind)) {
        if (!spec.id || ids.has(spec.id.toLowerCase())) throw new Error(`面板控件 id 缺失或重复：${spec.id ?? ''}`);
        ids.add(spec.id.toLowerCase());
      }
      if (spec.minWidth !== undefined && (!Number.isFinite(spec.minWidth) || spec.minWidth<=0)) throw new Error('minWidth 必须为正数');
      if (kind === 'textbox' && !['input','select'].includes((spec.mode ?? 'input').toLowerCase())) throw new Error('文本框 mode 无效');
      if (spec.options && spec.optionsSource) throw new Error('options 与 optionsSource 不能同时声明');
      if (spec.options && (!Array.isArray(spec.options) || spec.options.some(v => typeof v !== 'string'))) throw new Error('options 必须是字符串数组');
      if (spec.optionsSource && !spec.optionsSource.command) throw new Error('候选源缺少 command');
      if (kind === 'sourcepicker' && !spec.selectCommand) throw new Error('来源选择器缺少 selectCommand');
      if (kind === 'switch' && !(spec.action || spec.commitAction)) throw new Error('开关缺少 action / commitAction');
    }
  }
}
