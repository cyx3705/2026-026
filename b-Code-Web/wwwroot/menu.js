// Browser-owned context menus, including inside the dialog top layer.
let current;
export function closeMenu(owner) {
  if (current && (!owner || current.owner === owner)) current.close();
}
export function showMenu(items, opener, {x, y, owner, report = () => {}} = {}) {
  closeMenu();
  const document=opener.ownerDocument,window=document.defaultView,{innerWidth,innerHeight}=window;
  const menu = document.createElement('div'), abort = new AbortController(), rect = opener.getBoundingClientRect();
  menu.className = 'table-menu'; menu.setAttribute('role','menu');
  const close = (focus = false) => { abort.abort(); menu.remove(); if (current?.menu === menu) current=null; if (focus && opener.isConnected) opener.focus(); };
  current={menu,owner,close};
  for (const item of items) {
    const button = document.createElement('button'); button.type='button'; button.textContent=item.title;
    button.setAttribute('role',item.checked === undefined ? 'menuitem' : 'menuitemradio');
    if (item.checked !== undefined) button.setAttribute('aria-checked',String(item.checked));
    button.disabled=!!item.disabled; button.title=item.reason ?? '';
    if (item.danger) button.classList.add('danger');
    button.onclick=() => { close(true); Promise.resolve().then(item.run).catch(report); };
    menu.append(button);
  }
  (opener.closest('dialog,[popover]') ?? document.body).append(menu);
  menu.style.left=`${Math.max(0,Math.min(x ?? rect.x,innerWidth-menu.offsetWidth))}px`;
  menu.style.top=`${Math.max(0,Math.min(y ?? rect.bottom,innerHeight-menu.offsetHeight))}px`;
  const buttons=[...menu.querySelectorAll('button:not(:disabled)')];
  (buttons.find(b => b.getAttribute('aria-checked') === 'true') ?? buttons[0])?.focus();
  const options={capture:true,signal:abort.signal};
  document.addEventListener('pointerdown',e => { if (!menu.contains(e.target)) close(); },options);
  document.addEventListener('keydown',e => {
    if (e.key === 'Escape' || e.key === 'Tab') { e.preventDefault(); e.stopPropagation(); close(true); }
    else if (['ArrowDown','ArrowUp','Home','End'].includes(e.key)) {
      e.preventDefault(); const i=buttons.indexOf(document.activeElement);
      buttons[e.key === 'Home' ? 0 : e.key === 'End' ? buttons.length-1 : (i+(e.key === 'ArrowDown' ? 1 : -1)+buttons.length)%buttons.length]?.focus();
    }
  },options);
  window.addEventListener('resize',() => close(),{signal:abort.signal});
  document.addEventListener('scroll',() => close(),options);
  return close;
}
