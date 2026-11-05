import {commandText,payload} from './protocol.js';
export async function requestCommand(text,{signal}={}) {
  let response;
  try {response=await fetch('/api/command',{method:'POST',headers:{'Content-Type':'application/json','X-Aurora-Web':'1'},body:JSON.stringify({text}),signal});}
  catch(error){if(error.name==='AbortError')throw error;throw new Error('无法连接 Aurora Web 服务');}
  let result;
  try {result=await response.json();}catch{throw new Error(`网关返回无效响应（${response.status}）`);}
  if(!response.ok||result.success===false)throw new Error(result.message||`连接失败 ${response.status}`);
  return result;
}
// Data reads never dispatch UI commands in this browser or forward its credentials to JavaScript.
export async function invoke(name,args,options) {return payload(await requestCommand(commandText(name,args),options));}
