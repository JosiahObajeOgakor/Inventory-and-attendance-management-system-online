import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Appearance, AppearanceSettings, PALETTES, TEMPLATES } from '../../core/appearance.service';
import { I18n } from '../../core/i18n.service';

/** Appearance and language. Everything applies instantly and is remembered in this browser. The preview below uses the real styles. */
@Component({
  selector: 'app-appearance',
  imports: [],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-head"><h1>Appearance</h1><div class="actions"><button type="button" class="btn" (click)="ap.reset()">Back to defaults</button></div></div>
      <p class="muted lede">Pick a design, a colour, and how dense tables look. These choices are saved on this device only.</p>

      <section class="card card-pad">
        <h2 class="display sub">Design</h2>
        <div class="tpl" role="radiogroup" aria-label="Design template">
          @for (t of templates; track t.id) {
            <button type="button" role="radio" class="tile" [class.on]="s().template === t.id" [attr.aria-checked]="s().template === t.id" (click)="ap.update({ template: t.id })">
              <span class="mini" [style.background]="t.ground" aria-hidden="true">
                <i class="r" [style.background]="t.rail"></i>
                <b class="c" [style.background]="t.card" [style.border-radius.px]="t.radius"><em [style.background]="'var(--brand)'"></em><em></em><em></em></b>
              </span>
              <strong>{{ t.name }}</strong><small>{{ t.note }}</small>
            </button>
          }
        </div>
      </section>

      <div class="cols">
        <section class="card card-pad form">
          <div class="field"><span class="lab" id="pl">Colour</span>
            <div class="sw" role="radiogroup" aria-labelledby="pl">
              @for (p of palettes; track p.id) {
                <button type="button" role="radio" [attr.aria-checked]="s().palette === p.id" [class.on]="s().palette === p.id" (click)="ap.update({ palette: p.id })"><i [style.background]="p.swatch"></i>{{ p.name }}</button>
              }</div></div>
          <div class="field"><label for="tx">Text size</label>
            <select id="tx" class="input" [value]="s().text" (change)="ap.update({ text: $any($event.target).value })"><option value="small">Small</option><option value="normal">Normal</option><option value="large">Large</option></select></div>
          <div class="field"><label for="dn">Table density</label>
            <select id="dn" class="input" [value]="s().density" (change)="ap.update({ density: $any($event.target).value })"><option value="compact">Compact</option><option value="comfortable">Comfortable</option><option value="spacious">Spacious</option></select></div>
          <div class="field"><label for="ln">Table lines</label>
            <select id="ln" class="input" [value]="s().lines" (change)="ap.update({ lines: $any($event.target).value })"><option value="none">No grid lines</option><option value="horizontal">Horizontal lines</option><option value="full">Full grid</option></select></div>
          <label class="check"><input type="checkbox" [checked]="s().stripes" (change)="ap.update({ stripes: $any($event.target).checked })" /> Striped rows</label>
          <label class="check"><input type="checkbox" [checked]="s().boldHeaders" (change)="ap.update({ boldHeaders: $any($event.target).checked })" /> Bold column headers</label>
          <label class="check"><input type="checkbox" [checked]="s().rowNumbers" (change)="ap.update({ rowNumbers: $any($event.target).checked })" /> Show row numbers in tables</label>
          <label class="check"><input type="checkbox" [checked]="s().reduceEffects" (change)="ap.update({ reduceEffects: $any($event.target).checked })" /> Reduce card shadows</label>

          <div class="field"><label for="lg">Language</label>
            <select id="lg" class="input" [value]="i18n.code()" (change)="i18n.set($any($event.target).value)">@for (l of i18n.languages(); track l.code) { <option [value]="l.code">{{ l.name }}</option> }</select>
            <span class="muted sm">Igbo is machine-assisted and should be reviewed by a native speaker. An administrator can correct it without a rebuild (see the deployment notes).</span></div>
        </section>

        <section class="card card-pad" aria-label="Preview">
          <span class="eyebrow">Preview</span>
          <div class="table-wrap"><table class="table">
            <thead><tr><th>Customer</th><th>Date</th><th class="num">Total</th><th>Status</th></tr></thead>
            <tbody>
              <tr><td class="strong">PetMart Lagos</td><td>12 Sep 2026</td><td class="num mono">₦24,725.00</td><td><span class="stamp stamp-ok">Paid</span></td></tr>
              <tr><td class="strong">Companion Pets Abuja</td><td>14 Sep 2026</td><td class="num mono">₦23,000.00</td><td><span class="stamp stamp-warn">Partial</span></td></tr>
              <tr><td class="strong">Shore Feeds</td><td>18 Sep 2026</td><td class="num mono">₦80,000.00</td><td><span class="stamp stamp-bad">Unpaid</span></td></tr>
            </tbody>
          </table></div>
          <p><button type="button" class="btn btn-primary">Save sale</button> <button type="button" class="btn">Cancel</button></p>
        </section>
      </div>
    </div>`,
  styles: `
    .lede { margin: 0 0 1rem; max-width: 46rem; } .cols { display: grid; grid-template-columns: minmax(0, 26rem) minmax(0, 1fr); gap: 1rem; align-items: start; margin-top: 1rem; } .form { display: flex; flex-direction: column; gap: 1rem; }
    .sub { font-size: 1.4rem; margin: 0 0 .8rem; } .lab { font: 600 .75rem var(--font-body); letter-spacing: .04em; color: var(--ink-3); } .sm { font-size: .75rem; }
    .tpl { display: grid; grid-template-columns: repeat(auto-fill, minmax(11rem, 1fr)); gap: .8rem; }
    .tile { display: flex; flex-direction: column; gap: .3rem; text-align: left; border: 1.5px solid var(--line-strong); background: var(--paper); border-radius: var(--r-2); padding: .55rem; cursor: pointer; font: inherit; }
    .tile:hover { border-color: var(--brand); } .tile.on { border-color: var(--brand); box-shadow: 0 0 0 3px var(--brand-tint); } .tile small { color: var(--muted); font-size: .72rem; line-height: 1.3; }
    .mini { display: flex; height: 5.2rem; border-radius: 6px; overflow: hidden; border: 1px solid var(--line); margin-bottom: .3rem; }
    .mini .r { flex: 0 0 26%; } .mini .c { flex: 1; margin: .45rem; padding: .35rem; display: flex; flex-direction: column; gap: .25rem; border: 1px solid #0001; }
    .mini em { display: block; height: .32rem; border-radius: 2px; background: #cfd6dc; } .mini em:first-child { width: 55%; }
    .sw { display: flex; flex-wrap: wrap; gap: .5rem; margin-top: .4rem; } .sw button { display: inline-flex; align-items: center; gap: .45rem; border: 1px solid var(--line-strong); background: #fff; border-radius: 99px; padding: .3rem .75rem .3rem .35rem; cursor: pointer; font: inherit; }
    .sw button.on { border-color: var(--brand); box-shadow: 0 0 0 2px var(--brand-tint); font-weight: 600; } .sw i { width: 1.1rem; height: 1.1rem; border-radius: 50%; display: inline-block; }
    @media (max-width: 900px) { .cols { grid-template-columns: minmax(0, 1fr); } }
  `,
})
export class AppearancePage {
  protected readonly ap = inject(Appearance);
  protected readonly i18n = inject(I18n);
  protected readonly palettes = PALETTES;
  protected readonly templates = TEMPLATES;
  protected s(): AppearanceSettings { return this.ap.settings(); }
}
