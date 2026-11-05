// Resolve table controls without changing the declaration or its data/column layout.
export function tableActions(node, declaration) {
  const errors=[],columns=new Map(),rows=[];
  const resolve=(id,label)=>{
    if(typeof id!=='string'||!id.trim()){errors.push(`${label}：未声明 action`);return;}
    const action=declaration(id);
    if(!action){errors.push(`${label}：未声明的动作：${id}`);return;}
    return action;
  };
  for(const column of node.columns??[]) {
    const label=`列 ${column.title??column.key}`;
    if(column.cellAction!=null&&column.cellEditAction!=null){
      errors.push(`${label}：cellAction 与 cellEditAction 不可同时声明`);
      continue;
    }
    const id=column.cellEditAction??column.cellAction;
    if(id==null)continue;
    const action=resolve(id,label);
    if(action)columns.set(column.key,{id,edit:column.cellEditAction!=null,
      style:typeof column.cellStyle==='string'?column.cellStyle.toLowerCase():undefined,danger:!!action.danger,
      summary:action.summary||action.title||`点击执行 ${id}`});
  }
  for(const spec of node.rowActions??[]) {
    const action=resolve(spec.action,spec.title||'(无标题)');
    if(action)rows.push({...spec,title:spec.title||action.title||'执行',
      danger:typeof spec.style==='string'&&spec.style.toLowerCase()==='danger'||!!action.danger,summary:action.summary});
  }
  return {columns,rows,errors};
}

export function tableArguments(args,tables) {
  return Object.fromEntries(Object.entries(args??{}).map(([key,binding])=>{
    if(!binding||typeof binding!=='object'||Array.isArray(binding))throw new Error(`行操作参数 ${key} 必须是绑定对象`);
    if(binding.value!=null)return [key,binding.value];
    const path=typeof binding.from==='string'?binding.from.split('.'):[];
    const table=path.length===3&&path[1].toLowerCase()==='selected'
      ? [...tables].find(([id])=>id.toLowerCase()===path[0].toLowerCase())?.[1]:undefined;
    const row=table?.selected(),column=path[2];
    const value=row&&Object.hasOwn(row,column)?row[column]:undefined;
    if(value==null)throw new Error(`无法取得参数：${binding.from??key}`);
    return [key,value];
  }));
}
