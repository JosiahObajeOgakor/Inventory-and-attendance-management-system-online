import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

// ---- Time Off
export interface LeaveType { id: number; name: string; daysPerYear: number; paid: boolean; isActive: boolean; }
export interface LeaveRequest { id: number; employeeId: number; employee: string; leaveTypeId: number; leaveType: string; paid: boolean; fromDate: string; toDate: string; days: number;
  reason: string | null; status: string; requestedBy: string | null; decidedBy: string | null; decidedAt: string | null; decisionNote: string | null; createdAt: string; }
export interface LeaveBalance { employeeId: number; employee: string; leaveTypeId: number; leaveType: string; entitled: number; taken: number; pending: number; left: number | null; }
export interface TimeOff { year: number; types: LeaveType[]; requests: LeaveRequest[]; balances: LeaveBalance[]; awayToday: { employeeId: number; employee: string; leaveType: string; toDate: string }[]; pendingCount: number; }
export interface UnpaidRow { employeeId: number; employee: string; days: number; }

/** Time Off. */
@Injectable({ providedIn: 'root' })
export class AppsApi {
  private readonly http = inject(HttpClient);
  private get<T>(u: string, p: Record<string, string | number | undefined> = {}) {
    const clean = Object.fromEntries(Object.entries(p).filter(([, v]) => v !== undefined && v !== '')) as Record<string, string | number>;
    return firstValueFrom(this.http.get<T>(u, { params: new HttpParams({ fromObject: clean }) }));
  }
  private post<T = void>(u: string, body: unknown = {}) { return firstValueFrom(this.http.post<T>(u, body)); }

  timeOff(year?: number, status?: string) { return this.get<TimeOff>('/api/time-off', { year, status }); }
  saveLeaveType(t: { id: number | null; name: string; daysPerYear: number; paid: boolean; isActive: boolean }) { return this.post<{ id: number }>('/api/time-off/types', t); }
  requestLeave(r: { employeeId: number; leaveTypeId: number; fromDate: string; toDate: string; reason: string | null }) { return this.post<{ id: number }>('/api/time-off/requests', r); }
  decideLeave(id: number, approve: boolean, note: string | null) { return this.post(`/api/time-off/requests/${id}/decision`, { approve, note }); }
  cancelLeave(id: number) { return this.post(`/api/time-off/requests/${id}/cancel`); }
  unpaidLeave(year: number, month: number) { return this.get<UnpaidRow[]>(`/api/time-off/unpaid/${year}/${month}`); }
}