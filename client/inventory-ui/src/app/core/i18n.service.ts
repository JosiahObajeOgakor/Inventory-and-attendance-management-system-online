import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

const ATTRS = ['placeholder', 'aria-label', 'title', 'alt'] as const;
export interface Language { code: string; name: string; }

/**
 * English ↔ Igbo without touching any screen: like the desktop app's Lang.ApplyByText, it walks the page and swaps any text that EXACTLY matches a
 * catalogued English string (so a sentence with a name or number in it is never mangled). A MutationObserver keeps new content translated, and
 * switching back restores the original text, but only where Angular hasn't changed that text in the meantime.
 */
@Injectable({ providedIn: 'root' })
export class I18n {
  private readonly http = inject(HttpClient);
  readonly languages = signal<Language[]>([{ code: 'en', name: 'English' }, { code: 'ig', name: 'Igbo' }]);
  readonly code = signal<string>(read());
  private map = new Map<string, string>();
  private textDone = new Map<Text, { orig: string; set: string }>();
  private attrDone = new Map<Element, Map<string, { orig: string; set: string }>>();
  private observer: MutationObserver | null = null;
  private pending = new Set<Node>();
  private scheduled = false;

  /** Called once at startup: fetch the catalogue for the saved language and start watching the page. */
  async init(): Promise<void> {
    try { const l = await firstValueFrom(this.http.get<Language[]>('/api/i18n/languages')); if (l.length) this.languages.set(l); } catch { /* the built-in list is fine */ }
    await this.set(this.code(), false);
    this.observer = new MutationObserver(m => this.onMutations(m));
    this.observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: [...ATTRS] });
  }

  async set(code: string, save = true): Promise<void> {
    this.restore();
    this.map = new Map();
    if (code !== 'en') {
      try { this.map = new Map(Object.entries(await firstValueFrom(this.http.get<Record<string, string>>(`/api/i18n/${code}`)))); } catch { code = 'en'; }
    }
    this.code.set(code);
    document.documentElement.lang = code === 'ig' ? 'ig' : 'en';
    if (save) try { localStorage.setItem('lang', code); } catch { /* private mode */ }
    if (this.map.size) this.translate(document.body);
  }

  private onMutations(list: MutationRecord[]) {
    if (!this.map.size) return;
    for (const m of list) {
      if (m.type === 'characterData' && m.target.nodeType === Node.TEXT_NODE) {
        const done = this.textDone.get(m.target as Text);
        if (done && (m.target as Text).nodeValue === done.set) continue;      // our own change
        if (done) this.textDone.delete(m.target as Text);                     // Angular rewrote it: treat as fresh text
        this.pending.add(m.target);
      } else if (m.type === 'attributes') this.pending.add(m.target);
      else m.addedNodes.forEach(n => this.pending.add(n));
    }
    if (!this.scheduled) { this.scheduled = true; queueMicrotask(() => { this.scheduled = false; const nodes = [...this.pending]; this.pending.clear(); this.quietly(() => nodes.forEach(n => n.isConnected && this.translate(n))); }); }
  }

  private quietly(fn: () => void) {
    this.observer?.disconnect();
    try { fn(); } finally { this.observer?.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: [...ATTRS] }); }
  }

  private translate(root: Node) {
    const run = () => {
      if (root.nodeType === Node.TEXT_NODE) { this.text(root as Text); return; }
      if (root.nodeType !== Node.ELEMENT_NODE && root.nodeType !== Node.DOCUMENT_FRAGMENT_NODE) return;
      const w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
      let n: Node | null = root.nodeType === Node.ELEMENT_NODE ? root : w.nextNode();
      while (n) {
        if (n.nodeType === Node.TEXT_NODE) this.text(n as Text);
        else { const el = n as Element; if (el.tagName === 'SCRIPT' || el.tagName === 'STYLE' || el.hasAttribute('data-no-i18n')) { n = w.nextSibling() ?? w.nextNode(); continue; } this.attrs(el); }
        n = w.nextNode();
      }
    };
    this.observer ? this.quietly(run) : run();
  }

  private text(t: Text) {
    const raw = t.nodeValue ?? ''; const key = raw.trim();
    if (!key || this.textDone.has(t)) return;
    const tr = this.map.get(key); if (!tr) return;
    const set = raw.replace(key, tr);
    this.textDone.set(t, { orig: raw, set }); t.nodeValue = set;
  }

  private attrs(el: Element) {
    for (const a of ATTRS) {
      const v = el.getAttribute(a); if (!v) continue;
      const done = this.attrDone.get(el)?.get(a); if (done && v === done.set) continue;
      const tr = this.map.get(v.trim()); if (!tr) continue;
      let m = this.attrDone.get(el); if (!m) this.attrDone.set(el, (m = new Map()));
      m.set(a, { orig: v, set: tr }); el.setAttribute(a, tr);
    }
  }

  /** Puts every translated caption back, but only where nothing else has changed it since. */
  private restore() {
    this.quietly(() => {
      for (const [t, d] of this.textDone) if (t.isConnected && t.nodeValue === d.set) t.nodeValue = d.orig;
      for (const [el, m] of this.attrDone) if (el.isConnected) for (const [a, d] of m) if (el.getAttribute(a) === d.set) el.setAttribute(a, d.orig);
    });
    this.textDone.clear(); this.attrDone.clear();
  }
}

function read(): string { try { const c = localStorage.getItem('lang'); return c === 'ig' ? 'ig' : 'en'; } catch { return 'en'; } }
