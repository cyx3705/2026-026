import {el,button,isVisible,bindDocumentEvent} from './dom.js';
import {bindArgs} from './protocol.js';
import {bindSnapshot} from './data-view.js';
import {attachments,imageBytes,rasterBlob} from './chat-model.js';
import {selectionRow} from './selection.js';

export function renderConversations(node,context,hooks) {
  const box=el('section',undefined,'conversation-list');box.setAttribute('aria-label','会话列表');
  const list=el('div'),notice=el('p','暂时没有会话','chat-empty');box.append(list,notice);
  let selected,firstId;
  const paint=()=>{for(const card of list.children)card.setAttribute('aria-pressed',String(card.dataset.id===selected));};
  bindSnapshot(node,box,context,hooks,{rows:true,
    onClear(){list.replaceChildren();notice.textContent='暂时没有会话';notice.hidden=false;if(node.channel)hooks.publish(node.channel,undefined);},
    onError(error){notice.textContent=error.message;if(node.channel)hooks.publish(node.channel,undefined);},
    onData(rows){
      const previous=selectionRow(hooks.selection(),node.channel),top=box.scrollTop;
      selected=previous?.id;
      list.replaceChildren(...rows.map(row=>{
        const card=button('',()=>{selected=row.id;if(node.channel)hooks.publish(node.channel,row);paint();},hooks.report);
        card.className='conversation-card';card.dataset.id=row.id;card.title=row.title??'';
        const body=el('span',undefined,'conversation-body'),tail=el('span',undefined,'conversation-tail'),summary=el('span',undefined,'conversation-summary');
        body.append(el('strong',row.title??''),el('span',row.last??row.subtitle??''));
        summary.append(el('span',`${row.source??''} ${row.kind??''}`));
        if(Number(row.unread)>0)summary.append(el('span',row.unread,'unread-badge'));
        tail.append(el('small',row.when??''),summary);
        card.append(el('span',String(row.title||'?').slice(0,1),'avatar'),body,tail);return card;
      }));
      const current=rows.find(row=>row.id===selected);
      if(previous&&node.channel)hooks.publish(node.channel,current);
      paint();notice.hidden=rows.length>0;
      box.scrollTop=firstId&&rows[0]?.id!==firstId?0:top;firstId=rows[0]?.id;
    }
  });
  return box;
}

