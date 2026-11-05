export const allCommands='全部';
export const noCommandClass='无类';
export const commandName=entry=>String(entry.commandName??entry.name??'');
export const commandDomain=entry=>String(entry.domain??commandName(entry).split('.')[0]);
export function commandClass(entry) {
  const parts=commandName(entry).split('.');
  const value=Object.hasOwn(entry,'commandClass')?entry.commandClass:Object.hasOwn(entry,'class')?entry.class:parts.length>2?parts[1]:'';
  return String(value??'').trim()||noCommandClass;
}
const same=(a,b)=>String(a).toLowerCase()===String(b).toLowerCase();
const sortedUnique=values=>[...new Map(values.filter(Boolean).map(value=>[value.toLowerCase(),value])).values()].sort((a,b)=>a<b?-1:a>b?1:0);
export function catalogDomains(catalog) {return [allCommands,...sortedUnique(catalog.map(commandDomain))];}
export function catalogClasses(catalog,domain) {
  if(!domain||domain===allCommands)return [allCommands];
  const classes=sortedUnique(catalog.filter(entry=>same(commandDomain(entry),domain)).map(commandClass));
  return [allCommands,...classes.filter(value=>value!==noCommandClass),...classes.filter(value=>value===noCommandClass)];
}
export function normalizeCatalogFilter(model,catalog) {
  const domain=catalogDomains(catalog).find(value=>same(value,model.domain??allCommands))??allCommands;
  const commandClass=catalogClasses(catalog,domain).find(value=>same(value,model.commandClass??allCommands))??allCommands;
  Object.assign(model,{domain,commandClass});
}
export function setCatalogFilter(model,field,value,catalog) {
  const next={domain:model.domain,commandClass:model.commandClass};normalizeCatalogFilter(next,catalog);
  const choices=field==='domain'?catalogDomains(catalog):catalogClasses(catalog,next.domain);
  const canonical=choices.find(choice=>same(choice,value));
  if(!canonical)throw new Error(`${field==='domain'?'日志域':'命令类'}不存在：${value}；可用项：${choices.join(' / ')}`);
  next[field]=canonical;normalizeCatalogFilter(next,catalog);Object.assign(model,next);return canonical;
}
