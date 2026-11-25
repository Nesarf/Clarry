// Clarry - host half of the optional DSH button.
//
// Clarry is a standalone program: core/src/Clarry.Server serves the theatre and
// core/src/Clarry.Desktop puts a window on it. This half is a shell and nothing more --
// the world lives in the C# server, and this side only names the plugin so the client half
// has somewhere to register.
//
// Nothing here is required to run Clarry. If the DSH button never loads, the theatre still runs.

export const name = 'clarry';

/** Services this half needs from the host. */
export const inject = ['tools'];

export function apply(ctx) {
  ctx.logger?.info?.('clarry: host half loaded');
}