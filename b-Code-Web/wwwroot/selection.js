import {validatePanel,widgetKind} from './panel-model.js';
import {walkPageNodes} from './page-tree.js';

const key=value=>String(value??'').toLowerCase();
export function selectionRow(selection,channel) {
  if(!selection||typeof channel!=='string')return undefined;
  if(Object.hasOwn(selection,channel))return selection[channel];
  const name=Object.keys(selection).find(name=>key(name)===key(channel));
  return name===undefined?undefined:selection[name];
}
export function selectionBinding(value,reference=false) {
  if(typeof value!=='string')return null;
  const unwrapped=value.trim().replace(/^\{|\}$/g,''),binding=reference?unwrapped.replace(/^selection\./i,''):unwrapped,cut=binding.lastIndexOf('.');
  return cut>0&&cut<binding.length-1?{channel:binding.slice(0,cut),column:binding.slice(cut+1)}:null;
}

// Build declarations before any page mounts: visibility and window order cannot choose a publisher.
export function selectionRegistry(pages,actions=new Map()) {
  const declarations=new Map(),references=new Map(),conflicts=[];
  const reference=(channel,origin)=>{
    if(typeof channel!=='string'||!channel.trim())return;
    const id=key(channel);let entry=references.get(id);
    if(!entry)references.set(id,entry={channel,origins:new Set()});entry.origins.add(origin);
  };
  const placeholders=(value,origin)=>{
    if(typeof value!=='string')return;
    for(const match of value.matchAll(/\{selection\.([^{}]+)\}/gi)){
      const binding=selectionBinding(match[1]);if(binding)reference(binding.channel,origin);
    }
  };
  const args=(values,origin)=>{for(const value of Object.values(values??{}))placeholders(value,origin);};
  const declare=(source,page,origin)=>{
    if(typeof source.channel!=='string'||!source.channel.trim())return;
    const id=key(source.channel),previous=declarations.get(id);
    if(previous){conflicts.push({channel:source.channel,pageId:page.id,origin,
      message:`通道 ${source.channel} 已由 ${previous.origin} 声明，${origin} 的声明被忽略`});return;}
    declarations.set(id,{channel:source.channel,owner:page.owner,origin,pageId:page.id,source});
  };
  const visit=(node,page,path)=>{
    if(!node||typeof node!=='object')return;
    const type=key(node.type),origin=`${page.owner}/${page.id}#${node.id??path}`;
    args(node.dataSource?.args,origin);args(node.imageSource?.args,origin);
    if(['switch','chattimeline'].includes(type)&&node.source){const binding=selectionBinding(node.source,true);if(binding)reference(binding.channel,origin);}
    if(['table','conversationlist'].includes(type))declare(node,page,origin);
    if(['panel','popup'].includes(type)) {
      try{validatePanel(node);}catch{return;}
      for(const row of node.rows)for(const widget of row.widgets){
        const widgetOrigin=`${origin}/${widget.id??widget.action??widget.kind}`;
        if(['textbox','choices','switch'].includes(widgetKind(widget.kind)))declare(widget,page,widgetOrigin);
        const binding=selectionBinding(widget.follows);if(binding)reference(binding.channel,widgetOrigin);
        reference(widget.enabledWhen?.selected,widgetOrigin);
        args(widget.optionsSource?.args,widgetOrigin);placeholders(widget.selectCommand,widgetOrigin);
      }
    }
  };
  for(const page of pages)walkPageNodes(page.content,(node,path)=>visit(node,page,path));
  for(const action of actions.values())args(action.args,`动作 ${action.id}（${action.owner??''}）`);
  return {
    declarations,conflicts,
    accepts(channel,source){return declarations.get(key(channel))?.source===source;},
    snapshot(selection){
      const channels=[...declarations.values()].map(({source,...entry})=>({...entry,
        hasSelection:selectionRow(selection,entry.channel)!=null,row:selectionRow(selection,entry.channel)??null,
        referencedBy:[...(references.get(key(entry.channel))?.origins??[])].sort()})).sort((a,b)=>a.channel.localeCompare(b.channel));
      const dangling=[...references].filter(([id])=>!declarations.has(id)).flatMap(([,entry])=>[...entry.origins].map(origin=>`${origin} → ${entry.channel}（没有声明这个通道）`)).sort();
      return {channels,dangling,conflicts:conflicts.map(({message})=>message)};
    }
  };
}
export function selectionReport(registry,selection) {
  const data=registry.snapshot(selection);
  const lines=data.channels.map(entry=>`  ${entry.channel}\n      声明: ${entry.origin}\n      选中: ${entry.hasSelection?'有':'无'}\n      引用: ${entry.referencedBy.join(' / ')||'(无人引用)'}`);
  if(data.dangling.length)lines.push(`\n${data.dangling.length} 条断链引用:\n${data.dangling.join('\n')}`);
  if(data.conflicts.length)lines.push(`\n${data.conflicts.length} 条重复声明:\n${data.conflicts.join('\n')}`);
  return {success:true,message:lines.length?`共 ${data.channels.length} 条选择通道:\n${lines.join('\n')}`:'(没有任何组件声明选择通道)',data};
}
