export function payload(result) {
  if (result.success === false) throw new Error(result.message || '指令执行失败');
  const data = result.data ?? result.message;
  if (typeof data !== 'string') return data;
  try { return JSON.parse(data); } catch { return data; }
}
export function commandText(name, args = {}) {
  if (!/^[a-z][a-z0-9_.]*$/i.test(name)) throw new Error('指令名称无效');
  return [name, ...Object.entries(args).map(([key, value]) => {
    if (!/^[a-z][a-z0-9_]*$/i.test(key)) throw new Error('参数名称无效');
    return `${key}=${JSON.stringify(String(value))}`;
  })].join(' ');
}
export function bindArgs(args = {}, values = {}, row = {}, selection = {}) {
  return Object.fromEntries(Object.entries(args).map(([key, value]) => [key,
    String(value).replace(/\{([^{}]+)\}/g, (_, ref) => {
      let found;
      if (ref.startsWith('selection.')) {
        const path = ref.slice(10), split = path.lastIndexOf('.');
        found = selection[path.slice(0, split)]?.[path.slice(split + 1)];
      } else found = Object.hasOwn(values, ref) ? values[ref] : row[ref];
      if (found === undefined || found === null) throw new Error(`无法取得参数：${ref}`);
      return String(found);
    })]));
}
export function rowsOf(data) {
  if (Array.isArray(data)) return data;
  if (Array.isArray(data?.rows)) return data.rows;
  throw new Error('数据源未返回行数组或 rows 快照');
}
export function validateEnvelope(set, field, owner) {
  if (set?.schemaVersion !== 1 || !Array.isArray(set[field]) || !set.owner
      || (owner && set.owner !== owner)) throw new Error('页面协议版本、归属或结构不匹配');
  const ids = new Set();
  for (const item of set[field]) {
    if (!/^[a-z0-9_.-]+$/.test(item.id) || ids.has(item.id)) throw new Error('页面或动作 id 无效或重复');
    ids.add(item.id);
  }
  return set;
}
