import { Injectable, signal } from '@angular/core';

export const PALETTES = [
  { id: 'company', name: 'Match the business', swatch: 'linear-gradient(90deg,#1f6b4f 50%,#6a2c5b 50%)' },
  { id: 'forest', name: 'Forest', swatch: '#1f6b4f' }, { id: 'plum', name: 'Plum', swatch: '#6a2c5b' },
  { id: 'ocean', name: 'Ocean', swatch: '#1d6fa5' }, { id: 'royal', name: 'Royal blue', swatch: '#3b4fbf' },
  { id: 'teal', name: 'Teal', swatch: '#0f766e' }, { id: 'lime', name: 'Lime', swatch: '#4d7c0f' },
  { id: 'sunset', name: 'Sunset', swatch: '#c2410c' }, { id: 'crimson', name: 'Crimson', swatch: '#b91c1c' },
  { id: 'rose', name: 'Rose', swatch: '#be185d' }, { id: 'violet', name: 'Violet', swatch: '#6d28d9' },
  { id: 'gold', name: 'Gold', swatch: '#a16207' }, { id: 'graphite', name: 'Graphite', swatch: '#3b4656' },
] as const;
export type Palette = (typeof PALETTES)[number]['id'];

/** Whole-look presets: they change the page ground, the side menu, corner rounding and heading style; colour is chosen separately. */
export const TEMPLATES = [
  { id: 'stockdesk', name: 'Stock desk', note: 'The original: dark menu, stencil headings.', rail: '#16202a', ground: '#e8ece9', card: '#fbfcfb', radius: 6 },
  { id: 'clean', name: 'Clean light', note: 'Bright menu, calm modern headings.', rail: '#ffffff', ground: '#f3f5f7', card: '#ffffff', radius: 10 },
  { id: 'mist', name: 'Soft mist', note: 'Pale blue-grey, generously rounded.', rail: '#eef2fa', ground: '#eaf0f8', card: '#ffffff', radius: 16 },
  { id: 'paper', name: 'Warm paper', note: 'Cream ground, gentle and easy on the eyes.', rail: '#fbf6ec', ground: '#f3ecdd', card: '#fffdf8', radius: 8 },
  { id: 'compact', name: 'Compact pro', note: 'Square corners and tight spacing for power users.', rail: '#1c2130', ground: '#e9ebef', card: '#ffffff', radius: 2 },
  { id: 'contrast', name: 'High contrast', note: 'Black menu, strong borders, easiest to read.', rail: '#000000', ground: '#ffffff', card: '#ffffff', radius: 4 },
] as const;
export type Template = (typeof TEMPLATES)[number]['id'];

export interface AppearanceSettings {
  template: Template; palette: Palette; text: 'small' | 'normal' | 'large'; density: 'compact' | 'comfortable' | 'spacious';
  lines: 'none' | 'horizontal' | 'full'; stripes: boolean; boldHeaders: boolean; rowNumbers: boolean; reduceEffects: boolean;
}

export const DEFAULTS: AppearanceSettings = { template: 'stockdesk', palette: 'company', text: 'normal', density: 'comfortable', lines: 'horizontal', stripes: false, boldHeaders: false, rowNumbers: false, reduceEffects: false };

/** Look-and-feel choices (the desktop "Appearance" window, plus colour palettes and design templates). Stored in this browser only. */
@Injectable({ providedIn: 'root' })
export class Appearance {
  readonly settings = signal<AppearanceSettings>(load());

  init(): void { this.apply(); }

  update(patch: Partial<AppearanceSettings>): void {
    this.settings.update(s => ({ ...s, ...patch }));
    this.apply();
    try { localStorage.setItem('appearance', JSON.stringify(this.settings())); } catch { /* private mode: still applied for this visit */ }
  }

  reset(): void { this.update({ ...DEFAULTS }); }

  private apply(): void {
    const s = this.settings(); const h = document.documentElement.dataset;
    h['template'] = s.template; h['palette'] = s.palette; h['text'] = s.text; h['density'] = s.density; h['lines'] = s.lines;
    h['stripes'] = s.stripes ? 'on' : 'off'; h['boldheads'] = s.boldHeaders ? 'on' : 'off'; h['rownums'] = s.rowNumbers ? 'on' : 'off'; h['flat'] = s.reduceEffects ? 'on' : 'off';
  }
}

function load(): AppearanceSettings {
  try {
    const raw = JSON.parse(localStorage.getItem('appearance') ?? '{}') as Partial<AppearanceSettings>;
    const pick = <T extends string>(v: unknown, ok: readonly T[], d: T): T => (ok.includes(v as T) ? (v as T) : d);
    return {
      template: pick(raw.template, TEMPLATES.map(t => t.id), DEFAULTS.template), palette: pick(raw.palette, PALETTES.map(p => p.id), DEFAULTS.palette),
      text: pick(raw.text, ['small', 'normal', 'large'] as const, DEFAULTS.text), density: pick(raw.density, ['compact', 'comfortable', 'spacious'] as const, DEFAULTS.density),
      lines: pick(raw.lines, ['none', 'horizontal', 'full'] as const, DEFAULTS.lines),
      stripes: raw.stripes === true, boldHeaders: raw.boldHeaders === true, rowNumbers: raw.rowNumbers === true, reduceEffects: raw.reduceEffects === true,
    };
  } catch { return { ...DEFAULTS }; }
}
