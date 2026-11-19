// Clarry - host half.
//
// Clarry is a Hippocras variant: a theatre in which agents play among themselves.
// The user joins as one character and is only asked to act when their character is
// addressed. No dice decide the plot; dice exist only inside a story when the agents
// themselves choose to run a tabletop session.
//
// This half stays small on purpose: it owns the world state and the tools.
// The UI lives in ../client.

export const name = 'clarry';

/** Services this half needs from the host. */
export const inject = ['tools'];

export function apply(ctx) {
  ctx.logger?.info?.('clarry: host half loaded');
}