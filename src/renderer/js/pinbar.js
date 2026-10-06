// Bar at the top of a full-screen session. It shrinks to a thin handle after a few seconds and opens
// again when the pointer touches it or it gets keyboard focus.

const bar = document.getElementById('pinbar');
let timer = null;
let collapsed = false;

function setCollapsed(on) {
  if (collapsed === on) return;
  collapsed = on;
  bar.classList.toggle('pinbar--collapsed', on);
  window.rdp.pinbar.action(on ? 'collapse' : 'expand').catch(() => {});
}

function scheduleCollapse() {
  clearTimeout(timer);
  timer = setTimeout(() => { if (!bar.matches(':hover') && !bar.contains(document.activeElement)) setCollapsed(true); }, 3000);
}

bar.addEventListener('mouseenter', () => { clearTimeout(timer); setCollapsed(false); });
document.body.addEventListener('mouseenter', () => { clearTimeout(timer); setCollapsed(false); });
bar.addEventListener('mouseleave', scheduleCollapse);
bar.addEventListener('focusin', () => { clearTimeout(timer); setCollapsed(false); });
bar.addEventListener('focusout', scheduleCollapse);
bar.addEventListener('click', (e) => {
  const b = e.target.closest('[data-act]');
  if (b) window.rdp.pinbar.action(b.dataset.act).catch(() => {});
});

(async () => {
  const res = await window.rdp.pinbar.state();
  const s = res && res.ok ? res.data : null;
  if (s) {
    document.getElementById('pinbar-name').textContent = s.name;
    document.getElementById('pinbar-dock').hidden = !s.canDock;
  }
  scheduleCollapse();
})();
