/**
 * A small spreadsheet formula engine: numbers, text, cell references (B3), ranges (A1:A9), + − × ÷ ^, comparisons, and the functions
 * SUM, AVERAGE, MIN, MAX, COUNT, ROUND, ABS, IF and FIG("name") for a live figure from the business's records. No eval: formulas are parsed here.
 */
export type Value = number | string | boolean;
export type Fig = (name: string) => number | undefined;
export const COLS = 'ABCDEFGHIJKL'.split('');

export const keyOf = (col: number, row: number) => `${COLS[col]}${row + 1}`;

type Tok = { t: 'num' | 'str' | 'id' | 'op'; v: string };

function tokenize(src: string): Tok[] {
  const out: Tok[] = []; let i = 0;
  while (i < src.length) {
    const c = src[i];
    if (c === ' ') { i++; continue; }
    if (/[0-9.]/.test(c)) { let j = i; while (j < src.length && /[0-9.]/.test(src[j])) j++; out.push({ t: 'num', v: src.slice(i, j) }); i = j; continue; }
    if (c === '"') { let j = i + 1; while (j < src.length && src[j] !== '"') j++; if (j >= src.length) throw new Error('#ERR'); out.push({ t: 'str', v: src.slice(i + 1, j) }); i = j + 1; continue; }
    if (/[A-Za-z]/.test(c)) { let j = i; while (j < src.length && /[A-Za-z0-9_]/.test(src[j])) j++; out.push({ t: 'id', v: src.slice(i, j).toUpperCase() }); i = j; continue; }
    const two = src.slice(i, i + 2);
    if (two === '<>' || two === '<=' || two === '>=') { out.push({ t: 'op', v: two }); i += 2; continue; }
    if ('+-*/^(),:<>='.includes(c)) { out.push({ t: 'op', v: c }); i++; continue; }
    throw new Error('#ERR');
  }
  return out;
}

const REF = /^[A-L](\d{1,3})$/;

