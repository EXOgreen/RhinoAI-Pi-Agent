import { bind, el } from '../core/dom.js';
import type { Child } from '../core/dom.js';
import { renderMarkdown } from '../core/markdown.js';
import type { Signal } from '../core/signal.js';
import { t } from '../i18n/t.js';
import type { PanelContext } from './context.js';

/**
 * Markdown re-rendered on the animation frame after a delta lands, so a fast stream costs one
 * render per frame rather than one per token. The Eto panel had to grow a bubble, re-measure its
 * wrapped height and re-pin its width on every chunk; here the browser does all of that.
 */
export function agentMessage(ctx: PanelContext, text: Signal<string>, streaming: () => boolean): Child {
  const host = el('div', {
    class: () => `msg-agent md${streaming() ? ' streaming' : ''}`,
    'aria-live': 'polite',
  });

  const options = { copy: ctx.copy, openLink: ctx.openLink };
  let queued = false;

  bind(() => {
    text();
    if (queued) return;
    queued = true;
    requestAnimationFrame(() => {
      queued = false;
      host.replaceChildren(renderMarkdown(text.peek(), options));
    });
  });

  return host;
}

/**
 * The agent's reasoning. Open while it is the live block so a long pause reads as activity, then
 * collapsed: the transcript is for the reply, and the thinking is there for whoever wants it.
 */
export function thoughtBlock(ctx: PanelContext, text: Signal<string>, streaming: () => boolean): Child {
  const body = el('div', { class: 'md' });

  let queued = false;
  bind(() => {
    text();
    if (queued) return;
    queued = true;
    requestAnimationFrame(() => {
      queued = false;
      body.replaceChildren(renderMarkdown(text.peek(), { copy: ctx.copy, openLink: ctx.openLink }));
    });
  });

  return el(
    'details',
    { class: 'thought', open: () => streaming() },
    el('summary', { text: () => t('thought.summary') }),
    body,
  );
}
