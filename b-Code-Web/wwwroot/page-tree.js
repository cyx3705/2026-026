const containers=new Set(['stack','grid','switch']);
const supported=new Set(['text','panel','popup','table','conversationlist','chattimeline','swimlane',...containers]);
const retired=new Map([['button','按钮'],['input','输入框'],['select','下拉框']]);
const record=value=>value!==null&&typeof value==='object'&&!Array.isArray(value);
export const nodeKind=node=>typeof node?.type==='string'?node.type.toLowerCase():'';

// Validate structure before replacing any page from the module. Semantic panel
// validation remains with the panel, so one invalid panel keeps its explicit error.
export function validateNodeTree(node,path='content',depth=0) {
  if(!record(node))throw new Error(`${path} 必须是节点对象`);
  if(depth>48)throw new Error(`${path} 页面树嵌套过深`);
  const string=(value,name)=>{if(value!=null&&typeof value!=='string')throw new Error(`${path}.${name} 必须是文本`);};
  const array=(value,name,visit)=>{
    if(value==null)return;
    if(!Array.isArray(value))throw new Error(`${path}.${name} 必须是数组`);
    value.forEach((item,index)=>visit(item,`${path}.${name}[${index}]`));
  };
  for(const name of ['type','id','text','style','orientation','gap','channel','readAction','source','case','trigger'])string(node[name],name);
  if(node.fill!=null&&typeof node.fill!=='boolean')throw new Error(`${path}.fill 必须是布尔值`);
  for(const name of ['dataSource','imageSource'])if(node[name]!=null){
    const source=node[name];if(!record(source))throw new Error(`${path}.${name} 必须是对象`);
    for(const field of ['command','deltaCommand','rowKey'])string(source[field],`${name}.${field}`);
    if(source.args!=null&&(!record(source.args)||Object.values(source.args).some(value=>value!=null&&typeof value!=='string')))
      throw new Error(`${path}.${name}.args 必须是文本字典`);
  }
  array(node.children,'children',(child,location)=>validateNodeTree(child,location,depth+1));
  for(const name of ['columns','rowActions','rows'])array(node[name],name,(item,location)=>{
    if(!record(item))throw new Error(`${location} 必须是对象`);
    if(name==='rows'){
      if(item.widgets!=null&&!Array.isArray(item.widgets))throw new Error(`${location}.widgets 必须是数组`);
      for(const [index,widget]of (item.widgets??[]).entries())if(!record(widget))throw new Error(`${location}.widgets[${index}] 必须是对象`);
    }
  });
}

// Visit exactly the nodes the renderer can reach. An unsupported node is a
// placeholder, so its unused children must not create phantom missing components.
export function walkPageNodes(node,visit,path='root') {
  if(!record(node))return;
  visit(node,path);
  if(containers.has(nodeKind(node)))for(const [index,child]of (node.children??[]).entries())walkPageNodes(child,visit,`${path}.${index}`);
}
export function nodeIssue(node) {
  const type=nodeKind(node);
  if(supported.has(type))return null;
  if(retired.has(type))return {retired:true,message:`${retired.get(type)}（${type}）不再是页面节点：小型交互控件请放进控制面板（panel / popup）`};
  const component=node?.type?.trim()?node.type:'(未命名)';
  return {component,message:`该组件待交付：${component}`};
}
export function missingReport(pages) {
  const groups=new Map();
  for(const page of pages)walkPageNodes(page.content,node=>{
    const issue=nodeIssue(node);if(!issue?.component)return;
    const key=issue.component.toLowerCase();
    if(!groups.has(key))groups.set(key,{component:issue.component,users:new Map()});
    const name=`${page.owner}/${page.id}`;groups.get(key).users.set(name.toLowerCase(),name);
  });
  const data=[...groups.values()].map(group=>({component:group.component,users:[...group.users.values()].sort()})).sort((a,b)=>a.component.localeCompare(b.component));
  return {success:true,message:data.length?data.map(group=>`${group.component} ← ${group.users.join('、')}`).join('\n'):'无缺件',data};
}