export function renderChat(node,context,hooks) {
  const box=el('section',undefined,'chat-view'),heading=el('header',undefined,'chat-heading');
  const title=el('h3',node.text??'消息'),subtitle=el('p','选择左侧会话开始聊天');heading.append(title,subtitle);
  const frame=el('div',undefined,'chat-frame'),viewport=el('div',undefined,'chat-viewport'),messages=el('div'),notice=el('p','选择左侧会话开始聊天','chat-empty');
  viewport.tabIndex=0;viewport.setAttribute('aria-label','消息记录');
  const latest=button('回到最新 ↓',()=>{viewport.scrollTop=viewport.scrollHeight;checkRead();},hooks.report);latest.className='chat-latest';latest.hidden=true;
  viewport.append(messages);frame.append(viewport,notice,latest);box.append(heading,frame);
  let disposed=false,epoch=0,rows=[],acceptedSelection={},follow=true,preview,frameRequest,frameWindow;
  let imageObserver,imageAbort=new AbortController(),queue=[],running=0;
  const urls=new Set(),watermarks=new Map(),pendingReads=new Set();
  const ref=node.source?.replace(/^\{|\}$/g,'').replace(/^selection\./i,'');
  const channel=ref?.slice(0,ref.lastIndexOf('.')),column=ref?.slice(ref.lastIndexOf('.')+1);
  const sourceRow=selection=>selectionRow(selection,channel);
  const updateHeading=()=>{const row=sourceRow(hooks.selection());title.textContent=row?.[column]??node.text??'消息';subtitle.textContent=row?.subtitle??(row?'':'选择左侧会话开始聊天');};
  context.followers.push(updateHeading);updateHeading();
  const bottom=()=>viewport.scrollHeight-viewport.clientHeight-viewport.scrollTop<24;
  const scheduleRead=()=>{frameWindow?.cancelAnimationFrame(frameRequest);frameWindow=box.ownerDocument.defaultView;frameRequest=frameWindow.requestAnimationFrame(()=>checkRead());};
  function checkRead(){
    if(disposed||!node.readAction||!rows.length||box.ownerDocument.hidden||!isVisible(box)||!bottom()||!viewport.clientHeight)return;
    const through=Math.max(0,...rows.map(row=>Number(row.ts_ms)||0)),identity=sourceRow(acceptedSelection)?.id??JSON.stringify(acceptedSelection);
    if(!Number.isSafeInteger(through)||!through||through<=(watermarks.get(identity)??0)||pendingReads.has(identity))return;
    pendingReads.add(identity);
    // Selection and watermark are fixed before the asynchronous command starts.
    const selection=structuredClone(acceptedSelection);
    void hooks.action(node.readAction,{}, {readThrough:through},selection).then(()=>watermarks.set(identity,through))
      .catch(error=>{if(!disposed)hooks.report(error);}).finally(()=>pendingReads.delete(identity));
  }
  viewport.onscroll=()=>{follow=bottom();latest.hidden=follow;scheduleRead();};
  const resize=new ResizeObserver(()=>{if(follow)viewport.scrollTop=viewport.scrollHeight;scheduleRead();});resize.observe(viewport);
  function resetImages(){
    ++epoch;imageAbort.abort();imageAbort=new AbortController();imageObserver?.disconnect();queue=[];
    for(const url of urls)URL.revokeObjectURL(url);urls.clear();preview?.close();preview=null;
  }
  const clear=()=>{resetImages();rows=[];messages.replaceChildren();notice.textContent='选择左侧会话开始聊天';notice.hidden=false;latest.hidden=true;};
  bindSnapshot(node,box,context,hooks,{rows:true,
    onClear:clear,
    onError(error){notice.textContent=error.message;},
    onData(next,selection,changed){
      const atBottom=follow||changed,top=viewport.scrollTop;
      resetImages();rows=next;acceptedSelection=selection;notice.hidden=!!rows.length;notice.textContent='暂时没有消息';
      messages.replaceChildren(...rows.map(row=>message(row,selection)));
      follow=atBottom;viewport.scrollTop=atBottom?viewport.scrollHeight:top;latest.hidden=atBottom;
      imageObserver=new IntersectionObserver(entries=>{
        for(const entry of entries)if(entry.isIntersecting&&isVisible(box)){
          imageObserver.unobserve(entry.target);queue.push(entry.target);pumpImages();
        }
      },{root:viewport,rootMargin:'100px'});
      for(const image of messages.querySelectorAll('.chat-image[data-path]'))imageObserver.observe(image);
      scheduleRead();
    }
  });
  function message(row,selection){
    const item=el('article',undefined,`chat-message ${row.direction==='out'?'out':'in'}`),bubble=el('div',undefined,'chat-bubble');
    let images=[];try{images=attachments(row);}catch(error){bubble.append(el('p',error.message,'notice'));}
    if(images.length)bubble.classList.add('has-images');
    if(row.text)bubble.append(el('p',row.text,'chat-text'));
    for(const image of images){
      const view=el('button',image.path?'正在加载图片…':'原件不可用','chat-image');view.type='button';view.setAttribute('aria-label',image.name);view.title=image.name;
      view.image=image;view.selection=selection;
      if(image.path)view.dataset.path=image.path;
      else view.disabled=true;
      view.onclick=()=>{if(view.dataset.failed){delete view.dataset.failed;queue.push(view);pumpImages();}else void openImage(view);};bubble.append(view);
    }
    if(row.attachment_summary)bubble.append(el('p',row.attachment_summary,'chat-attachment'));
    if(row.media_hint)bubble.append(el('p',row.media_hint,'chat-attachment'));
    item.append(el('small',`${row.who??''} ${row.time??''}`),bubble);return item;
  }
  async function loadBlob(view,size,signal){
    const source=node.imageSource;
    const descriptor=hooks.catalog().find(c=>(c.commandName??c.name)===source?.command);
    if(!source||descriptor?.readonly!==true)throw new Error('图片来源必须是已注册的只读指令');
    const args=bindArgs(source.args,{'image.path':view.image.path},{},view.selection);
    const data=await hooks.invoke(source.command,args,{signal});
    if(signal.aborted)throw new DOMException('已取消','AbortError');
    const info=imageBytes(data?.base64);return rasterBlob(info,size);
  }
  function pumpImages(){
    while(running<2&&queue.length&&!disposed&&isVisible(box)){
      const view=queue.shift(),ticket=epoch,signal=imageAbort.signal;running++;
      void loadBlob(view,360,signal).then(blob=>{
        if(disposed||ticket!==epoch||signal.aborted)return;
        const url=URL.createObjectURL(blob);urls.add(url);const image=el('img');image.src=url;image.alt=view.image.name;
        image.onload=()=>{if(follow)viewport.scrollTop=viewport.scrollHeight;};view.replaceChildren(image);
      }).catch(error=>{if(ticket===epoch&&!disposed&&error.name!=='AbortError'){view.textContent=`${error.message}（点击重试）`;view.dataset.failed='true';}})
        .finally(()=>{running--;pumpImages();});
    }
  }
  async function openImage(view){
    const ticket=epoch,signal=imageAbort.signal,body=el('div',undefined,'chat-image-preview');
    const status=el('p','正在加载图片…');body.append(status);preview=hooks.dialog(view.image.name,body);
    const modal=preview;
    try {
      const blob=await loadBlob(view,1600,signal);
      if(disposed||ticket!==epoch||!modal.open)return;
      const url=URL.createObjectURL(blob),image=el('img');image.src=url;image.alt=view.image.name;body.replaceChildren(image);
      modal.addEventListener('close',()=>URL.revokeObjectURL(url),{once:true});
    }catch(error){if(!disposed&&ticket===epoch&&modal.open)status.textContent=error.message;}
  }
  const visible=new ResizeObserver(()=>{if(isVisible(box)){pumpImages();scheduleRead();}});visible.observe(box);
  const onVisibility=()=>{if(!box.ownerDocument.hidden){pumpImages();scheduleRead();}};bindDocumentEvent(box,'visibilitychange',onVisibility,context);
  context.disposers.push(()=>{disposed=true;resetImages();resize.disconnect();visible.disconnect();frameWindow?.cancelAnimationFrame(frameRequest);});
  return box;
}
