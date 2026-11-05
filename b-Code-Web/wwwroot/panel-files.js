import {validatePanel} from './panel-model.js';
import {validateNodeTree} from './page-tree.js';

export function panelFilePages(snapshot,registered=[]) {
  if(!Array.isArray(snapshot?.panels)||!Array.isArray(snapshot.errors)||snapshot.errors.some(error=>typeof error!=='string'))throw new Error('独立面板列表结构无效');
  const pages=[],errors=[...snapshot.errors],ids=new Set(registered.filter(page=>!page.standalonePanel).map(page=>page.id.toLowerCase()));
  for(const definition of snapshot.panels){
    try {
      if(typeof definition?.id!=='string'||!definition.id.trim()||typeof definition.title!=='string'||!definition.title.trim())throw new Error('面板 id 或标题缺失');
      const id=definition.id.toLowerCase();if(ids.has(id))throw new Error(`面板 id 冲突：${definition.id}`);
      const content={type:'panel',id:definition.id,text:definition.title,rows:definition.rows};
      validateNodeTree(content);validatePanel(content);
      const side=String(definition.side??'right').toLowerCase(),ratio=Number(definition.ratio??.22);
      pages.push({id:definition.id,title:definition.title,owner:'HistoryAurora',scene:'HistoryAurora',standalonePanel:true,content,
        placement:{side:['left','top','bottom','center'].includes(side)?side:'right',ratio:Number.isFinite(ratio)&&ratio>0&&ratio<1?ratio:.22,visible:definition.visible!==false}});
      ids.add(id);
    } catch(error) {errors.push(`独立面板 ${definition?.id??'(未命名)'}：${error.message}`);}
  }
  return {pages,errors};
}
export async function loadPanelFiles(registered) {
  const response=await fetch('/api/panels',{headers:{'X-Aurora-Web':'1'}});
  if(!response.ok)throw new Error('无法读取独立面板文件');
  return panelFilePages(await response.json(),registered);
}
