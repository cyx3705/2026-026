import {el,button,isVisible} from './dom.js';
import {closeMenu} from './menu.js';

let current;
export function closePopup(owner) {if(current&&(!owner||current.owner===owner))current.close();}

// REQ-WEB-029: the same panel instance opens at its button/pointer on desktop,
// and uses the existing full-screen history/focus host on a narrow viewport.
export function renderPopup(node,context,{panel,dialog,report,active}) {
  const trigger=(node.trigger??'button').trim().toLowerCase(),contextual=trigger==='context',title=node.text||'更多';
  if(contextual&&context.contextPopup)return el('p',`弹出层 ${node.id??title} 声明了 trigger=context，但本页已经接了一个；右键只有一次，第二个不会被接上`,'notice');
  const warning=trigger&&!['button','context'].includes(trigger)?`弹出层 ${node.id??title} 的 trigger=${node.trigger} 无法识别，按 button 处理；只支持 button / context`:'';
  if(warning)report(warning);
  const content=panel(node,context),surface=el('div',undefined,'page-popup');
  surface.setAttribute('popover','auto');surface.setAttribute('role','dialog');surface.setAttribute('aria-label',title);
  surface.id=`popup-${crypto.randomUUID()}`;surface.append(content);
  const opener=contextual?context.element:button(title,()=>open(),report);
  if(warning)opener.title=warning;
  if(!contextual){opener.setAttribute('aria-haspopup','dialog');opener.setAttribute('aria-expanded','false');opener.setAttribute('aria-controls',surface.id);}
  if(!contextual&&node.style?.toLowerCase()==='accent')opener.classList.add('popup-accent');
  let modal,abort,observer,previousFocus;
  const expanded=()=>typeof surface.showPopover==='function'&&surface.matches(':popover-open');
  if(!contextual) {
    // Light dismiss runs between pointerdown and click. Remember the original
    // toggle intent so clicking the opener closes rather than immediately reopens.
    let pointerToggle=false;
    opener.addEventListener('pointerdown',()=>{pointerToggle=expanded();});
    opener.addEventListener('click',event=>{
      const closing=event.detail>0&&pointerToggle;pointerToggle=false;
      if(closing){event.preventDefault();event.stopImmediatePropagation();close();restoreFocus();}
    },true);
  }
  const close=()=>{
    if(modal?.open)modal.close();modal=undefined;
    if(expanded())surface.hidePopover();
    surface.remove();abort?.abort();abort=undefined;observer?.disconnect();observer=undefined;
    if(current?.surface===surface)current=undefined;
    if(!contextual)opener.setAttribute('aria-expanded','false');
  };
  const restoreFocus=()=>{if(previousFocus?.isConnected)previousFocus.focus({preventScroll:true});};
  const open=(event)=>{
    if(!active()||!isVisible(context.element))return;
    if(!event&&(modal?.open||expanded())){close();restoreFocus();return;}
    closePopup();closeMenu();
    const doc=context.element.ownerDocument,view=doc.defaultView;
    previousFocus=doc.activeElement;
    if(view.matchMedia('(max-width:700px)').matches||!surface.showPopover) {
      modal=dialog(title,content);const opened=modal;
      modal.addEventListener('close',()=>{if(modal!==opened)return;modal=undefined;if(current?.surface===surface)current=undefined;if(!contextual)opener.setAttribute('aria-expanded','false');},{once:true});
    } else {
      surface.append(content);(opener.closest('dialog')??doc.body).append(surface);surface.showPopover();
      const position=()=>{
        const rect=opener.getBoundingClientRect(),bounds=surface.getBoundingClientRect(),x=event?.clientX??rect.left;
        let y=event?.clientY??rect.bottom;
        if(!event&&y+bounds.height>view.innerHeight-4)y=rect.top-bounds.height;
        surface.style.left=`${Math.max(4,Math.min(x,view.innerWidth-bounds.width-4))}px`;
        surface.style.top=`${Math.max(4,Math.min(y,view.innerHeight-bounds.height-4))}px`;
      };
      position();observer=new view.ResizeObserver(position);observer.observe(surface);
      abort=new AbortController();
      view.addEventListener('resize',close,{signal:abort.signal});
      // Portalled panel menus remain in this popover's top layer (menu.js).
      surface.querySelector('input,textarea,button,[tabindex="0"]')?.focus({preventScroll:true});
    }
    current={surface,owner:context,close};
    if(!contextual)opener.setAttribute('aria-expanded','true');
  };
  surface.addEventListener('toggle',event=>{
    if(event.newState==='closed'&&surface.isConnected&&!expanded())close();
  });
  surface.addEventListener('keydown',event=>{
    if(event.key==='Escape'&&!event.defaultPrevented){event.preventDefault();event.stopPropagation();close();restoreFocus();}
  });
  if(contextual) {
    context.contextPopup={open};
    const rightClick=event=>{
      if(event.defaultPrevented||event.target.closest('input,textarea,select,[contenteditable="true"]'))return;
      event.preventDefault();open(event);
    };
    const keyboard=event=>{
      if(event.defaultPrevented||!(event.key==='ContextMenu'||event.key==='F10'&&event.shiftKey)
        ||event.target.closest('input,textarea,select,[contenteditable="true"]'))return;
      event.preventDefault();const rect=context.element.getBoundingClientRect();open({clientX:rect.x+8,clientY:rect.y+8});
    };
    context.element.tabIndex=0;
    context.element.addEventListener('contextmenu',rightClick);
    context.element.addEventListener('keydown',keyboard);
    context.disposers.push(()=>{context.element.removeEventListener('contextmenu',rightClick);context.element.removeEventListener('keydown',keyboard);});
  }
  (context.relocators??=[]).push(()=>{close();context.element.ownerDocument.adoptNode(surface);context.element.ownerDocument.adoptNode(content);});
  (context.revealers??=[]).push(()=>{if(!isVisible(context.element))close();});
  context.disposers.push(close);
  if(contextual){const hidden=el('span');hidden.hidden=true;return hidden;}
  return opener;
}
