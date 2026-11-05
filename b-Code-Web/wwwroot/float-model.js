// Float intent is independent of scenes and named docking layouts, like desktop.
export const floatStorageKey='aurora.web.floats.v1';
export function floatRecords(value) {
  const result=Object.create(null);
  if(!value||typeof value!=='object'||Array.isArray(value))return result;
  for(const [id,entry]of Object.entries(value)) {
    if(!/^[a-z0-9_.-]+$/.test(id)||!entry||typeof entry.floating!=='boolean')continue;
    const {left,top,width,height}=entry;
    if(![left,top,width,height].every(Number.isFinite))continue;
    result[id]={left:Math.round(left),top:Math.round(top),width:Math.max(240,Math.min(1600,Math.round(width))),
      height:Math.max(160,Math.min(1200,Math.round(height))),floating:entry.floating};
  }
  return result;
}
export function floatBounds(record,screen) {
  const left=screen.availLeft??0,top=screen.availTop??0,width=Math.max(240,screen.availWidth),height=Math.max(160,screen.availHeight);
  const w=Math.min(record?.width??760,width),h=Math.min(record?.height??460,height);
  return {left:Math.round(Math.max(left,Math.min(record?.left??left+48,left+width-w))),
    top:Math.round(Math.max(top,Math.min(record?.top??top+48,top+height-h))),width:w,height:h};
}
