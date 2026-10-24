// REQ-WEB-006: layout is user-owned. Leaves have one page; splits carry proportional sizes.
export const leaf = page => ({kind:'page',page});
export const split = (axis, ratio, first, second) => ({kind:'split',axis,ratio,first,second});
export function pageIds(tree) {
  if (!tree) return [];
  return tree.kind === 'page' ? [tree.page] : [...pageIds(tree.first),...pageIds(tree.second)];
}
export function removePage(tree,id) {
  if (!tree) return null;
  if (tree.kind === 'page') return tree.page === id ? null : tree;
  const first = removePage(tree.first,id), second = removePage(tree.second,id);
  return !first ? second : !second ? first : {...tree,first,second};
}
export function pageHome(tree,id) {
  if (!tree || tree.kind !== 'split') return null;
  for (const [branch,sibling,leading] of [[tree.first,tree.second,true],[tree.second,tree.first,false]]) {
    if (branch.kind==='page' && branch.page===id) return {target:pageIds(sibling)[0],
      side:tree.axis==='x'?(leading?'left':'right'):(leading?'top':'bottom'),ratio:leading?tree.ratio:1-tree.ratio};
  }
  return pageHome(tree.first,id) || pageHome(tree.second,id);
}
export function dockPage(tree,id,target,side,ratio=.3) {
  if (!['left','right','top','bottom','center'].includes(side)) throw new Error('停靠方位无效');
  ratio=Math.max(.12,Math.min(.88,Number(ratio)||.3));
  if (id === target && pageIds(tree).includes(id)) return tree;
  const base = removePage(tree,id), page = leaf(id);
  if (!base) return page;
  const insert = node => {
    if (side === 'center') return page;
    const leading = side === 'left' || side === 'top';
    return split(side === 'left' || side === 'right' ? 'x' : 'y', leading ? ratio : 1-ratio,
      leading ? page : node, leading ? node : page);
  };
  if (!target || !pageIds(base).includes(target)) return insert(base);
  const visit = node => node.kind === 'page' ? node.page === target ? insert(node) : node
    : {...node,first:visit(node.first),second:visit(node.second)};
  return visit(base);
}
export function normalizeLayout(tree, available, seen = new Set(), depth=0) {
  if (!tree || depth > 40) return null;
  if (tree.kind === 'page') {
    if (!available.has(tree.page) || seen.has(tree.page)) return null;
    seen.add(tree.page); return leaf(tree.page);
  }
  if (tree.kind !== 'split' || !['x','y'].includes(tree.axis)) return null;
  const first = normalizeLayout(tree.first,available,seen,depth+1), second = normalizeLayout(tree.second,available,seen,depth+1);
  if (!first) return second;
  if (!second) return first;
  return split(tree.axis,Math.max(.12,Math.min(.88,Number(tree.ratio)||.5)),first,second);
}
export function defaultLayout(pages) {
  const seats = {};
  for (const page of pages) {
    if (page.placement?.visible === false || page.placement?.side === 'tab') continue;
    const side = page.placement?.side || 'right';
    seats[side] ??= page;
  }
  const ratio = page => Math.max(.12,Math.min(.7,Number(page?.placement?.ratio)||.25));
  let tree = seats.center ? leaf(seats.center.id) : null;
  for (const side of ['top','bottom','left','right']) {
    const page = seats[side]; if (!page) continue;
    tree = dockPage(tree,page.id,null,side,ratio(page));
  }
  return tree;
}
