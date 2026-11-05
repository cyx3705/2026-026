// REQ-WEB-030: fixed desktop row metrics allow bounded DOM without changing
// the complete row set, its identity, selection, or data-source revision.
export function createTableViewport({scroll,identity,makeRow,selectedKey,onNavigate,onChange,mutate}) {
  const directRowLimit=200,overscan=6;
  let rows=[],indices=new Map(),views=new Map(),body,frame,frameWindow,disposed=false;
  const metrics=()=>{
    const style=scroll.ownerDocument.defaultView.getComputedStyle(scroll);
    return {row:parseFloat(style.getPropertyValue('--table-row-height'))||30,
      header:scroll.querySelector('thead')?.getBoundingClientRect().height||parseFloat(style.getPropertyValue('--control-height'))||28};
  };
  function schedule() {
    if(disposed||frame!==undefined)return;
    frameWindow=scroll.ownerDocument.defaultView;frame=frameWindow.requestAnimationFrame(()=>{frame=undefined;render();});
  }
  function selection() {
    for(const [key,view]of views) {
      const selected=key===selectedKey();view.tr.setAttribute('aria-selected',String(selected));
      view.tr.tabIndex=selected||selectedKey()===undefined&&view.index===0?0:-1;
    }
  }
  function spacer(count,height) {
    const row=scroll.ownerDocument.createElement('tr'),cell=scroll.ownerDocument.createElement('td');
    row.className='table-spacer';row.setAttribute('aria-hidden','true');row.setAttribute('role','presentation');
    cell.colSpan=Math.max(1,body.closest('table').tHead?.rows[0]?.cells.length??1);cell.style.height=`${count*height}px`;
    row.append(cell);return row;
  }
  function render() {
    if(disposed||!body)return;
    const {row:height,header}=metrics(),virtual=rows.length>directRowLimit;
    const top=Math.min(scroll.scrollTop,Math.max(0,header+rows.length*height-scroll.clientHeight));
    const first=virtual?Math.max(0,Math.floor(Math.max(0,top-header)/height)-overscan):0;
    const end=virtual?Math.min(rows.length,Math.ceil((top+Math.max(height,scroll.clientHeight)-header)/height)+overscan):rows.length;
    const wanted=new Set();for(let i=first;i<end;i++)wanted.add(i);
    const focused=scroll.ownerDocument.activeElement;
    const focusedKey=[...views].find(([,view])=>view.tr.contains(focused))?.[0];
    // Keep at most the active editor/focused rows beyond the viewport. Their
    // actual indices still determine placement; they never occupy another row's slot.
    for(const [key,view]of views) {
      const index=indices.get(key);
      if(index!==undefined&&rows[index]===view.row&&(view.editing||view.tr.contains(focused)))wanted.add(index);
    }
    const next=new Map(),ordered=[];let cursor=0;
    for(const index of [...wanted].sort((a,b)=>a-b)) {
      const row=rows[index],key=identity(row,index),old=views.get(key),view=old?.row===row?old:makeRow(row,index);
      view.index=index;view.tr.setAttribute('aria-rowindex',String(index+2));
      if(index>cursor)ordered.push(spacer(index-cursor,height));
      ordered.push(view.tr);next.set(key,view);cursor=index+1;
    }
    if(cursor<rows.length)ordered.push(spacer(rows.length-cursor,height));
    // Reuse spacers as well as data rows while the viewport remains unchanged.
    ordered.forEach((node,index)=>{
      const old=body.children[index];
      if(node.classList.contains('table-spacer')&&old?.classList.contains('table-spacer')&&node.firstChild.style.height===old.firstChild.style.height)ordered[index]=old;
    });
    const changed=ordered.length!==body.children.length||ordered.some((node,index)=>node!==body.children[index]);
    if(changed)onChange();
    const left=scroll.scrollLeft;
    mutate(()=>{
      const keep=new Set(ordered);
      for(const child of [...body.children])if(!keep.has(child))child.remove();
      ordered.forEach((child,index)=>{
        if(body.children[index]===child)return;
        if(body.moveBefore&&child.isConnected&&body.isConnected)body.moveBefore(child,body.children[index]??null);
        else body.insertBefore(child,body.children[index]??null);
      });
      if(focused?.isConnected&&scroll.contains(focused)&&scroll.ownerDocument.activeElement!==focused)focused.focus({preventScroll:true});
      else if(focusedKey!==undefined&&!focused.isConnected)(next.get(focusedKey)?.tr??scroll).focus({preventScroll:true});
      scroll.scrollTop=top;scroll.scrollLeft=left;
    });
    views=next;selection();body.closest('table').setAttribute('aria-rowcount',String(rows.length+1));
  }
  function reconcile(nextRows,nextBody) {
    if(body!==nextBody){body=nextBody;views=new Map();}
    if(rows!==nextRows){rows=nextRows;indices=new Map(rows.map((row,index)=>[identity(row,index),index]));}
    render();
  }
  function navigate(index) {
    if(!rows.length)return;
    index=Math.max(0,Math.min(rows.length-1,index));const {row:height,header}=metrics();
    if(index*height<scroll.scrollTop)scroll.scrollTop=index*height;
    else if(header+(index+1)*height>scroll.scrollTop+scroll.clientHeight)scroll.scrollTop=header+(index+1)*height-scroll.clientHeight;
    render();const view=views.get(identity(rows[index],index));
    if(view){onNavigate(view);view.tr.focus({preventScroll:true});schedule();}
  }
  function keydown(event,view) {
    if(event.altKey||event.metaKey||!['ArrowUp','ArrowDown','Home','End','PageUp','PageDown'].includes(event.key))return;
    event.preventDefault();event.stopPropagation();const {row:height,header}=metrics();
    const step=Math.max(1,Math.floor((scroll.clientHeight-header)/height));
    const index=view?.index??indices.get(selectedKey())??Math.max(0,Math.floor(scroll.scrollTop/height));
    navigate(event.key==='Home'?0:event.key==='End'?rows.length-1:index+({ArrowUp:-1,ArrowDown:1,PageUp:-step,PageDown:step}[event.key]));
  }
  const scrollKey=event=>{if(event.target===scroll)keydown(event);};
  scroll.tabIndex=0;scroll.setAttribute('aria-label','表格滚动区域');
  scroll.addEventListener('scroll',schedule,{passive:true});scroll.addEventListener('focusout',schedule);scroll.addEventListener('keydown',scrollKey);
  function clear() {views.clear();rows=[];indices.clear();body=undefined;}
  return {reconcile,schedule,selection,keydown,clear,updateViews(update){mutate(()=>{for(const view of views.values())update(view);});},
    relocate(){if(frame!==undefined)frameWindow.cancelAnimationFrame(frame);frame=undefined;schedule();},
    dispose(){disposed=true;if(frame!==undefined)frameWindow.cancelAnimationFrame(frame);scroll.removeEventListener('scroll',schedule);scroll.removeEventListener('focusout',schedule);scroll.removeEventListener('keydown',scrollKey);clear();}};
}
