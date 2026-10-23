import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface FieldDef { key: string; label: string; type: string; required: boolean; options: string[] | null; lookup: string | null; help: string | null; inList: boolean; default: string | null; readOnly: boolean; }
export interface StatusDef { name: string; tone: string; next: string[]; }
export interface ActionDef { key: string; label: string; inStatuses: string[] | null; confirm: string | null; primary: boolean; prompt: string | null; }
export interface KindDef { key: string; label: string; plural: string; parent: string | null; fields: FieldDef[]; statuses: StatusDef[]; actions: ActionDef[] | null; hint: string | null; noCreate: boolean; noDelete: boolean; }
export interface AppDef { key: string; name: string; permission: string; intro: string; kinds: KindDef[]; public: string | null; }
export interface Card { label: string; value: string; sub: string | null; tone: string; }
export interface Overview { def: AppDef; cards: Card[]; publicUrl: string | null; }
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type Data = Record<string, any>;
export interface RecordRow { id: number; kind: string; parentId: number | null; title: string; status: string; code: string | null; date: string | null; amount: number | null; data: Data;
  labels: Record<string, string>; flags: string[]; createdBy: string | null; createdAt: string; updatedAt: string; }
export interface ActionResult { message: string | null; url: string | null; }
/** Something a digital marketer asked to put live, waiting for an admin. */
export interface Approval { app: string; appName: string; id: number; kind: string; title: string; what: string; submittedBy: string; submittedAt: string; content: { label: string; value: string }[]; }
/** The shared records screen's API: every record-keeping app is reached the same way. */
@Injectable({ providedIn: 'root' })
export class EngineApi {
  private readonly http = inject(HttpClient);
  overview(app: string) { return firstValueFrom(this.http.get<Overview>(`/api/apps/${app}`)); }
  list(app: string, kind: string, o: { parent?: number | null; status?: string; q?: string } = {}) {
    const p: Record<string, string> = {};
    if (o.parent) p['parent'] = String(o.parent); if (o.status) p['status'] = o.status; if (o.q) p['q'] = o.q;
    return firstValueFrom(this.http.get<RecordRow[]>(`/api/apps/${app}/${kind}`, { params: new HttpParams({ fromObject: p }) }));
  }
  get(app: string, id: number) { return firstValueFrom(this.http.get<RecordRow>(`/api/apps/${app}/records/${id}`)); }
  create(app: string, kind: string, body: { parentId: number | null; fields: Data }) { return firstValueFrom(this.http.post<{ id: number }>(`/api/apps/${app}/${kind}`, body)); }
  update(app: string, kind: string, id: number, body: { parentId: number | null; fields: Data }) { return firstValueFrom(this.http.put<{ id: number }>(`/api/apps/${app}/${kind}/${id}`, body)); }
  setStatus(app: string, id: number, status: string) { return firstValueFrom(this.http.post<void>(`/api/apps/${app}/records/${id}/status`, { status })); }
  action(app: string, id: number, key: string, text: string | null) { return firstValueFrom(this.http.post<ActionResult>(`/api/apps/${app}/records/${id}/actions/${key}`, { text })); }
  remove(app: string, id: number) { return firstValueFrom(this.http.delete<void>(`/api/apps/${app}/records/${id}`)); }
  /** Names to pick from (customers, products): id and name only. */
  options(kind: 'customer' | 'product') { return firstValueFrom(this.http.get<{ id: number; name: string }[]>(`/api/apps/options/${kind}`)); }
  approvals() { return firstValueFrom(this.http.get<Approval[]>('/api/apps/approvals')); }
  approve(app: string, id: number) { return firstValueFrom(this.http.post<ActionResult>(`/api/apps/${app}/records/${id}/approve`, {})); }
  reject(app: string, id: number, text: string) { return firstValueFrom(this.http.post<void>(`/api/apps/${app}/records/${id}/reject`, { text })); }

  figures() { return firstValueFrom(this.http.get<{ names: { name: string; label: string }[]; values: Record<string, number> }>('/api/apps/spreadsheet/figures')); }
}
