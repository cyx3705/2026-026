// Native aurora.ui.dialog accepts semantic values only, never HTML or styles.
export function booleanArg(value,name,fallback=false) {
  if(value===undefined)return fallback;
  if(!['true','false'].includes(String(value).toLowerCase()))throw new Error(`${name} 必须是 true 或 false`);
  return String(value).toLowerCase()==='true';
}
export function dialogRequest(args={}) {
  const kind=String(args.kind??args[0]??'message').toLowerCase();
  if(!['message','confirm','prompt','choice','content'].includes(kind))throw new Error(`不支持的 kind：${kind}`);
  let choices=[];
  if(kind==='choice'||kind==='content'&&String(args.options??'').trim()){
    try{choices=JSON.parse(args.options);}catch{throw new Error('options 不是合法的 {label,value} JSON 列表');}
    if(!Array.isArray(choices)||!choices.length)throw new Error('choice 弹窗的 options 不能为空');
    choices=choices.map((item,index)=>{
      if(!item||typeof item!=='object'||Array.isArray(item))throw new Error(`options[${index}] 必须同时提供非空 label 和 value`);
      const fields=Object.fromEntries(Object.entries(item).map(([key,value])=>[key.toLowerCase(),value]));
      if(typeof fields.label!=='string'||!fields.label.trim()||typeof fields.value!=='string'||!fields.value.trim())
        throw new Error(`options[${index}] 必须同时提供非空 label 和 value`);
      return {label:fields.label,value:fields.value};
    });
  }
  const timeout=Number(args.timeout??0);
  if(!Number.isSafeInteger(timeout)||timeout<-2147483648||timeout>2147483647)throw new Error('timeout 必须是有效整数');
  return {kind,title:String(args.title??''),body:String(args.body??''),content:String(args.content??''),value:String(args.value??''),choices,
    primary:String(args.primary??'确定'),cancel:String(args.cancel??'取消'),danger:booleanArg(args.danger,'danger'),defaultCancel:booleanArg(args.defaultcancel,'defaultcancel'),timeout};
}
