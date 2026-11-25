// Clarry - browser half.
//
// Format follows the DSH web-plugin contract: a lazy factory registered into the browser
// module table. React comes from that table; nothing is installed twice.
//
// The entry point is a fixed button in the window's top area (shell.overlay, since the
// host allocates no header slot). Clicking it opens the stage panel, which for now is the
// Clarry server's own page.

window.__ModuleLoader__.load({
  id: 'clarry',
  factory(require) {
    const React = require('react');
    const h = React.createElement;

    const SERVER = 'http://127.0.0.1:3090/';

    const styles = `
      .clarry-root { position: fixed; top: 84px; right: 20px; z-index: 60; pointer-events: none; }
      .clarry-root > * { pointer-events: auto; }
      .clarry-btn {
        display: inline-flex; align-items: center; gap: 6px;
        padding: 5px 12px; border-radius: 5px; cursor: pointer;
        font: 500 13px/1.4 system-ui, -apple-system, "Segoe UI", sans-serif;
        color: #F5EBDC; background: #2B1B4A;
        border: 1px solid #A8894A; box-shadow: 0 1px 0 #0D0F1F;
      }
      .clarry-btn:hover { border-color: #D4AF37; }
      .clarry-btn.on { background: #3A2560; border-color: #D4AF37; }
      .clarry-glyph { color: #D4AF37; font-size: 13px; }
      .clarry-stage {
        position: fixed; inset: 124px 16px 16px 16px; z-index: 59;
        border: 1px solid #A8894A; border-radius: 6px; overflow: hidden;
        background: #1A1E3A; box-shadow: 0 8px 28px rgba(0,0,0,.55);
      }
      .clarry-stage iframe { width: 100%; height: 100%; border: 0; display: block; }
      .clarry-note {
        position: absolute; left: 12px; bottom: 10px;
        font: 12px/1.4 system-ui, sans-serif; color: #A8894A;
      }
    `;

    function ClarryButton() {
      const [open, setOpen] = React.useState(false);
      const [health, setHealth] = React.useState(null);

      React.useEffect(() => {
        let alive = true;
        const tick = () => {
          fetch(SERVER + 'api/health')
            .then((r) => (r.ok ? r.json() : null))
            .then((j) => { if (alive) setHealth(j); })
            .catch(() => { if (alive) setHealth(null); });
        };
        tick();
        const id = setInterval(tick, 15000);
        return () => { alive = false; clearInterval(id); };
      }, []);

      const label = health
        ? `Clarry ${health.stage}/${health.capacity}`
        : 'Clarry';

      return h('div', { className: 'clarry-root' },
        h('style', null, styles),
        h('button', {
          className: 'clarry-btn' + (open ? ' on' : ''),
          title: health ? `世界 ${health.ok ? '在线' : '离线'}｜回合 ${health.round}` : 'Clarry（服务未响应）',
          onClick: () => setOpen((v) => !v),
        },
          h('span', { className: 'clarry-glyph' }, '\u2726'),
          h('span', null, label)
        ),
        open
          ? h('div', { className: 'clarry-stage' },
              h('iframe', { src: SERVER + 'stage', title: 'Clarry stage', allow: 'clipboard-write' }),
              h('div', { className: 'clarry-note' }, SERVER + 'stage')
            )
          : null
      );
    }

    return {
      inject: ['slots'],
      apply(ctx) {
        ctx.slots.inject('shell.overlay', () => ctx.slots.register({
          name: 'shell.overlay',
          id: 'clarry',
          order: 40,
        }, ClarryButton));
      },
    };
  },
});