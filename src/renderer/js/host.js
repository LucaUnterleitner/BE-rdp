// Window for session tabs that were dragged out of the main window.
import { mountTabStrip } from './tabstrip.js';

mountTabStrip({
  strip: document.getElementById('tabstrip'),
  area: document.getElementById('session-area'),
  showHome: false,
  onChange: (m) => {
    const active = m.tabs.find((t) => t.id === m.active);
    document.title = active ? `${active.name} – BearingPoint Remote Desktop` : 'BearingPoint Remote Desktop';
  },
});