class Parser {
  private p = 0;
  constructor(private readonly toks: Tok[], private readonly cell: (key: string) => Value, private readonly fig: Fig) {}
  parse(): Value { const v = this.cmp(); if (this.p < this.toks.length) throw new Error('#ERR'); return Array.isArray(v) ? this.scalar(v) : v; }
  private peek() { return this.toks[this.p]; }
  private eat(v?: string) { const t = this.toks[this.p]; if (!t || (v !== undefined && t.v !== v)) throw new Error('#ERR'); this.p++; return t; }
  private at(v: string) { return this.toks[this.p]?.t === 'op' && this.toks[this.p].v === v; }
  private scalar(v: Value | Value[]): Value { return Array.isArray(v) ? (v[0] ?? 0) : v; }
  private num(v: Value | Value[]): number {
    const s = this.scalar(v);
    if (typeof s === 'number') return s; if (typeof s === 'boolean') return s ? 1 : 0;
    if (s === '') return 0; const n = Number(s); if (Number.isNaN(n)) throw new Error('#VALUE'); return n;
  }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private cmp(): any {
    let l = this.add();
    for (const op of ['=', '<>', '<=', '>=', '<', '>']) {
      if (this.at(op)) {
        this.eat(); const r = this.add(); const a = this.scalar(l), b = this.scalar(r);
        const x = typeof a === 'string' || typeof b === 'string' ? [String(a), String(b)] : [this.num(a), this.num(b)];
        return op === '=' ? x[0] === x[1] : op === '<>' ? x[0] !== x[1] : op === '<=' ? x[0] <= x[1] : op === '>=' ? x[0] >= x[1] : op === '<' ? x[0] < x[1] : x[0] > x[1];
      }
    }
    return l;
  }
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private add(): any {
    let l = this.mul();
    while (this.at('+') || this.at('-')) { const op = this.eat().v; const r = this.mul(); l = op === '+' ? this.num(l) + this.num(r) : this.num(l) - this.num(r); }
    return l;
  }
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private mul(): any {
    let l = this.pow();
    while (this.at('*') || this.at('/')) {
      const op = this.eat().v; const r = this.pow();
      if (op === '*') l = this.num(l) * this.num(r); else { const d = this.num(r); if (d === 0) throw new Error('#DIV/0'); l = this.num(l) / d; }
    }
    return l;
  }
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private pow(): any { const b = this.unary(); if (this.at('^')) { this.eat(); return Math.pow(this.num(b), this.num(this.pow())); } return b; }
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private unary(): any { if (this.at('-')) { this.eat(); return -this.num(this.unary()); } if (this.at('+')) { this.eat(); return this.unary(); } return this.primary(); }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private primary(): any {
    const t = this.peek(); if (!t) throw new Error('#ERR');
    if (t.t === 'num') { this.p++; return Number(t.v); }
    if (t.t === 'str') { this.p++; return t.v; }
    if (t.t === 'op' && t.v === '(') { this.eat('('); const v = this.cmp(); this.eat(')'); return v; }
    if (t.t === 'id') {
      this.p++;
      if (this.at('(')) return this.call(t.v);
      if (!REF.test(t.v)) throw new Error('#NAME');
      if (this.at(':')) { this.eat(':'); const end = this.eat().v; if (!REF.test(end)) throw new Error('#ERR'); return this.range(t.v, end); }
      return this.cell(t.v);
    }
    throw new Error('#ERR');
  }
  private range(a: string, b: string): Value[] {
    const c1 = a.charCodeAt(0) - 65, c2 = b.charCodeAt(0) - 65, r1 = Number(a.slice(1)), r2 = Number(b.slice(1)); const out: Value[] = [];
    for (let c = Math.min(c1, c2); c <= Math.max(c1, c2); c++) for (let r = Math.min(r1, r2); r <= Math.max(r1, r2); r++) out.push(this.cell(`${COLS[c]}${r}`));
    return out;
  }
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private call(name: string): any {
    this.eat('('); const args: (Value | Value[])[] = [];
    if (!this.at(')')) { args.push(this.cmp()); while (this.at(',')) { this.eat(); args.push(this.cmp()); } }
    this.eat(')');
    const nums = () => args.flatMap(a => (Array.isArray(a) ? a : [a])).filter(v => typeof v === 'number' || (typeof v === 'string' && v !== '' && !Number.isNaN(Number(v)))).map(Number);
    switch (name) {
      case 'SUM': return nums().reduce((s, x) => s + x, 0);
      case 'AVERAGE': { const n = nums(); if (!n.length) throw new Error('#DIV/0'); return n.reduce((s, x) => s + x, 0) / n.length; }
      case 'MIN': return nums().length ? Math.min(...nums()) : 0;
      case 'MAX': return nums().length ? Math.max(...nums()) : 0;
      case 'COUNT': return nums().length;
      case 'ABS': return Math.abs(this.num(args[0]));
      case 'ROUND': { const f = Math.pow(10, args[1] === undefined ? 0 : this.num(args[1])); return Math.round(this.num(args[0]) * f) / f; }
      case 'IF': { const c = this.scalar(args[0]); return (typeof c === 'boolean' ? c : this.num(c) !== 0) ? this.scalar(args[1] ?? '') : this.scalar(args[2] ?? ''); }
      case 'FIG': { const v = this.fig(String(this.scalar(args[0]))); if (v === undefined) throw new Error('#NAME'); return v; }
      default: throw new Error('#NAME');
    }
  }
}

/** The value of every non-empty cell, given what was typed. Errors are shown as #ERR, #NAME, #DIV/0, #VALUE or #CYCLE. */
export function evaluateAll(raw: Record<string, string>, fig: Fig): Record<string, Value> {
  const memo: Record<string, Value> = {}; const busy = new Set<string>();
  const get = (key: string): Value => {
    if (key in memo) return memo[key];
    const r = (raw[key] ?? '').trim();
    if (r === '') return '';
    if (!r.startsWith('=')) return (memo[key] = Number.isNaN(Number(r)) || r === '' ? raw[key] : Number(r));
    if (busy.has(key)) throw new Error('#CYCLE');
    busy.add(key);
    try { return (memo[key] = new Parser(tokenize(r.slice(1)), get, fig).parse()); }
    catch (e) { return (memo[key] = (e as Error).message.startsWith('#') ? (e as Error).message : '#ERR'); }
    finally { busy.delete(key); }
  };
  const out: Record<string, Value> = {};
  for (const k of Object.keys(raw)) { try { out[k] = get(k); } catch { out[k] = '#CYCLE'; } }
  return out;
}

export function show(v: Value | undefined): string {
  if (v === undefined || v === '') return '';
  if (typeof v === 'number') return Number.isInteger(v) ? v.toLocaleString('en-NG') : v.toLocaleString('en-NG', { maximumFractionDigits: 2 });
  if (typeof v === 'boolean') return v ? 'TRUE' : 'FALSE';
  return v;
}
