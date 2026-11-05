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
    if (branch.kind==='page' && branch.page===id) return {target:pageIds(sibling)[0],targets:pageIds(sibling),
      side:tree.axis==='x'?(leading?'left':'right'):(leading?'top':'bottom'),ratio:leading?tree.ratio:1-tree.ratio};
  }
  return pageHome(tree.first,id) || pageHome(tree.second,id);
}
export function restorePage(tree,id,home) {
  const base=removePage(tree,id),present=new Set(pageIds(base));
  const targets=(home.targets??[home.target]).filter(target=>present.has(target));
  if(!targets.length)return dockPage(base,id,null,home.side,home.ratio);
  const visit=node=>{
    if(node.kind==='split'){
      const first=new Set(pageIds(node.first)),second=new Set(pageIds(node.second));
      if(targets.every(target=>first.has(target)))return {...node,first:visit(node.first)};
      if(targets.every(target=>second.has(target)))return {...node,second:visit(node.second)};
    }
    return dockPage(node,id,null,home.side,home.ratio);
  };
  return visit(base);
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
    if (typeof tree.page!=='string'||!tree.page||(available&&!available.has(tree.page)) || seen.has(tree.page)) return null;
    seen.add(tree.page); return leaf(tree.page);
  }
  if (tree.kind !== 'split' || !['x','y'].includes(tree.axis)) return null;
  const first = normalizeLayout(tree.first,available,seen,depth+1), second = normalizeLayout(tree.second,available,seen,depth+1);
  if (!first) return second;
  if (!second) return first;
  return split(tree.axis,Math.max(.12,Math.min(.88,Number(tree.ratio)||.5)),first,second);
}
// Keep missing pages in the saved tree. Only the projection is compacted for this
// registration cycle; links let a visible separator resize its original split.
export function visibleLayout(tree,available,links=new WeakMap()) {
  if(!tree)return null;
  if(tree.kind==='page')return available.has(tree.page)?tree:null;
  const first=visibleLayout(tree.first,available,links),second=visibleLayout(tree.second,available,links);
  if(!first)return second;if(!second)return first;
  const node={...tree,first,second};links.set(node,tree);return node;
}
export const residentPage=id=>['console','mcp'].includes(id);
export function sceneLayout(pages,scene) {
  const own=pages.filter(page=>(page.scene||page.owner)===scene&&!residentPage(page.id));
  return defaultLayout([...own,...pages.filter(page=>residentPage(page.id))]);
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

// REQ-WEB-013: translate the existing AvalonDock tree; only the selected page in each pane is visible.
export function desktopLayout(snapshot, available, width=1200, height=800) {
  if(snapshot?.schemaVersion!==1 || !snapshot.root) throw new Error('桌面布局版本或结构不匹配');
  const seen=new Set();
  const prune=(node,depth=0)=>{
    if(!node || depth>40)return null;
    const contents=(node.contents??[]).filter(c=>typeof c.id==='string'&&(!available||available.has(c.id)) && !snapshot.placements?.[c.id]?.hidden);
    const selected=contents.find(c=>c.id===node.selectedContentId) ?? contents[0];
    if(selected && !seen.has(selected.id)) { seen.add(selected.id);return {...node,page:selected.id}; }
    const children=(node.children??[]).map(child=>prune(child,depth+1)).filter(Boolean);
    return children.length?{...node,children}:null;
  };
  const convert=(node,w,h)=>{
    if(!node)return null;
    if(node.page)return leaf(node.page);
    const axis=node.orientation==='Vertical'?'y':'x',extent=axis==='x'?w:h;
    const lengths=node.children.map(child=>child[axis==='x'?'dockWidth':'dockHeight']);
    const pixel=lengths.reduce((sum,l)=>sum+(l?.unit==='Pixel'?Math.max(1,Number(l.value)||1):0),0);
    const stars=lengths.reduce((sum,l)=>sum+(l?.unit==='Pixel'?0:Math.max(.01,Number(l?.value)||1)),0);
    const remaining=Math.max(extent-pixel,stars*48);
    const weights=lengths.map(l=>l?.unit==='Pixel'?Math.max(1,Number(l.value)||1):remaining*Math.max(.01,Number(l?.value)||1)/(stars||1));
    const total=weights.reduce((a,b)=>a+b,0);
    const trees=node.children.map((child,i)=>convert(child,axis==='x'?w*weights[i]/total:w,axis==='y'?h*weights[i]/total:h));
    const combine=index=>{
      if(index===trees.length-1)return trees[index];
      const rest=weights.slice(index).reduce((a,b)=>a+b,0);
      return split(axis,weights[index]/rest,trees[index],combine(index+1));
    };
    return combine(0);
  };
  return normalizeLayout(convert(prune(snapshot.root),width,height),available);
}

export function seedDesktopLayouts(saved,history,pages,width,height) {
  const scenes=new Set(pages.map(p=>p.scene||p.owner)),errors=[];
  saved.desktopHistory ??= {};
  for(const [scene,snapshot]of Object.entries(history?.layouts??{})) {
    if(!scenes.has(scene)||saved.desktopHistory[scene])continue;
    // The local console survives a failed native-page registration. Do not consume
    // the migration until Aurora's shared page descriptions are available again.
    if(scene==='HistoryAurora'&&!pages.some(p=>(p.scene||p.owner)===scene&&p.id!=='console'))continue;
    const existing=pageIds(saved.layouts[scene]);
    // Migrate the obsolete console-only Aurora seed once. Real browser arrangements stay authoritative.
    const legacy=scene==='HistoryAurora' && existing.length===1 && existing[0]==='console';
    if(Object.hasOwn(saved.layouts,scene)&&!legacy) { saved.desktopHistory[scene]=true;continue; }
    let tree;
    try {tree=desktopLayout(snapshot,null,width,height);}
    catch {errors.push(`${scene}：桌面布局历史版本或结构无效`);continue;}
    if(!tree)continue;
    saved.layouts[scene]=tree;saved.desktopHistory[scene]=true;
    saved.homes[scene]={};
    saved.active[scene]=pageIds(tree).includes(snapshot.activeContentId)?snapshot.activeContentId:pageIds(tree)[0];
  }
  return errors;
}
